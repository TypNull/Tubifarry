using FluentValidation.Results;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download.Clients;
using System.Net;
using System.Text.Json;
using Tubifarry.Download.Clients.Soulseek.Models;
using Tubifarry.Indexers.Soulseek;

namespace Tubifarry.Download.Clients.Soulseek;

public class SlskdApiClient(IHttpClient httpClient) : ISlskdApiClient
{
    private readonly SemaphoreSlim _enqueueLimiter = new(2, 2);

    public async Task<SlskdEnqueueResult> EnqueueDownloadAsync(
        SlskdProviderSettings settings, string username, IEnumerable<(string Filename, long Size)> files, string? externalId = null, string? destination = null)
    {
        await _enqueueLimiter.WaitAsync();
        try
        {
            List<(string Filename, long Size)> fileList = files.ToList();

            SlskdEnqueueResult? batchResult = await TryEnqueueBatchAsync(settings, username, fileList, externalId, destination);
            if (batchResult != null)
                return batchResult;

            string payload = JsonSerializer.Serialize(fileList.Select(f => new { f.Filename, f.Size }));
            HttpResponse response = await httpClient.ExecuteAsync(BuildRequest(
                settings,
                $"/api/v0/transfers/downloads/{Uri.EscapeDataString(username)}",
                HttpMethod.Post,
                payload));

            if (response.StatusCode != HttpStatusCode.Created)
                throw new DownloadClientException($"Enqueue failed for {username}. Status: {response.StatusCode}");

            return new SlskdEnqueueResult(null, fileList.Select(f => f.Filename).ToList(), []);
        }
        finally
        {
            _enqueueLimiter.Release();
        }
    }

    private async Task<SlskdEnqueueResult?> TryEnqueueBatchAsync(
        SlskdProviderSettings settings, string username, List<(string Filename, long Size)> files, string? externalId, string? destination)
    {
        string batchId = Guid.NewGuid().ToString();
        string payload = JsonSerializer.Serialize(new
        {
            id = batchId,
            username,
            files = files.Select(f => new { filename = f.Filename, size = f.Size }),
            options = new { externalId, destination }
        });

        for (int attempt = 0; ; attempt++)
        {
            HttpRequest request = BuildRequest(settings, "/api/v0/transfers/downloads/batches", HttpMethod.Post, payload);
            request.SuppressHttpError = true;
            HttpResponse response = await httpClient.ExecuteAsync(request);

            switch (response.StatusCode)
            {
                case HttpStatusCode.Created:
                    return new SlskdEnqueueResult(batchId, files.Select(f => f.Filename).ToList(), []);

                case HttpStatusCode.OK:
                case HttpStatusCode.MultiStatus:
                    return ParseBatchResponse(batchId, files, response.Content);

                case HttpStatusCode.BadRequest when response.Content?.Contains("QueueDownloadRequest", StringComparison.OrdinalIgnoreCase) == true:
                case HttpStatusCode.MethodNotAllowed:
                case HttpStatusCode.NotFound when IsEndpointMissing(response):
                    return null;

                case HttpStatusCode.NotFound:
                    throw new DownloadClientException($"Enqueue failed for {username}: user appears to be offline.");

                case HttpStatusCode.TooManyRequests when attempt < 2:
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    continue;

                default:
                    throw new DownloadClientException($"Batch enqueue failed for {username}. Status: {response.StatusCode}");
            }
        }
    }

    private static SlskdEnqueueResult ParseBatchResponse(string batchId, List<(string Filename, long Size)> files, string content)
    {
        List<SlskdEnqueueFailure> failures = [];

        using JsonDocument doc = JsonDocument.Parse(content);
        if (doc.RootElement.TryGetProperty("failures", out JsonElement failuresEl) &&
            failuresEl.ValueKind == JsonValueKind.Array)
        {
            failures = failuresEl.Deserialize<List<SlskdEnqueueFailure>>() ?? [];
        }

        HashSet<string> failedNames = failures.Select(f => f.Filename).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> enqueued = files.Select(f => f.Filename).Where(f => !failedNames.Contains(f)).ToList();

        return new SlskdEnqueueResult(batchId, enqueued, failures);
    }

