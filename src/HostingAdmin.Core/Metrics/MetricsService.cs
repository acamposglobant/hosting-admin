namespace HostingAdmin.Core.Metrics;

/// <summary>
/// Bounded metrics access (plan D10): serves snapshots from a short-TTL cache,
/// collapses concurrent requests into a single in-flight collection
/// (single-flight), and enforces a hard per-collection timeout. Burst requests
/// therefore perform at most one underlying collection.
/// </summary>
public sealed class MetricsService
{
    private readonly IMetricsProvider provider;
    private readonly MetricsOptions options;
    private readonly SemaphoreSlim collectionGate = new(1, 1);
    private readonly object cacheLock = new();
    private MetricsSnapshot? cachedSnapshot;
    private DateTimeOffset cachedAt;

    public MetricsService(IMetricsProvider provider, MetricsOptions options)
    {
        this.provider = provider;
        this.options = options;
    }

    public MetricsOptions Options => options;

    public async Task<MetricsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var cached = ReadCached();
        if (cached is not null)
        {
            return cached;
        }

        // Single-flight: one collection at a time; waiters re-check the cache
        // afterwards, so a burst performs one underlying collection.
        try
        {
            await collectionGate.WaitAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown race: proceed without the gate rather than failing.
        }

        try
        {
            cached = ReadCached();
            if (cached is not null)
            {
                return cached;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(options.CollectionTimeout);
            try
            {
                cached = await provider.GetSnapshotAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Collection exceeded its timeout: report unavailability rather
                // than failing the request (D10 bounded resources, D11).
                cached = MetricsSnapshot.Unavailable("collection_timeout");
            }

            lock (cacheLock)
            {
                cachedSnapshot = cached;
                cachedAt = DateTimeOffset.UtcNow;
            }

            return cached;
        }
        finally
        {
            try
            {
                collectionGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Shutdown race: nothing to release.
            }
        }
    }

    private MetricsSnapshot? ReadCached()
    {
        lock (cacheLock)
        {
            if (cachedSnapshot is not null && DateTimeOffset.UtcNow - cachedAt < options.CacheTtl)
            {
                return cachedSnapshot;
            }

            return null;
        }
    }
}
