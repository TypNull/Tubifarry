using FluentValidation.Results;
using Tubifarry.Download.Clients.Soulseek.Models;
using Tubifarry.Indexers.Soulseek;

namespace Tubifarry.Download.Clients.Soulseek;

public class SlskdUserTransfers
{
    public string Username { get; set; } = string.Empty;
    public IEnumerable<SlskdDownloadDirectory> Directories { get; set; } = [];
}

public class SlskdEventRecord
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
}

public interface ISlskdApiClient
{
    Task<SlskdEnqueueResult> EnqueueDownloadAsync(SlskdProviderSettings settings, string username, IEnumerable<(string Filename, long Size)> files, string? externalId = null, string? destination = null);
    Task<List<SlskdUserTransfers>> GetAllTransfersAsync(SlskdProviderSettings settings, bool includeRemoved = false);
    Task<SlskdUserTransfers?> GetUserTransfersAsync(SlskdProviderSettings settings, string username);
    Task<SlskdDownloadFile?> GetTransferAsync(SlskdProviderSettings settings, string username, string fileId);
    Task<int?> GetQueuePositionAsync(SlskdProviderSettings settings, string username, string fileId);
    Task DeleteTransferAsync(SlskdProviderSettings settings, string username, string fileId, bool remove = false);
    Task DeleteAllCompletedAsync(SlskdProviderSettings settings);
    Task<string?> GetDownloadPathAsync(SlskdProviderSettings settings);
    Task<SlskdDestinationConfig?> GetDestinationConfigAsync(SlskdProviderSettings settings);
    Task<ValidationFailure?> TestConnectionAsync(SlskdProviderSettings settings);
    Task<(List<SlskdEventRecord> Events, int TotalCount)> GetEventsAsync(SlskdProviderSettings settings, int offset, int limit);
}
