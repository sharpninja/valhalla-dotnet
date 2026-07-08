// Faithful C# port of Valhalla baldr DoubleBucketQueue.
// Source: valhalla/baldr/double_bucket_queue.h @ Valhalla 3.7.0
//
// A form of priority queue using a bucket sort for performance. An "overflow"
// bucket holds costs outside the current low-level bucket range; those are
// migrated into the low-level buckets as needed. Each bucket stores label
// indexes into an external label container.
//
// The C++ is a header-only template `DoubleBucketQueue<label_t>` whose only
// requirement on label_t is a `float sortcost() const` accessor. This port
// constrains the type parameter to <see cref="ISortCost"/> to express that
// requirement. Algorithms, float/double field types, range/bucket math and the
// overflow-rebucketing (including the precision-correction branch) are
// reproduced exactly.

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Requirement on the label type stored in a <see cref="DoubleBucketQueue{TLabel}"/>:
/// it must expose a sort cost. Mirrors the implicit C++ requirement that
/// <c>label_t</c> provides <c>float sortcost() const</c>.
/// </summary>
public interface ISortCost
{
    /// <summary>Gets the sort cost used to bucket this label.</summary>
    float SortCost();
}

/// <summary>
/// Double Bucket Queue - a bucket-sort priority queue used by pathfinding.
/// Faithful port of <c>valhalla::baldr::DoubleBucketQueue&lt;label_t&gt;</c>.
/// </summary>
/// <typeparam name="TLabel">Label type exposing a sort cost (see <see cref="ISortCost"/>).</typeparam>
public sealed class DoubleBucketQueue<TLabel>
    where TLabel : ISortCost
{
    // Bucket = list of label indexes; buckets = list of buckets.
    // Mirrors `using bucket_t = std::vector<uint32_t>;` and `using buckets_t = std::vector<bucket_t>;`.

    private float bucketrange_;  // Total range of costs in lower level buckets
    private float bucketsize_;   // Bucket size (range of costs in same bucket)
    private float inv_;          // 1/bucketsize (so we can avoid division)
    private double mincost_;     // Minimum cost within the low level buckets
    private float maxcost_;      // Above this goes into overflow bucket
    private float currentcost_;  // Current cost

    // Low level buckets.
    private List<List<uint>> buckets_ = new();

    // Index of the current bucket within buckets_ (mirrors the C++ iterator
    // currentbucket_; an index of buckets_.Count corresponds to buckets_.end()).
    private int currentbucketindex_;

    // Overflow bucket.
    private readonly List<uint> overflowbucket_ = new();

    // Access to a container of labels to get cost given the label index.
    private IReadOnlyList<TLabel>? labelcontainer_;

    /// <summary>
    /// Default constructor: creates an empty object that must be initialized with
    /// <see cref="Reuse"/>. Mirrors the C++ default c-tor which calls
    /// <c>reuse(0.f, 1.f, 1, nullptr)</c>.
    /// </summary>
    public DoubleBucketQueue()
    {
        Reuse(0.0f, 1.0f, 1, null);
    }

    /// <summary>
    /// Constructs given a minimum cost, a cost range held within the bucket sort, and a
    /// bucket size. All costs above <c>mincost + range</c> are stored in an overflow bucket.
    /// </summary>
    /// <param name="mincost">Minimum cost. Used to create the initial range for bucket sorting.</param>
    /// <param name="range">Cost range for low-level buckets.</param>
    /// <param name="bucketsize">Bucket size (range of costs within same bucket). Must be an integer value.</param>
    /// <param name="labelcontainer">Container of labels with sort costs.</param>
    public DoubleBucketQueue(float mincost, float range, uint bucketsize, IReadOnlyList<TLabel>? labelcontainer)
    {
        Reuse(mincost, range, bucketsize, labelcontainer);
    }

    /// <summary>
    /// The same as the constructor, but without buffer reallocation where possible. Before
    /// calling this method you should clean up the current state (call <see cref="Clear"/>).
    /// </summary>
    /// <param name="mincost">Minimum cost. Used to create the initial range for bucket sorting.</param>
    /// <param name="range">Cost range for low-level buckets.</param>
    /// <param name="bucketsize">Bucket size (range of costs within same bucket). Must be an integer value.</param>
    /// <param name="labelcontainer">Container of labels with sort costs.</param>
    public void Reuse(float mincost, float range, uint bucketsize, IReadOnlyList<TLabel>? labelcontainer)
    {
        labelcontainer_ = labelcontainer;

        // We need at least a bucketsize of 1 or more.
        if (bucketsize < 1)
        {
            throw new InvalidOperationException("Bucketsize must be 1 or greater");
        }

        // We need at least a bucketrange of something larger than 0.
        if (range <= 0.0f)
        {
            throw new InvalidOperationException("Bucketrange must be greater than 0");
        }

        // Adjust min cost to be the start of a bucket.
        uint c = (uint)mincost;
        currentcost_ = c - (c % bucketsize);
        mincost_ = currentcost_;
        bucketrange_ = range;
        bucketsize_ = bucketsize;
        inv_ = 1.0f / bucketsize_;

        // Set the maximum cost (above this goes into the overflow bucket).
        maxcost_ = (float)(mincost_ + bucketrange_);

        // Allocate the low-level buckets.
        // size_t bucketcount = (range / bucketsize_) + 1; (truncates toward zero)
        int bucketcount = (int)(range / bucketsize_) + 1;
        Resize(buckets_, bucketcount);

        // Set the current bucket to the lowest cost low level bucket.
        currentbucketindex_ = 0;
    }

    /// <summary>
    /// Clears all labels from the low-level buckets and the overflow bucket. Mirrors the C++
    /// <c>clear()</c> which empties each bucket from the current bucket onward and resets state.
    /// </summary>
    public void Clear()
    {
        // Empty the overflow bucket and each bucket from current onward.
        overflowbucket_.Clear();
        while (currentbucketindex_ != buckets_.Count)
        {
            buckets_[currentbucketindex_].Clear();
            ++currentbucketindex_;
        }

        // Reset current bucket and cost.
        currentcost_ = (float)mincost_;
        currentbucketindex_ = 0;
    }

    /// <summary>
    /// Adds a label index to the bucketed sort. Adds it to the appropriate bucket given the
    /// cost. If the cost is greater than the max cost the label is placed in the overflow
    /// bucket. If the cost is less than the current bucket cost the label is placed in the
    /// current bucket to prevent underflow.
    /// </summary>
    /// <param name="label">Label index to add to the queue.</param>
    public void Add(uint label)
    {
        GetBucket(labelcontainer_![(int)label].SortCost()).Add(label);
    }

    /// <summary>
    /// Indicates the specified label index now has a smaller cost and reorders it in the sorted
    /// bucket list. Nothing happens if the old and new cost map to the same bucket.
    /// </summary>
    /// <param name="label">Label index to reorder.</param>
    /// <param name="newcost">New sort cost.</param>
    public void Decrease(uint label, float newcost)
    {
        // Get the buckets of the previous and new costs. Nothing needs to be done if the old
        // and new costs are in the same bucket.
        List<uint> prevbucket = GetBucket(labelcontainer_![(int)label].SortCost());
        List<uint> newbucket = GetBucket(newcost);
        if (!ReferenceEquals(prevbucket, newbucket))
        {
            // Add label to newbucket and remove from previous bucket.
            newbucket.Add(label);

            // Mirrors `prevbucket.erase(std::remove(prevbucket.begin(), prevbucket.end(), label));`
            // std::remove shifts the first matching element to the end and erase(it) removes a
            // single element at that returned iterator. Net effect: remove the first occurrence.
            int idx = prevbucket.IndexOf(label);
            if (idx >= 0)
            {
                prevbucket.RemoveAt(idx);
            }
        }
    }

    /// <summary>
    /// Removes the lowest cost label index from the sorted buckets.
    /// </summary>
    /// <returns>
    /// The label index of the lowest cost label, or <see cref="GraphConstants.InvalidLabel"/>
    /// if the buckets are empty.
    /// </returns>
    public uint Pop()
    {
        if (Empty())
        {
            // No labels found in the low-level buckets.
            if (overflowbucket_.Count == 0)
            {
                // Return an invalid label if no labels are in the overflow buckets.
                // Reset currentbucket to the last bucket - in case another access of the
                // adjacency list is done.
                --currentbucketindex_;
                return GraphConstants.InvalidLabel;
            }
            else
            {
                // Move labels from the overflow bucket to the low level buckets.
                // Return invalid label if still empty.
                EmptyOverflow();
                if (Empty())
                {
                    return GraphConstants.InvalidLabel;
                }
            }
        }

        // Return label from lowest non-empty bucket.
        List<uint> bucket = buckets_[currentbucketindex_];
        uint label = bucket[bucket.Count - 1];
        bucket.RemoveAt(bucket.Count - 1);
        return label;
    }

    /// <summary>
    /// Returns the bucket the cost lies within. Mirrors the C++ ternary in <c>get_bucket</c>:
    /// below current cost goes to the current bucket; below max cost indexes a low-level
    /// bucket; otherwise the overflow bucket.
    /// </summary>
    private List<uint> GetBucket(float cost)
    {
        if (cost < currentcost_)
        {
            return buckets_[currentbucketindex_];
        }

        if (cost < maxcost_)
        {
            return buckets_[(int)(uint)((cost - mincost_) * inv_)];
        }

        return overflowbucket_;
    }

    /// <summary>
    /// Increments the current bucket through the low-level buckets until a non-empty bucket is
    /// found. Returns true if the low-level buckets are all empty.
    /// </summary>
    private bool Empty()
    {
        while (currentbucketindex_ != buckets_.Count && buckets_[currentbucketindex_].Count == 0)
        {
            ++currentbucketindex_;
            currentcost_ += bucketsize_;
        }

        return currentbucketindex_ == buckets_.Count;
    }

    /// <summary>
    /// Empties the overflow bucket by placing the label indexes into the low level buckets.
    /// Faithfully reproduces the min-element scan, range adjustment (including the precision
    /// correction branch), and the partition of in-range labels back into the buckets.
    /// </summary>
    private void EmptyOverflow()
    {
        // Get the minimum label so we can figure out where the new range should be.
        // Mirrors std::min_element with the sortcost comparator.
        bool hasMin = false;
        int minItr = -1;
        float minItrCost = 0.0f;
        for (int i = 0; i < overflowbucket_.Count; i++)
        {
            float cost = labelcontainer_![(int)overflowbucket_[i]].SortCost();
            if (!hasMin || cost < minItrCost)
            {
                hasMin = true;
                minItr = i;
                minItrCost = cost;
            }
        }

        // If there is actually stuff to move.
        if (hasMin)
        {
            // Adjust cost range so smallest element is in the buckets_.
            // C++: mincost_ += (std::floor((min - mincost_) / bucketrange_)) * bucketrange_;
            // mincost_ is double and min/bucketrange_ are float, so the whole expression is
            // evaluated in double precision (no intermediate float cast).
            float min = labelcontainer_![(int)overflowbucket_[minItr]].SortCost();
            mincost_ += Math.Floor((min - mincost_) / bucketrange_) * bucketrange_;

            // Avoid precision issues.
            if (mincost_ > min)
            {
                mincost_ -= bucketrange_;
            }
            else if (mincost_ + bucketrange_ < min)
            {
                mincost_ += bucketrange_;
            }

            maxcost_ = (float)(mincost_ + bucketrange_);

            // Move elements within the range from overflow to buckets. Mirrors
            // std::remove_if: in-range labels are pushed into the buckets and removed from
            // overflow; out-of-range labels are retained in their original order.
            var retained = new List<uint>(overflowbucket_.Count);
            foreach (uint label in overflowbucket_)
            {
                float cost = labelcontainer_![(int)label].SortCost();
                if (cost < maxcost_)
                {
                    buckets_[(int)(uint)((cost - mincost_) * inv_)].Add(label);
                }
                else
                {
                    retained.Add(label);
                }
            }

            overflowbucket_.Clear();
            overflowbucket_.AddRange(retained);
        }

        // Reset current cost and bucket to beginning of low level buckets.
        currentcost_ = (float)mincost_;
        currentbucketindex_ = 0;
    }

    /// <summary>
    /// Resizes a list of buckets to <paramref name="count"/> entries, mirroring
    /// <c>std::vector::resize</c>: truncates if larger, appends fresh empty buckets if smaller.
    /// </summary>
    private static void Resize(List<List<uint>> buckets, int count)
    {
        if (buckets.Count > count)
        {
            buckets.RemoveRange(count, buckets.Count - count);
        }
        else
        {
            while (buckets.Count < count)
            {
                buckets.Add(new List<uint>());
            }
        }
    }
}