    private static bool IsEndpointMissing(HttpResponse response) =>
        string.IsNullOrWhiteSpace(response.Content) ||
        !response.Content.Contains("offline", StringComparison.OrdinalIgnoreCase);

    public async Task<List<SlskdUserTransfers>> GetAllTransfersAsync(SlskdProviderSettings settings, bool includeRemoved = false)
    {
        string endpoint = "/api/v0/transfers/downloads" + (includeRemoved ? "?includeRemoved=true" : "");
        HttpResponse response = await httpClient.ExecuteAsync(BuildRequest(settings, endpoint));

        if (response.StatusCode != HttpStatusCode.OK)
            return [];

        List<SlskdUserTransfers> result = [];
        using JsonDocument doc = JsonDocument.Parse(response.Content);

        foreach (JsonElement userEl in doc.RootElement.EnumerateArray())
        {
            string username = userEl.TryGetProperty("username", out JsonElement u) ? u.GetString() ?? "" : "";
            userEl.TryGetProperty("directories", out JsonElement dirsEl);
            result.Add(new SlskdUserTransfers
            {
                Username = username,
                Directories = SlskdDownloadDirectory.GetDirectories(dirsEl).ToList()
            });
        }

        return result;
    }

    public async Task<SlskdUserTransfers?> GetUserTransfersAsync(SlskdProviderSettings settings, string username)
    {
        HttpResponse response = await httpClient.ExecuteAsync(
            BuildRequest(settings, $"/api/v0/transfers/downloads/{Uri.EscapeDataString(username)}"));

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (response.StatusCode != HttpStatusCode.OK)
            return null;

        using JsonDocument doc = JsonDocument.Parse(response.Content);
        doc.RootElement.TryGetProperty("directories", out JsonElement dirsEl);

        return new SlskdUserTransfers
        {
            Username = username,
            Directories = SlskdDownloadDirectory.GetDirectories(dirsEl).ToList()
        };
    }

    public async Task<SlskdDownloadFile?> GetTransferAsync(SlskdProviderSettings settings, string username, string fileId)
    {
        HttpResponse response = await httpClient.ExecuteAsync(
            BuildRequest(settings, $"/api/v0/transfers/downloads/{Uri.EscapeDataString(username)}/{fileId}"));

        if (response.StatusCode != HttpStatusCode.OK)
            return null;

        using JsonDocument doc = JsonDocument.Parse(response.Content);
        return SlskdDownloadFile.ParseSingle(doc.RootElement);
    }

