using System.Text.Json;
using Tubifarry.Indexers.Soulseek;

namespace Tubifarry.Download.Clients.Soulseek.Models;

public record SlskdDownloadFile(
    string Id,
    string Username,
    string Direction,
    string Filename,
    long Size,
    long StartOffset,
    string State,
    DateTime RequestedAt,
    DateTime EnqueuedAt,
    DateTime StartedAt,
    long BytesTransferred,
    double AverageSpeed,
    long BytesRemaining,
    TimeSpan ElapsedTime,
    double PercentComplete,
    TimeSpan RemainingTime,
    TimeSpan? EndedAt,
    int? PlaceInQueue = null,
    string? BatchId = null
)
{
    public static IEnumerable<SlskdDownloadFile> GetFiles(JsonElement filesElement)
    {
        if (filesElement.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (JsonElement file in filesElement.EnumerateArray())
            yield return Parse(file);
    }

    public static SlskdDownloadFile? ParseSingle(JsonElement el) =>
        el.ValueKind == JsonValueKind.Object ? Parse(el) : null;

    private static SlskdDownloadFile Parse(JsonElement file) => new(
        Id: file.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty,
        Username: file.TryGetProperty("username", out JsonElement username) ? username.GetString() ?? string.Empty : string.Empty,
        Direction: file.TryGetProperty("direction", out JsonElement direction) ? direction.GetString() ?? string.Empty : string.Empty,
        Filename: file.TryGetProperty("filename", out JsonElement filename) ? filename.GetString() ?? string.Empty : string.Empty,
        Size: file.TryGetProperty("size", out JsonElement size) ? size.GetInt64() : 0L,
        StartOffset: file.TryGetProperty("startOffset", out JsonElement startOffset) ? startOffset.GetInt64() : 0L,
        State: file.TryGetProperty("state", out JsonElement state) ? state.GetString() ?? string.Empty : string.Empty,
        RequestedAt: file.TryGetProperty("requestedAt", out JsonElement requestedAt) && DateTime.TryParse(requestedAt.GetString(), out DateTime rat) ? rat : DateTime.MinValue,
        EnqueuedAt: file.TryGetProperty("enqueuedAt", out JsonElement enqueuedAt) && DateTime.TryParse(enqueuedAt.GetString(), out DateTime eat) ? eat : DateTime.MinValue,
        StartedAt: file.TryGetProperty("startedAt", out JsonElement startedAt) && DateTime.TryParse(startedAt.GetString(), out DateTime sat) ? sat.ToUniversalTime() : DateTime.MinValue,
        BytesTransferred: file.TryGetProperty("bytesTransferred", out JsonElement bytesTransferred) ? bytesTransferred.GetInt64() : 0L,
        AverageSpeed: file.TryGetProperty("averageSpeed", out JsonElement averageSpeed) ? averageSpeed.GetDouble() : 0.0,
        BytesRemaining: file.TryGetProperty("bytesRemaining", out JsonElement bytesRemaining) ? bytesRemaining.GetInt64() : 0L,
        ElapsedTime: file.TryGetProperty("elapsedTime", out JsonElement elapsedTime) && TimeSpan.TryParse(elapsedTime.GetString(), out TimeSpan et) ? et : TimeSpan.Zero,
        PercentComplete: file.TryGetProperty("percentComplete", out JsonElement percentComplete) ? percentComplete.GetDouble() : 0.0,
        RemainingTime: file.TryGetProperty("remainingTime", out JsonElement remainingTime) && TimeSpan.TryParse(remainingTime.GetString(), out TimeSpan rt) ? rt : TimeSpan.Zero,
        EndedAt: file.TryGetProperty("endedAt", out JsonElement endedAt) && TimeSpan.TryParse(endedAt.GetString(), out TimeSpan ea) ? ea : null,
        PlaceInQueue: file.TryGetProperty("placeInQueue", out JsonElement placeInQueue) && placeInQueue.ValueKind != JsonValueKind.Null ? placeInQueue.GetInt32() : null,
        BatchId: file.TryGetProperty("batchId", out JsonElement batchId) && batchId.ValueKind == JsonValueKind.String ? batchId.GetString() : null
    );

    public SlskdFileData ToSlskdFileData()
    {
        string? ext = Path.GetExtension(Filename);
        if (!string.IsNullOrEmpty(ext))
            ext = ext.TrimStart('.');

        return new SlskdFileData(
            Filename: Filename,
            BitRate: null,
            BitDepth: null,
            Size: Size,
            Length: null,
            Extension: ext ?? "",
            SampleRate: null,
            Code: 1,
            IsLocked: false
        );
    }
}
