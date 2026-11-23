using NLog;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Tubifarry.Download.Clients.Lucida;

public sealed class LucidaRateLimiter : ILucidaRateLimiter
{
    private readonly Logger _logger;

    private readonly ConcurrentDictionary<string, WorkerSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _globalTimingLock = new();
    private DateTime _lastRequestTime = DateTime.MinValue;

    public IReadOnlyCollection<string> Workers => [.. _slots.Keys];

    public LucidaRateLimiter(Logger logger)
    {
        _logger = logger;
        EnsureWorkerRegistered(ILucidaRateLimiter.WORKER_MAUS);
        EnsureWorkerRegistered(ILucidaRateLimiter.WORKER_HUND);
        EnsureWorkerRegistered(ILucidaRateLimiter.WORKER_KATZE);
    }

    public void EnsureWorkerRegistered(string workerName)
    {
        if (string.IsNullOrEmpty(workerName))
            return;

        _slots.GetOrAdd(workerName, static name => new WorkerSlot(name));
    }

    public async Task<string> WaitForAvailableWorkerAsync(CancellationToken cancellationToken)
    {
        int attempts = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            attempts++;
            string? best = PickBestWorker();

            if (best is not null)
            {
                WorkerSlot slot = _slots[best];

                bool acquired = await slot.RequestSemaphore.WaitAsync(
                    TimeSpan.FromSeconds(5), cancellationToken);

                if (acquired)
                {
                    await EnforceGlobalDelayAsync(cancellationToken);
                    Interlocked.Increment(ref slot.ActiveRequests);
                    _logger.Trace($"Acquired worker '{best}' (attempt {attempts})");
                    return best;
                }
            }

            await Task.Delay(1000, cancellationToken);
        }

