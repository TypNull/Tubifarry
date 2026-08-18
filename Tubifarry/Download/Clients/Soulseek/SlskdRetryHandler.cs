using NLog;
using NzbDrone.Core.Download;
using System.Text.Json;
using Tubifarry.Core.Telemetry;
using Tubifarry.Download.Clients.Soulseek.Models;

namespace Tubifarry.Download.Clients.Soulseek;

public class SlskdRetryHandler(ISlskdApiClient apiClient, ISentryHelper sentry, Logger logger)
{
    private readonly ISlskdApiClient _apiClient = apiClient;
    private readonly ISentryHelper _sentry = sentry;
    private readonly Logger _logger = logger;

    public void OnFileStateChanged(SlskdDownloadItem? item, SlskdFileState fileState, SlskdProviderSettings settings)
    {
        fileState.UpdateMaxRetryCount(settings.RetryAttempts);

        if (item != null
            && fileState.GetStatus() == DownloadItemStatus.Failed
            && fileState.RetryCount >= fileState.MaxRetryCount)
        {
            bool allOthersCompleted = item.FileStates.Values
                .Where(fs => !ReferenceEquals(fs, fileState))
                .All(fs => fs.GetStatus() == DownloadItemStatus.Completed);

            if (allOthersCompleted)
                fileState.UpdateMaxRetryCount(fileState.MaxRetryCount + 2);

        }

        if (fileState.GetStatus() != DownloadItemStatus.Warning)
            return;
        if (item == null)
            return;

        _logger.Trace($"Retry triggered: {Path.GetFileName(fileState.File.Filename)} | State: {fileState.State} | Attempt: {fileState.RetryCount + 1}/{fileState.MaxRetryCount}");
        _ = RetryDownloadAsync(item, fileState, settings);
    }

    private async Task RetryDownloadAsync(SlskdDownloadItem item, SlskdFileState fileState, SlskdProviderSettings settings)
    {
        ISpan? span = _sentry.StartSpan("slskd.retry", Path.GetFileName(fileState.File.Filename));
        _sentry.SetSpanData(span, "file.name", Path.GetFileName(fileState.File.Filename));
        _sentry.SetSpanData(span, "retry.attempt", fileState.RetryCount + 1);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(item.ReleaseInfo.Source);
            JsonElement matchingEl = doc.RootElement.EnumerateArray()
                .FirstOrDefault(x =>
                    x.TryGetProperty("Filename", out JsonElement fn) &&
                    fn.GetString() == fileState.File.Filename);

            if (matchingEl.ValueKind == JsonValueKind.Undefined)
            {
                _sentry.FinishSpan(span, SpanStatus.NotFound);
                return;
            }

            long size = matchingEl.TryGetProperty("Size", out JsonElement sz) ? sz.GetInt64() : 0L;
            string username = item.Username ?? ExtractUsernameFromPath(item.ReleaseInfo.DownloadUrl);

            await _apiClient.EnqueueDownloadAsync(settings, username, [(fileState.File.Filename, size)], externalId: item.ID);
            _logger.Trace($"Retry enqueued: {Path.GetFileName(fileState.File.Filename)}");
            _sentry.FinishSpan(span, SpanStatus.Ok);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Failed to retry download for file: {fileState.File.Filename}");
            _sentry.FinishSpan(span, ex);
        }
        finally
        {
            fileState.IncrementAttempt();
        }
    }

    private static string ExtractUsernameFromPath(string path)
    {
        string[] parts = path.TrimEnd('/').Split('/');
        return Uri.UnescapeDataString(parts[^1]);
    }
}