    public async Task<int?> GetQueuePositionAsync(SlskdProviderSettings settings, string username, string fileId)
    {
        HttpResponse response = await httpClient.ExecuteAsync(
            BuildRequest(settings, $"/api/v0/transfers/downloads/{Uri.EscapeDataString(username)}/{fileId}/position"));

        if (response.StatusCode != HttpStatusCode.OK)
            return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Content);
            if (doc.RootElement.ValueKind == JsonValueKind.Number)
                return doc.RootElement.GetInt32();
        }
        catch { /* fall through */ }

        return int.TryParse(response.Content.Trim(), out int position) ? position : null;
    }

    public async Task DeleteTransferAsync(SlskdProviderSettings settings, string username, string fileId, bool remove = false)
    {
        string endpoint = $"/api/v0/transfers/downloads/{Uri.EscapeDataString(username)}/{fileId}"
            + (remove ? "?remove=true" : "");
        await httpClient.ExecuteAsync(BuildRequest(settings, endpoint, HttpMethod.Delete));
    }

    public async Task DeleteAllCompletedAsync(SlskdProviderSettings settings) =>
        await httpClient.ExecuteAsync(
            BuildRequest(settings, "/api/v0/transfers/downloads/all/completed", HttpMethod.Delete));

    public async Task<string?> GetDownloadPathAsync(SlskdProviderSettings settings) =>
        (await GetDestinationConfigAsync(settings))?.DownloadsDirectory;

    public async Task<SlskdDestinationConfig?> GetDestinationConfigAsync(SlskdProviderSettings settings)
    {
        HttpResponse response = await httpClient.ExecuteAsync(BuildRequest(settings, "/api/v0/options"));

        if (response.StatusCode != HttpStatusCode.OK)
            return null;

        using JsonDocument doc = JsonDocument.Parse(response.Content);
        return SlskdDestinationConfig.FromOptions(doc.RootElement);
    }

    public async Task<ValidationFailure?> TestConnectionAsync(SlskdProviderSettings settings)
    {
        try
        {
            Uri uri = new(settings.BaseUrl);
            settings.IsLocalhost = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(uri.Host, out IPAddress? ip) && IPAddress.IsLoopback(ip));
        }
        catch (UriFormatException ex)
        {
            return new ValidationFailure("BaseUrl", $"Invalid BaseUrl format: {ex.Message}");
        }

        try
        {
            HttpRequest request = BuildRequest(settings, "/api/v0/application");
            request.AllowAutoRedirect = true;
            request.RequestTimeout = TimeSpan.FromSeconds(30);
            HttpResponse response = await httpClient.ExecuteAsync(request);

            if (response.StatusCode != HttpStatusCode.OK)
                return new ValidationFailure("BaseUrl", $"Unable to connect to Slskd. Status: {response.StatusCode}");

            using JsonDocument doc = JsonDocument.Parse(response.Content);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("server", out JsonElement serverEl) ||
                !serverEl.TryGetProperty("state", out JsonElement stateEl))
                return new ValidationFailure("BaseUrl", "Failed to parse Slskd response: missing 'server' or 'state'.");

            string? serverState = stateEl.GetString();
            if (string.IsNullOrEmpty(serverState) || !serverState.Contains("Connected"))
                return new ValidationFailure("BaseUrl", $"Slskd server is not connected. State: {serverState}");

            SlskdDestinationConfig? destinationConfig = await GetDestinationConfigAsync(settings);
            settings.DownloadPath = destinationConfig?.DownloadsDirectory ?? string.Empty;
            settings.SubdirectoryPattern = destinationConfig?.SubdirectoryPattern;
            if (string.IsNullOrEmpty(settings.DownloadPath))
                return new ValidationFailure("DownloadPath", "DownloadPath could not be found or is invalid.");

            return null;
        }
        catch (HttpException ex)
        {
            return new ValidationFailure("BaseUrl", $"Unable to connect to Slskd: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new ValidationFailure(string.Empty, $"Unexpected error: {ex.Message}");
        }
    }

    public async Task<(List<SlskdEventRecord> Events, int TotalCount)> GetEventsAsync(
        SlskdProviderSettings settings, int offset, int limit)
    {
        HttpResponse response = await httpClient.ExecuteAsync(
            BuildRequest(settings, $"/api/v0/events?offset={offset}&limit={limit}"));

        if (response.StatusCode != HttpStatusCode.OK)
            return ([], 0);

        string? totalHeader = response.Headers["X-Total-Count"];
        int.TryParse(totalHeader, out int totalCount);

        using JsonDocument doc = JsonDocument.Parse(response.Content);
        List<SlskdEventRecord> records = [];

        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            records.Add(new SlskdEventRecord
            {
                Id = el.TryGetProperty("id", out JsonElement id) && Guid.TryParse(id.GetString(), out Guid g) ? g : Guid.Empty,
                Timestamp = el.TryGetProperty("timestamp", out JsonElement ts) && DateTime.TryParse(ts.GetString(), out DateTime dt) ? dt : DateTime.MinValue,
                Type = el.TryGetProperty("type", out JsonElement type) ? type.GetString() ?? "" : "",
                Data = el.TryGetProperty("data", out JsonElement data) ? data.GetString() ?? "" : "",
            });
        }

        if (totalCount == 0)
            totalCount = offset + records.Count;

        return (records, totalCount);
    }

    private HttpRequest BuildRequest(SlskdProviderSettings settings, string endpoint,
        HttpMethod? method = null, string? content = null)
    {
        HttpRequestBuilder builder = new HttpRequestBuilder($"{settings.BaseUrl}{endpoint}")
            .SetHeader("X-API-KEY", settings.ApiKey)
            .SetHeader("Accept", "application/json");

        if (method != null)
            builder.Method = method;

        if (!string.IsNullOrEmpty(content))
        {
            builder.SetHeader("Content-Type", "application/json");
            HttpRequest request = builder.Build();
            request.SetContent(content);
            return request;
        }

        return builder.Build();
    }
}
