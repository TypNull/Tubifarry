using NLog;
using NzbDrone.Core.Download;
using Tubifarry.Core.Telemetry;
using Tubifarry.Download.Clients.Soulseek.Models;
using Tubifarry.Indexers.Soulseek;

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
            SlskdFileData? fileData = item.FileData.FirstOrDefault(f => f.Filename == fileState.File.Filename);
            if (fileData == null)
            {
                _sentry.FinishSpan(span, SpanStatus.NotFound);
                return;
            }

            string username = item.Username ?? SlskdDownloadItem.GetUsername(item.ReleaseInfo.DownloadUrl);

            await _apiClient.EnqueueDownloadAsync(settings, username, [(fileState.File.Filename, fileData.Size)], externalId: item.ID, destination: item.EnqueueDestination);
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
}
