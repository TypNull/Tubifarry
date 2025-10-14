using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Plugins;
using NzbDrone.Core.Plugins.Resources;
using System.Text.RegularExpressions;

namespace Tubifarry.Core.Utilities
{
    public class FlexiblePluginService : PluginService, IPluginService
    {
        private static readonly Regex MinVersionRegex = new(@"Minimum Lidarr Version:?\s*(?:\*\*)?[\s]*(?<version>\d+\.\d+\.\d+\.\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RepoRegex = new(@"https://github\.com/(?<owner>[^/]+)/(?<name>[^/]+)(?:/tree/(?<branch>[^/\s]+))?", RegexOptions.Compiled);
        private readonly Logger _logger;
        private readonly IHttpClient _httpClient;

        public FlexiblePluginService(IHttpClient httpClient,
                           IEnumerable<IPlugin> installedPlugins,
                           Logger logger) : base(httpClient, installedPlugins, logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        RemotePlugin? IPluginService.GetRemotePlugin(string repoUrl)
        {
            (string? branch, string? owner, string? name) = ParseBranchUrl(repoUrl);
            string releaseUrl = $"https://api.github.com/repos/{owner}/{name}/releases";
            List<Release>? releases = _httpClient.Get<List<Release>>(new HttpRequest(releaseUrl)).Resource;

            if (!releases?.Any() ?? true)
            {
                _logger.Warn($"No releases found for {name}");
                return null;
            }

            string framework = branch == null ? PluginInfo.Framework : $"{PluginInfo.Framework}-{branch}";
            releases = releases!.Where(release => release.Assets.Any(asset => asset.Name.EndsWith($"{framework}.zip"))).ToList();
            Release? latest = releases.OrderByDescending(x => x.PublishedAt).FirstOrDefault(x => IsSupported(x, branch));

            if (latest == null)
            {
                _logger.Warn($"Plugin {name} requires newer version of Lidarr");
                return null;
            }

            Version version = Version.Parse(latest.TagName.TrimStart('v'));
            Asset? asset = latest.Assets.FirstOrDefault(x => x.Name.EndsWith($"{framework}.zip"));

            if (asset == null)
            {
                _logger.Warn($"No plugin package found for {framework} for {name}");
                return null;
            }

            return new RemotePlugin
            {
                GithubUrl = repoUrl,
                Name = name,
                Owner = owner,
                Version = version,
                PackageUrl = asset.BrowserDownloadUrl
            };
        }

        public (string?, string?, string?) ParseBranchUrl(string repoUrl)
        {
            Match match = RepoRegex.Match(repoUrl);
            if (!match.Success)
            {
                _logger.Warn("Invalid plugin repo URL");
                return (null, null, null);
            }
            string? branch = match.Groups["branch"].Success ? match.Groups["branch"].Value : null;
            string owner = match.Groups["owner"].Value;
            string name = match.Groups["name"].Value;
            return (branch, owner, name);
        }

        private static bool IsSupported(Release release, string? branch)
        {
            Match match = MinVersionRegex.Match(release.Body);
            if (match.Success)
            {
                Version minVersion = Version.Parse(match.Groups["version"].Value);
                if (minVersion > BuildInfo.Version)
                    return false;
            }
            Version version = Version.Parse(release.TagName.TrimStart('v'));
            string framework = branch == null ? PluginInfo.Framework : $"{PluginInfo.Framework}-{branch}";
            Asset? asset = release.Assets.FirstOrDefault(x => x.Name.EndsWith($"{framework}.zip"));
            return asset != null;
        }
    }
}