        throw new OperationCanceledException("Cancelled while waiting for an available worker");
    }

    public void MarkWorkerRateLimited(string workerName)
    {
        if (string.IsNullOrEmpty(workerName))
            return;

        EnsureWorkerRegistered(workerName);
        WorkerSlot slot = _slots[workerName];

        DateTime until = DateTime.UtcNow.AddSeconds(ILucidaRateLimiter.COOLDOWN_SECONDS);
        slot.RateLimitedUntil = until;
        _logger.Debug($"Worker '{workerName}' rate-limited until {until:HH:mm:ss}");
    }

    public void ReleaseWorker(string workerName)
    {
        if (string.IsNullOrEmpty(workerName) || !_slots.TryGetValue(workerName, out WorkerSlot? slot))
            return;

        try
        {
            slot.RequestSemaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            _logger.Trace($"Semaphore already at max for worker '{workerName}'");
        }

        int current = Interlocked.Decrement(ref slot.ActiveRequests);
        if (current < 0)
            Interlocked.Exchange(ref slot.ActiveRequests, 0);

        slot.LastSuccessfulRequest = DateTime.UtcNow;
        _logger.Trace($"Released worker '{workerName}'");
    }

    public bool IsRateLimitedResponse(string responseContent)
    {
        if (string.IsNullOrEmpty(responseContent))
            return false;

        if (!responseContent.StartsWith("{\"0\":", StringComparison.Ordinal))
            return false;

        try
        {
            string decoded = DecodeJsonEncodedHtml(responseContent);
            return decoded.Contains("404", StringComparison.OrdinalIgnoreCase)
                && decoded.Contains("Not Found", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyDictionary<string, LucidaWorkerState> GetWorkerStates()
    {
        Dictionary<string, LucidaWorkerState> snapshot = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, WorkerSlot> pair in _slots)
        {
            WorkerSlot slot = pair.Value;
            bool onCooldown = slot.RateLimitedUntil.HasValue && slot.RateLimitedUntil.Value > DateTime.UtcNow;

            snapshot[pair.Key] = new LucidaWorkerState
            {
                Name = pair.Key,
                IsAvailable = !onCooldown,
                RateLimitedUntil = slot.RateLimitedUntil,
                ActiveRequests = Interlocked.CompareExchange(ref slot.ActiveRequests, 0, 0),
                ActivePolling = Interlocked.CompareExchange(ref slot.ActivePolling, 0, 0),
                LastSuccessfulRequest = slot.LastSuccessfulRequest
            };
        }

        return snapshot;
    }

    public async Task AcquirePollingSlotAsync(string workerName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(workerName))
            return;

        EnsureWorkerRegistered(workerName);
        WorkerSlot slot = _slots[workerName];

        await slot.PollingSemaphore.WaitAsync(cancellationToken);
        Interlocked.Increment(ref slot.ActivePolling);
    }

    public void ReleasePollingSlot(string workerName)
    {
        if (string.IsNullOrEmpty(workerName) || !_slots.TryGetValue(workerName, out WorkerSlot? slot))
            return;

        try
        {
            slot.PollingSemaphore.Release();
        }
        catch (SemaphoreFullException) { }

        int current = Interlocked.Decrement(ref slot.ActivePolling);
        if (current < 0)
            Interlocked.Exchange(ref slot.ActivePolling, 0);
    }

    private string? PickBestWorker()
    {
        string? best = null;
        int bestScore = int.MaxValue;

        foreach (KeyValuePair<string, WorkerSlot> pair in _slots)
        {
            WorkerSlot slot = pair.Value;

            if (slot.RateLimitedUntil.HasValue && slot.RateLimitedUntil.Value > DateTime.UtcNow)
                continue;

            int active = Interlocked.CompareExchange(ref slot.ActiveRequests, 0, 0);
            if (active >= ILucidaRateLimiter.MAX_CONCURRENT_PER_WORKER)
                continue;

            if (active < bestScore)
            {
                bestScore = active;
                best = pair.Key;
            }
        }

        if (best is null)
        {
            foreach (KeyValuePair<string, WorkerSlot> pair in _slots)
            {
                WorkerSlot slot = pair.Value;
                bool cooldownExpired = !slot.RateLimitedUntil.HasValue || slot.RateLimitedUntil.Value <= DateTime.UtcNow;
                if (cooldownExpired)
                    return pair.Key;
            }
        }

        return best;
    }

    private async Task EnforceGlobalDelayAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay;

        lock (_globalTimingLock)
        {
            TimeSpan elapsed = DateTime.UtcNow - _lastRequestTime;
            delay = TimeSpan.FromMilliseconds(ILucidaRateLimiter.MIN_REQUEST_DELAY_MS) - elapsed;
            _lastRequestTime = DateTime.UtcNow;
        }

        if (delay > TimeSpan.Zero)
        {
            _logger.Trace($"Enforcing global request delay: {delay.TotalMilliseconds:F0}ms");
            await Task.Delay(delay, cancellationToken);

            lock (_globalTimingLock)
            {
                _lastRequestTime = DateTime.UtcNow;
            }
        }
    }

    private static string DecodeJsonEncodedHtml(string jsonEncoded)
    {
        Dictionary<string, JsonElement>? data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonEncoded);
        if (data is null)
            return string.Empty;

        StringBuilder sb = new();
        for (int i = 0; ; i++)
        {
            if (data.TryGetValue(i.ToString(), out JsonElement element) && element.ValueKind == JsonValueKind.String)
                sb.Append(element.GetString());
            else
                break;
        }
        return sb.ToString();
    }

    private sealed class WorkerSlot
    {
        public readonly SemaphoreSlim RequestSemaphore;
        public readonly SemaphoreSlim PollingSemaphore;

        public int ActiveRequests;
        public int ActivePolling;

        public DateTime? RateLimitedUntil;
        public DateTime? LastSuccessfulRequest;

        public WorkerSlot(string name)
        {
            _ = name;
            RequestSemaphore = new SemaphoreSlim(
                ILucidaRateLimiter.MAX_CONCURRENT_PER_WORKER,
                ILucidaRateLimiter.MAX_CONCURRENT_PER_WORKER);
            PollingSemaphore = new SemaphoreSlim(
                ILucidaRateLimiter.POLLING_MAX_CONCURRENT,
                ILucidaRateLimiter.POLLING_MAX_CONCURRENT);
        }
    }
}
