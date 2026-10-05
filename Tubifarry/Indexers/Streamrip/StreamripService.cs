using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tubifarry.Core.Python;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Streamrip
{
    public sealed record StreamripAlbum(string Id, string Type, string Title, string Artist, int Tracks, string? Date, long Duration, bool Explicit, int BitDepth, double SampleRate, string Url, string Cover);

    public sealed record StreamripDownloadResult(IReadOnlyList<string> Files, IReadOnlyList<string> Errors);

    public sealed record StreamripDeviceLogin(string DeviceCode, string Url);

    public interface IStreamripService
    {
        bool HasTidalLogin(StreamripIndexerSettings settings);
        void ForgetTidalLogin(StreamripIndexerSettings settings);
        Task<StreamripDeviceLogin> StartTidalLoginAsync(StreamripIndexerSettings settings, CancellationToken token = default);
        Task<bool?> PollTidalLoginAsync(StreamripIndexerSettings settings, string deviceCode, CancellationToken token = default);
        Task CheckAsync(StreamripIndexerSettings settings, CancellationToken token = default);
        Task<IReadOnlyList<StreamripAlbum>> SearchAsync(StreamripIndexerSettings settings, string query, CancellationToken token = default);
        Task<StreamripDownloadResult> DownloadAsync(StreamripIndexerSettings settings, string type, string id, int tracks, string folder, int slot, IProgress<PythonWorkerProgress> progress, CancellationToken token = default);
    }

    public sealed class StreamripService(IPythonEnvironments environments, IPythonWorkerPool workers, IUvInstallation uvInstallation) : IStreamripService
    {
        private const string HandlerScript = "streamrip_handler.py";
        private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan SearchTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromHours(2);
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        public bool HasTidalLogin(StreamripIndexerSettings settings) => File.Exists(GetTidalTokenPath(settings));

        public void ForgetTidalLogin(StreamripIndexerSettings settings) => FileSystemHelper.TryDeleteFile(GetTidalTokenPath(settings));

        public async Task<StreamripDeviceLogin> StartTidalLoginAsync(StreamripIndexerSettings settings, CancellationToken token = default)
        {
            JsonNode? result = await InvokeOnceAsync(settings, "tidal_login_start", CreateParameters(settings), token);
            return new StreamripDeviceLogin(
                result?["device_code"]?.GetValue<string>() ?? throw new InvalidOperationException("Tidal did not return a device code."),
                result?["url"]?.GetValue<string>() ?? "https://link.tidal.com");
        }

        public async Task<bool?> PollTidalLoginAsync(StreamripIndexerSettings settings, string deviceCode, CancellationToken token = default)
        {
            Dictionary<string, object?> parameters = CreateParameters(settings);
            parameters["device_code"] = deviceCode;
            JsonNode? result = await InvokeOnceAsync(settings, "tidal_login_poll", parameters, token);
            return result?["status"]?.GetValue<string>() switch
            {
                "ready" => true,
                "pending" => null,
                _ => false
            };
        }

        private async Task<JsonNode?> InvokeOnceAsync(StreamripIndexerSettings settings, string method, object parameters, CancellationToken token)
        {
            using PythonEnvironmentLease lease = await environments.AcquireAsync(CreateEnvironmentSpec(settings), token);
            PythonWorkerSpec spec = CreateSpec(lease.Environment, settings, $"login-{Guid.NewGuid():N}");
            try
            {
                return await workers.InvokeAsync(spec, method, parameters, CheckTimeout, token: token);
            }
            finally
            {
                workers.Stop(spec.Key);
            }
        }

        private string GetTidalTokenPath(StreamripIndexerSettings settings) =>
            Path.Combine(string.IsNullOrWhiteSpace(settings.InstallDirectory) ? uvInstallation.DefaultRootDirectory : settings.InstallDirectory, "tokens", "tidal.json");

        public async Task CheckAsync(StreamripIndexerSettings settings, CancellationToken token = default)
        {
            using PythonEnvironmentLease lease = await environments.AcquireAsync(CreateEnvironmentSpec(settings), token);
            PythonWorkerSpec spec = CreateSpec(lease.Environment, settings, $"check-{Guid.NewGuid():N}");
            try
            {
                await workers.InvokeAsync(spec, "check", CreateParameters(settings), CheckTimeout, token: token);
            }
            finally
            {
                workers.Stop(spec.Key);
            }
        }

        public async Task<IReadOnlyList<StreamripAlbum>> SearchAsync(StreamripIndexerSettings settings, string query, CancellationToken token = default)
        {
            using PythonEnvironmentLease lease = await environments.AcquireAsync(CreateEnvironmentSpec(settings), token);
            PythonWorkerSpec spec = CreateSpec(lease.Environment, settings, "search");

            Dictionary<string, object?> parameters = CreateParameters(settings);
            parameters["query"] = query;
            parameters["limit"] = settings.SearchLimit;

            JsonNode? result = await workers.InvokeAsync(spec, "search", parameters, SearchTimeout, token: token);
            return result?["albums"]?.Deserialize<List<StreamripAlbum>>(JsonOptions) ?? [];
        }

        public async Task<StreamripDownloadResult> DownloadAsync(StreamripIndexerSettings settings, string type, string id, int tracks, string folder, int slot, IProgress<PythonWorkerProgress> progress, CancellationToken token = default)
        {
            using PythonEnvironmentLease lease = await environments.AcquireAsync(CreateEnvironmentSpec(settings), token);
            PythonWorkerSpec spec = CreateSpec(lease.Environment, settings, $"download-{slot}");

            Dictionary<string, object?> parameters = CreateParameters(settings);
            parameters["type"] = type;
            parameters["id"] = id;
            parameters["tracks"] = tracks;
            parameters["folder"] = folder;

            JsonNode? result = await workers.InvokeAsync(spec, "download", parameters, DownloadTimeout, progress, token);
            return new StreamripDownloadResult(
                result?["files"]?.Deserialize<List<string>>(JsonOptions) ?? [],
                result?["errors"]?.Deserialize<List<string>>(JsonOptions) ?? []);
        }

        private static PythonEnvironmentSpec CreateEnvironmentSpec(StreamripIndexerSettings settings) => new(
            "streamrip",
            PythonEnvironments.DefaultPythonVersion,
            ["streamrip @ https://github.com/nathom/streamrip/archive/refs/tags/v2.2.0.tar.gz", "truststore>=0.10"],
            string.IsNullOrWhiteSpace(settings.InstallDirectory) ? null : settings.InstallDirectory,
            ["streamrip", "truststore"]);

        private static PythonWorkerSpec CreateSpec(PythonEnvironment environment, StreamripIndexerSettings settings, string purpose)
        {
            string account = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{settings.Username}\n{settings.Secret}\n{settings.UseToken}\n{settings.AppId}\n{settings.AppSecret}")))[..12];
            string key = string.Join('|', "streamrip", settings.SourceName, account, purpose, environment.Directory);
            return new PythonWorkerSpec(key, $"streamrip-{settings.SourceName}", environment, HandlerScript, new Dictionary<string, string>());
        }

        private Dictionary<string, object?> CreateParameters(StreamripIndexerSettings settings) => new()
        {
            ["token_file"] = GetTidalTokenPath(settings),
            ["source"] = settings.SourceName,
            ["user"] = settings.Username,
            ["secret"] = settings.Secret,
            ["use_token"] = settings.UseToken,
            ["app_id"] = settings.AppId,
            ["app_secret"] = settings.AppSecret,
            ["quality"] = settings.SourceQuality
        };
    }
}
