using System.Security.Cryptography;
using System.Text;
using SharpNinja.Valhalla.Generations;

namespace SharpNinja.Valhalla.Tests.Generations;

public sealed class ValhallaGenerationArtifactSourceRegistryTests
{
    [Fact]
    public void FutureArtifactSource_RegistersWithoutCoreSwitchChange()
    {
        var source = new CountingMemorySource("future", Encoding.UTF8.GetBytes("generation"));
        var registry = new ValhallaGenerationArtifactSourceRegistry([source]);
        var exactUri = new Uri("future://regional/nashville/base.bin?generation=base-001");

        IValhallaGenerationArtifactSource resolved = registry.Resolve(exactUri);

        Assert.Same(source, resolved);
        Assert.Equal("future", resolved.Scheme);
    }
}

public sealed class ValhallaGenerationArtifactAcquisitionTests
{
    [Fact]
    public async Task CorruptOrCancelledArtifact_DoesNotReplaceValidGeneration()
    {
        string root = CreateRoot();
        try
        {
            string activePointer = Path.Combine(root, "active.pointer");
            await File.WriteAllTextAsync(
                activePointer,
                "previous-valid-generation",
                TestContext.Current.CancellationToken);
            byte[] corrupt = Encoding.UTF8.GetBytes("corrupt-content");
            var source = new CountingMemorySource("memory", corrupt);
            var materializer = CreateMaterializer(root, source);
            var artifact = new ValhallaGenerationArtifactIdentity(
                "base-corrupt",
                new Uri("memory://regional/base.bin"),
                Hash(Encoding.UTF8.GetBytes("expected-content")),
                corrupt.Length);

            ValhallaGenerationException exception = await Assert.ThrowsAsync<ValhallaGenerationException>(
                () => materializer.MaterializeAsync(artifact, TestContext.Current.CancellationToken));

            Assert.Equal(ValhallaGenerationFailureCode.ArtifactIntegrityMismatch, exception.Code);
            Assert.Equal("previous-valid-generation", await File.ReadAllTextAsync(activePointer, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(root, "generation.bin", SearchOption.AllDirectories));

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delayed = new CountingMemorySource(
                "delayed",
                Encoding.UTF8.GetBytes("valid-content"),
                gate.Task);
            var delayedMaterializer = CreateMaterializer(root, delayed);
            byte[] valid = Encoding.UTF8.GetBytes("valid-content");
            var delayedArtifact = new ValhallaGenerationArtifactIdentity(
                "base-delayed",
                new Uri("delayed://regional/base.bin"),
                Hash(valid),
                valid.Length);
            using var cancellation = new CancellationTokenSource();
            Task<ValhallaMaterializedArtifact> canceled = delayedMaterializer.MaterializeAsync(
                delayedArtifact,
                cancellation.Token);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            Assert.Equal("previous-valid-generation", await File.ReadAllTextAsync(activePointer, TestContext.Current.CancellationToken));
            gate.SetResult();
            ValhallaMaterializedArtifact completed = await delayedMaterializer.MaterializeAsync(
                delayedArtifact,
                TestContext.Current.CancellationToken);
            Assert.True(File.Exists(completed.LocalPath));
            Assert.Equal("previous-valid-generation", await File.ReadAllTextAsync(
                activePointer,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ConcurrentSameGeneration_IsIdempotentAndAtomicallyMaterialized()
    {
        string root = CreateRoot();
        try
        {
            byte[] content = Encoding.UTF8.GetBytes("canonical-generation-content");
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new CountingMemorySource("memory", content, gate.Task);
            var materializer = CreateMaterializer(root, source);
            var artifact = new ValhallaGenerationArtifactIdentity(
                "base-001",
                new Uri("memory://regional/exact/base.bin?generation=001"),
                Hash(content),
                content.Length);

            Task<ValhallaMaterializedArtifact>[] acquisitions = Enumerable.Range(0, 12)
                .Select(_ => materializer.MaterializeAsync(artifact, TestContext.Current.CancellationToken))
                .ToArray();
            gate.SetResult();
            ValhallaMaterializedArtifact[] materialized = await Task.WhenAll(acquisitions);

            Assert.Equal(1, source.OpenCount);
            Assert.Single(materialized.Select(item => item.LocalPath).Distinct(StringComparer.OrdinalIgnoreCase));
            Assert.All(materialized, item => Assert.Equal(Hash(content), item.ContentSha256));
            Assert.Equal(content, await File.ReadAllBytesAsync(materialized[0].LocalPath, TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(root, "generation.bin", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
            Assert.Equal(artifact.ArtifactUri, source.LastUri);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RequiredAcquisitionMatrix_IsComplete()
    {
        string root = CreateRoot();
        try
        {
            byte[] content = Encoding.UTF8.GetBytes("matrix-content");
            var source = new CountingMemorySource("custom", content);
            var registry = new ValhallaGenerationArtifactSourceRegistry([source]);
            var materializer = new ValhallaGenerationArtifactMaterializer(root, registry);
            var exactUri = new Uri("custom://regional/path/object.bin?generation=matrix-001");
            var artifact = new ValhallaGenerationArtifactIdentity(
                "matrix-001",
                exactUri,
                Hash(content),
                content.Length);

            ValhallaMaterializedArtifact first = await materializer.MaterializeAsync(artifact, TestContext.Current.CancellationToken);
            ValhallaMaterializedArtifact second = await materializer.MaterializeAsync(artifact, TestContext.Current.CancellationToken);

            Assert.Equal(exactUri, source.LastUri);
            Assert.Equal(first, second);
            Assert.Equal(1, source.OpenCount);
            Assert.True(Path.IsPathFullyQualified(first.LocalPath));
            Assert.Throws<ValhallaGenerationException>(
                () => registry.Resolve(new Uri("missing://regional/object.bin")));
            Assert.Throws<ArgumentException>(
                () => new ValhallaGenerationArtifactSourceRegistry(
                    [source, new CountingMemorySource("CUSTOM", content)]));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static ValhallaGenerationArtifactMaterializer CreateMaterializer(
        string root,
        IValhallaGenerationArtifactSource source) =>
        new(root, new ValhallaGenerationArtifactSourceRegistry([source]));

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "SharpNinja.Valhalla.Tests",
            "distributed-generations",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));
}

internal sealed class CountingMemorySource : IValhallaGenerationArtifactSource
{
    private readonly byte[] _content;
    private readonly Task _gate;
    private int _openCount;

    public CountingMemorySource(string scheme, byte[] content, Task? gate = null)
    {
        Scheme = scheme;
        _content = content;
        _gate = gate ?? Task.CompletedTask;
    }

    public string Scheme { get; }

    public int OpenCount => Volatile.Read(ref _openCount);

    public Uri? LastUri { get; private set; }

    public async ValueTask<Stream> OpenReadAsync(
        Uri artifactUri,
        CancellationToken cancellationToken)
    {
        LastUri = artifactUri;
        Interlocked.Increment(ref _openCount);
        await _gate.WaitAsync(cancellationToken);
        return new MemoryStream(_content, writable: false);
    }
}
