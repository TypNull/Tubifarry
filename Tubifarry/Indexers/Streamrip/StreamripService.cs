using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tubifarry.Core.Python;

namespace Tubifarry.Indexers.Streamrip
{
    public sealed record StreamripAlbum(string Id, string Type, string Title, string Artist, int Tracks, string? Date, long Duration, bool Explicit, int BitDepth, double SampleRate, string Url, string Cover);

    public interface IStreamripService
    {
        Task CheckAsync(StreamripIndexerSettings settings, CancellationToken token = default);
        Task<IReadOnlyList<StreamripAlbum>> SearchAsync(StreamripIndexerSettings settings, string query, CancellationToken token = default);
    }

    public sealed class StreamripService(IPythonEnvironments environments, IPythonWorkerPool workers) : IStreamripService
    {
        private const string HandlerScript = "streamrip_handler.py";
        private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan SearchTimeout = TimeSpan.FromMinutes(2);
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

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

        private static Dictionary<string, object?> CreateParameters(StreamripIndexerSettings settings) => new()
        {
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
