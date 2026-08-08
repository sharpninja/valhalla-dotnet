namespace SharpNinja.Valhalla.Traffic.Routing;

internal sealed class BoundedAsyncCache<TKey, TValue> : IDisposable
    where TKey : notnull
{
    private readonly Dictionary<TKey, Entry> _entries;
    private readonly LinkedList<Entry> _fifo = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _stateChanged = new(0, int.MaxValue);
    private readonly SemaphoreSlim _buildAdmission;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly int _capacity;
    private readonly Action<TKey, TValue>? _onEvicted;
    private int _activeOperations;
    private int _disposed;
    private int _disposedEntryResourceCount;
    private int _entryResourceCount;
    private int _infrastructureDisposed;

    public BoundedAsyncCache(
        int capacity,
        int maximumConcurrentBuilds,
        IEqualityComparer<TKey>? comparer = null,
        Action<TKey, TValue>? onEvicted = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (maximumConcurrentBuilds <= 0 || maximumConcurrentBuilds > capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentBuilds));
        }

        _capacity = capacity;
        _entries = new Dictionary<TKey, Entry>(comparer);
        _buildAdmission = new SemaphoreSlim(
            maximumConcurrentBuilds,
            maximumConcurrentBuilds);
        _onEvicted = onEvicted;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    internal int TrackedStorageCount
    {
        get
        {
            lock (_gate)
            {
                return _fifo.Count;
            }
        }
    }

    internal int DisposedEntryResourceCount =>
        Volatile.Read(ref _disposedEntryResourceCount);

    internal bool InfrastructureDisposed =>
        Volatile.Read(ref _infrastructureDisposed) != 0;

    public IReadOnlyList<TValue> CompletedValues
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values
                    .Where(static entry =>
                        entry.Work.IsValueCreated &&
                        entry.Work.Value.IsCompletedSuccessfully)
                    .Select(static entry => entry.Work.Value.Result)
                    .ToArray();
            }
        }
    }

    public async Task<TValue> GetOrAddAsync(
        TKey key,
        Func<CancellationToken, Task<TValue>> factory,
        CancellationToken cancellationToken)
    {
        using Lease lease = await AcquireAsync(key, factory, cancellationToken)
            .ConfigureAwait(false);
        return lease.Value;
    }

    public async Task<Lease> AcquireAsync(
        TKey key,
        Func<CancellationToken, Task<TValue>> factory,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeOperations);
        try
        {
            ArgumentNullException.ThrowIfNull(factory);
            while (true)
            {
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();

                Entry? entry = null;
                Entry? evictedEntry = null;
                TValue? evictedValue = default;
                TKey? evictedKey = default;
                bool invokeEviction = false;
                lock (_gate)
                {
                    if (_entries.TryGetValue(key, out Entry? existing))
                    {
                        if (!existing.RemovalRequested)
                        {
                            existing.WaiterCount++;
                            entry = existing;
                        }
                    }
                    else if (_entries.Count < _capacity ||
                             TryEvictOldestCompletedLocked(
                                 out evictedEntry,
                                 out evictedKey,
                                 out evictedValue,
                                 out invokeEviction))
                    {
                        entry = new Entry(
                            key,
                            token => BuildWithAdmissionAsync(factory, token),
                            OnEntryCancellationDisposed);
                        Interlocked.Increment(ref _entryResourceCount);
                        entry.WaiterCount = 1;
                        _entries.Add(key, entry);
                        entry.FifoNode = _fifo.AddLast(entry);
                    }
                }

                evictedEntry?.MarkUnlinked();
                if (invokeEviction)
                {
                    _onEvicted?.Invoke(evictedKey!, evictedValue!);
                }

                if (entry is not null)
                {
                    Task<TValue> work = entry.Work.Value;
                    AttachCompletion(entry, work);
                    try
                    {
                        TValue value = await work.WaitAsync(cancellationToken)
                            .ConfigureAwait(false);
                        return new Lease(this, entry, value);
                    }
                    catch
                    {
                        ReleaseWaiter(entry);
                        if (work.IsFaulted || work.IsCanceled)
                        {
                            RequestRemoval(entry);
                        }

                        throw;
                    }
                }

                await WaitForStateChangeAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeOperations);
            TryDisposeInfrastructure();
        }
    }

    public void RemoveWhere(Func<TKey, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        List<(TKey Key, TValue Value)> evicted = [];
        List<Entry> removed = [];
        List<Entry> activeEntriesToCancel = [];
        lock (_gate)
        {
            foreach (Entry entry in _entries.Values.Where(item => predicate(item.Key)).ToArray())
            {
                entry.RemovalRequested = true;
                if (CanRemove(entry))
                {
                    RemoveLocked(entry, evicted, removed);
                }
                else if (!entry.Work.IsValueCreated || !entry.Work.Value.IsCompleted)
                {
                    activeEntriesToCancel.Add(entry);
                }
            }
        }

        FinalizeRemovedEntries(removed);
        foreach (Entry entry in activeEntriesToCancel)
        {
            entry.RequestCancellation();
        }

        InvokeEvictions(evicted);
        SignalStateChanged();
        TryDisposeInfrastructure();
    }

    public void Clear()
        => RemoveWhere(static _ => true);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _lifetimeCancellation.Cancel();
        }
        catch (Exception)
        {
            // User cancellation callbacks cannot prevent deterministic cache teardown.
        }
        finally
        {
            Clear();
            TryDisposeInfrastructure();
        }
    }

    private async Task<TValue> BuildWithAdmissionAsync(
        Func<CancellationToken, Task<TValue>> factory,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        await _buildAdmission.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            return await factory(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _buildAdmission.Release();
        }
    }

    private void AttachCompletion(Entry entry, Task<TValue> work)
    {
        if (Interlocked.Exchange(ref entry.CompletionAttached, 1) != 0)
        {
            return;
        }

        _ = work.ContinueWith(
            static (_, state) =>
            {
                var completion = ((BoundedAsyncCache<TKey, TValue> Cache, Entry Entry))state!;
                completion.Cache.OnWorkCompleted(completion.Entry);
            },
            (this, entry),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnWorkCompleted(Entry entry)
    {
        List<(TKey Key, TValue Value)> evicted = [];
        List<Entry> removed = [];
        lock (_gate)
        {
            if (entry.RemovalRequested && CanRemove(entry))
            {
                RemoveLocked(entry, evicted, removed);
            }
        }

        FinalizeRemovedEntries(removed);
        InvokeEvictions(evicted);
        SignalStateChanged();
        TryDisposeInfrastructure();
    }

    private void ReleaseWaiter(Entry entry)
    {
        List<(TKey Key, TValue Value)> evicted = [];
        List<Entry> removed = [];
        lock (_gate)
        {
            if (entry.WaiterCount > 0)
            {
                entry.WaiterCount--;
            }

            if (entry.RemovalRequested && CanRemove(entry))
            {
                RemoveLocked(entry, evicted, removed);
            }
        }

        FinalizeRemovedEntries(removed);
        InvokeEvictions(evicted);
        SignalStateChanged();
        TryDisposeInfrastructure();
    }

    private void RequestRemoval(Entry entry)
    {
        List<(TKey Key, TValue Value)> evicted = [];
        List<Entry> removed = [];
        lock (_gate)
        {
            entry.RemovalRequested = true;
            if (CanRemove(entry))
            {
                RemoveLocked(entry, evicted, removed);
            }
        }

        FinalizeRemovedEntries(removed);
        InvokeEvictions(evicted);
        SignalStateChanged();
        TryDisposeInfrastructure();
    }

    private bool TryEvictOldestCompletedLocked(
        out Entry? evictedEntry,
        out TKey? evictedKey,
        out TValue? evictedValue,
        out bool invokeEviction)
    {
        evictedEntry = null;
        evictedKey = default;
        evictedValue = default;
        invokeEviction = false;
        LinkedListNode<Entry>? node = _fifo.First;
        while (node is not null)
        {
            LinkedListNode<Entry>? next = node.Next;
            Entry candidate = node.Value;
            if (!_entries.TryGetValue(candidate.Key, out Entry? current) ||
                !ReferenceEquals(candidate, current))
            {
                _fifo.Remove(node);
                candidate.FifoNode = null;
                node = next;
                continue;
            }

            if (!CanRemove(candidate))
            {
                node = next;
                continue;
            }

            _fifo.Remove(node);
            candidate.FifoNode = null;
            _entries.Remove(candidate.Key);
            evictedEntry = candidate;
            if (candidate.Work.Value.IsCompletedSuccessfully)
            {
                evictedKey = candidate.Key;
                evictedValue = candidate.Work.Value.Result;
                invokeEviction = true;
            }

            return true;
        }

        return _entries.Count < _capacity;
    }

    private void RemoveLocked(
        Entry entry,
        List<(TKey Key, TValue Value)> evicted,
        List<Entry> removed)
    {
        if (!_entries.TryGetValue(entry.Key, out Entry? current) ||
            !ReferenceEquals(entry, current))
        {
            return;
        }

        _entries.Remove(entry.Key);
        if (entry.FifoNode is { } node)
        {
            _fifo.Remove(node);
            entry.FifoNode = null;
        }

        removed.Add(entry);
        if (entry.Work.IsValueCreated &&
            entry.Work.Value.IsCompletedSuccessfully)
        {
            evicted.Add((entry.Key, entry.Work.Value.Result));
        }
    }

    private static void FinalizeRemovedEntries(IEnumerable<Entry> removed)
    {
        foreach (Entry entry in removed)
        {
            entry.MarkUnlinked();
        }
    }

    private static bool CanRemove(Entry entry)
        => entry.WaiterCount == 0 &&
           entry.Work.IsValueCreated &&
           entry.Work.Value.IsCompleted;

    private async Task WaitForStateChangeAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        try
        {
            await _stateChanged.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested &&
                  !cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    private void InvokeEvictions(IEnumerable<(TKey Key, TValue Value)> evicted)
    {
        if (_onEvicted is null)
        {
            return;
        }

        foreach ((TKey key, TValue value) in evicted)
        {
            _onEvicted(key, value);
        }
    }

    private void OnEntryCancellationDisposed()
    {
        Interlocked.Increment(ref _disposedEntryResourceCount);
        Interlocked.Decrement(ref _entryResourceCount);
        TryDisposeInfrastructure();
    }

    private void TryDisposeInfrastructure()
    {
        if (Volatile.Read(ref _disposed) == 0 ||
            Volatile.Read(ref _activeOperations) != 0 ||
            Volatile.Read(ref _entryResourceCount) != 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_entries.Count != 0 ||
                Interlocked.Exchange(ref _infrastructureDisposed, 1) != 0)
            {
                return;
            }
        }

        _lifetimeCancellation.Dispose();
        _stateChanged.Dispose();
        _buildAdmission.Dispose();
    }

    private void SignalStateChanged()
    {
        try
        {
            _stateChanged.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
            when (Volatile.Read(ref _infrastructureDisposed) != 0)
        {
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

    internal sealed class Lease : IDisposable
    {
        private BoundedAsyncCache<TKey, TValue>? _owner;
        private Entry? _entry;

        internal Lease(
            BoundedAsyncCache<TKey, TValue> owner,
            Entry entry,
            TValue value)
        {
            _owner = owner;
            _entry = entry;
            Value = value;
        }

        public TValue Value { get; }

        public void Dispose()
        {
            BoundedAsyncCache<TKey, TValue>? owner =
                Interlocked.Exchange(ref _owner, null);
            Entry? entry = Interlocked.Exchange(ref _entry, null);
            if (owner is not null && entry is not null)
            {
                owner.ReleaseWaiter(entry);
            }
        }
    }

    internal sealed class Entry
    {
        private readonly object _lifecycleGate = new();
        private readonly Action _onCancellationDisposed;
        private bool _cancellationCompleted;
        private bool _cancellationDisposed;
        private bool _cancellationStarted;
        private bool _unlinked;

        public Entry(
            TKey key,
            Func<CancellationToken, Task<TValue>> factory,
            Action onCancellationDisposed)
        {
            Key = key;
            _onCancellationDisposed = onCancellationDisposed;
            Work = new Lazy<Task<TValue>>(
                () => factory(Cancellation.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public TKey Key { get; }

        public Lazy<Task<TValue>> Work { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public LinkedListNode<Entry>? FifoNode { get; set; }

        public int WaiterCount { get; set; }

        public bool RemovalRequested { get; set; }

        public int CompletionAttached;

        public void RequestCancellation()
        {
            lock (_lifecycleGate)
            {
                if (_cancellationDisposed || _cancellationStarted)
                {
                    return;
                }

                _cancellationStarted = true;
            }

            _ = CancelCoreAsync();
        }

        public void MarkUnlinked()
        {
            bool dispose;
            lock (_lifecycleGate)
            {
                _unlinked = true;
                dispose = TryReserveCancellationDisposalLocked();
            }

            if (dispose)
            {
                DisposeCancellation();
            }
        }

        private async Task CancelCoreAsync()
        {
            try
            {
                await Cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cancellation callback failures cannot keep an invalidated cache entry alive.
            }
            finally
            {
                bool dispose;
                lock (_lifecycleGate)
                {
                    _cancellationCompleted = true;
                    dispose = TryReserveCancellationDisposalLocked();
                }

                if (dispose)
                {
                    DisposeCancellation();
                }
            }
        }

        private bool TryReserveCancellationDisposalLocked()
        {
            if (_cancellationDisposed ||
                !_unlinked ||
                (_cancellationStarted && !_cancellationCompleted))
            {
                return false;
            }

            _cancellationDisposed = true;
            return true;
        }

        private void DisposeCancellation()
        {
            Cancellation.Dispose();
            _onCancellationDisposed();
        }
    }
}
