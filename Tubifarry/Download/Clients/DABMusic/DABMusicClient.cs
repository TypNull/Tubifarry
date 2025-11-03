using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using System.Text.RegularExpressions;
using Tubifarry.Download.Base;
using Tubifarry.Indexers.DABMusic;

namespace Tubifarry.Download.Clients.DABMusic
{
    /// <summary>
    /// DABMusic download client for high-quality music downloads
    /// Integrates with DABMusic API to download tracks and albums
    /// </summary>
    public class DABMusicClient : DownloadClientBase<DABMusicProviderSettings>
    {
        private readonly IDABMusicDownloadManager _downloadManager;
        private readonly INamingConfigService _namingService;
        private readonly IDABMusicSessionManager _sessionManager;

        public DABMusicClient(
            IDABMusicDownloadManager downloadManager,
            IDABMusicSessionManager sessionManager,
            IConfigService configService,
            IDiskProvider diskProvider,
            INamingConfigService namingConfigService,
            IRemotePathMappingService remotePathMappingService,
            ILocalizationService localizationService,
            Logger logger)
            : base(configService, diskProvider, remotePathMappingService, localizationService, logger)
        {
            _downloadManager = downloadManager;
            _namingService = namingConfigService;
            _sessionManager = sessionManager;
        }

        public override string Name => "DABMusic";
        public override string Protocol => nameof(QobuzDownloadProtocol);
        public new DABMusicProviderSettings Settings => base.Settings;

        public override Task<string> Download(RemoteAlbum remoteAlbum, IIndexer indexer) => _downloadManager.Download(remoteAlbum, indexer, _namingService.GetConfig(), this);

        public override IEnumerable<DownloadClientItem> GetItems() => _downloadManager.GetItems();

        public override void RemoveItem(DownloadClientItem item, bool deleteData)
        {
            if (deleteData)
                DeleteItemData(item);
            _downloadManager.RemoveItem(item);
        }

        public override DownloadClientInfo GetStatus() => new()
        {
            IsLocalhost = false,
            OutputRootFolders = new() { new OsPath(Settings.DownloadPath) }
        };

        protected override void Test(List<ValidationFailure> failures)
        {
            if (!_diskProvider.FolderExists(Settings.DownloadPath))
            {
                failures.Add(new ValidationFailure("DownloadPath", "Download path does not exist"));
                return;
            }

            if (!_diskProvider.FolderWritable(Settings.DownloadPath))
            {
                failures.Add(new ValidationFailure("DownloadPath", "Download path is not writable"));
            }

            try
            {
                BaseHttpClient httpClient = new(Settings.BaseUrl, TimeSpan.FromSeconds(30));
                string response = httpClient.GetStringAsync("/").GetAwaiter().GetResult();

                if (string.IsNullOrEmpty(response))
                {
                    failures.Add(new ValidationFailure("BaseUrl", "Cannot connect to DABMusic instance: Empty response"));
                    return;
                }

                if (!response.Contains("DABMusic", StringComparison.OrdinalIgnoreCase) &&
                    !response.Contains("dabmusic", StringComparison.OrdinalIgnoreCase) &&
                    !Regex.IsMatch(response, "<title>.*?(DAB|Music).*?</title>", RegexOptions.IgnoreCase))
                {
                    failures.Add(new ValidationFailure("BaseUrl", "The provided URL does not appear to be a DABMusic instance"));
                    return;
                }

                if (!string.IsNullOrWhiteSpace(Settings.Email) && !string.IsNullOrWhiteSpace(Settings.Password))
                {
                    DABMusicSession? session = _sessionManager.GetOrCreateSession(Settings.BaseUrl.Trim(), Settings.Email, Settings.Password, true);

                    if (session == null)
                    {
                        failures.Add(new ValidationFailure("Email", "Failed to authenticate with DABMusic. Check your email and password."));
                        return;
                    }

                    _logger.Debug($"Successfully authenticated with DABMusic as {Settings.Email}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error connecting to DABMusic instance");
                failures.Add(new ValidationFailure("BaseUrl", $"Cannot connect to DABMusic instance: {ex.Message}"));
            }
        }
    }
}