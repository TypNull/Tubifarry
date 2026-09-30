using NLog;
using System.Collections.Concurrent;
using Tubifarry.Download.Clients.Soulseek.Models;

namespace Tubifarry.Download.Clients.Soulseek;

public class SlskdBatchRestorer(ISlskdApiClient apiClient, Logger logger)
{
    private static readonly TimeSpan _batchRetention = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, (SlskdBatch Batch, DateTime FetchedAt)> _batches = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISlskdApiClient _apiClient = apiClient;
    private readonly Logger _logger = logger;

    public static string? GetMainBatchId(SlskdDownloadDirectory dir) =>
        dir.Files?
            .Where(f => f.BatchId != null)
            .GroupBy(f => f.BatchId!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault()?.Key;

    public async Task RestoreAsync(SlskdDownloadItem item, string batchId, SlskdProviderSettings settings)
    {
        item.BatchId = batchId;

        SlskdBatch? batch = await GetBatchAsync(settings, batchId);
        string? subdirectory;
        if (batch != null)
        {
            item.EnqueueDestination = batch.Destination;
            subdirectory = GetRelativeDestination(settings, batch.Destination);
        }
        else
        {
            subdirectory = item.EnqueueDestination ??= SlskdFolderNaming.GetDownloadDestination(item.FileData.Select(f => f.Filename), item.ReleaseInfo.Artist, item.ReleaseInfo.Album);
        }

        if (subdirectory != null)
            item.DerivedSubdirectory ??= subdirectory;

        _logger.Debug($"Restored batch {batchId} for {item.ID} with destination '{item.EnqueueDestination ?? "<slskd default>"}' ({(batch != null ? "from slskd" : "computed")}), subdirectory '{subdirectory ?? "<unresolved>"}'");
    }

    private async Task<SlskdBatch?> GetBatchAsync(SlskdProviderSettings settings, string batchId)
    {
        DateTime now = DateTime.UtcNow;
        foreach (KeyValuePair<string, (SlskdBatch Batch, DateTime FetchedAt)> entry in _batches.Where(e => now - e.Value.FetchedAt >= _batchRetention).ToList())
            _batches.TryRemove(entry.Key, out _);

        if (_batches.TryGetValue(batchId, out (SlskdBatch Batch, DateTime FetchedAt) cached))
            return cached.Batch;

        try
        {
            SlskdBatch? batch = await _apiClient.GetBatchAsync(settings, batchId);
            if (batch != null)
                _batches[batchId] = (batch, now);
            return batch;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, $"Fetching slskd batch {batchId} failed");
            return null;
        }
    }

    private static string? GetRelativeDestination(SlskdProviderSettings settings, string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
            return null;

        if (SlskdPathResolver.MakeRelativeToDownloads(settings.DownloadPath, destination) is string relative)
            return relative;

        string normalized = destination.Replace('\\', '/').Trim('/');
        return Path.IsPathRooted(destination) || !normalized.Split('/').All(SlskdFolderNaming.IsSafeSegment)
            ? null
            : normalized;
    }
}
