using FluentValidation.Results;
using System.Collections.Concurrent;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;
using Tubifarry.Core.Model;
using Tubifarry.Core.Python;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Streamrip
{
    public class StreamripIndexer(IStreamripService streamrip, IIndexerStatusService indexerStatusService, IConfigService configService, IParsingService parsingService, Logger logger)
        : IndexerBase<StreamripIndexerSettings>(indexerStatusService, configService, parsingService, logger)
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan TidalLoginLifetime = TimeSpan.FromMinutes(5);
        private static readonly ConcurrentDictionary<string, (StreamripDeviceLogin Login, DateTime Started)> PendingTidalLogins = new(StringComparer.Ordinal);

        public override string Name => "Streamrip";
        public override string Protocol => nameof(StreamripDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;

        public override ProviderMessage Message => new("Searches SoundCloud, Qobuz or Deezer through streamrip. Use it together with the Streamrip download client.", ProviderMessageType.Info);

        public override Task<IList<ReleaseInfo>> FetchRecent() => Task.FromResult<IList<ReleaseInfo>>([]);

        public override Task<IList<ReleaseInfo>> Fetch(AlbumSearchCriteria searchCriteria) =>
            SearchAsync(string.Join(' ', new[] { searchCriteria.ArtistQuery, searchCriteria.AlbumQuery }.Where(part => !string.IsNullOrWhiteSpace(part))));

        public override Task<IList<ReleaseInfo>> Fetch(ArtistSearchCriteria searchCriteria) => SearchAsync(searchCriteria.ArtistQuery);

        public override HttpRequest GetDownloadRequest(string link) => new(link);

        protected override async Task Test(List<ValidationFailure> failures)
        {
            try
            {
                using CancellationTokenSource timeout = new(TestTimeout);

                if (Settings.SourceType == StreamripSource.Tidal && !streamrip.HasTidalLogin(Settings))
                {
                    string? instruction = await ContinueTidalLoginAsync(timeout.Token);
                    if (instruction != null)
                    {
                        failures.Add(new ValidationFailure(nameof(Settings.Source), instruction));
                        return;
                    }
                }

                try
                {
                    await streamrip.CheckAsync(Settings, timeout.Token);
                }
                catch (PythonWorkerException ex) when (Settings.SourceType == StreamripSource.Tidal && ex.Message.Contains("rejected the saved login", StringComparison.Ordinal))
                {
                    _logger.Warn("Tidal rejected the saved login, starting a new login");
                    streamrip.ForgetTidalLogin(Settings);
                    failures.Add(new ValidationFailure(nameof(Settings.Source), await ContinueTidalLoginAsync(timeout.Token) ?? "Tidal login completed, press Test again."));
                }
            }
            catch (PythonWorkerException ex)
            {
                _logger.Warn("Streamrip could not log in to {0}: {1}\n{2}", Settings.SourceType, ex.Message, ex.PythonTraceback);
                failures.Add(new ValidationFailure(nameof(Settings.Secret), $"Could not log in to {Settings.SourceType}: {ex.PythonType} {ex.Message}".Trim()));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Preparing streamrip failed");
                failures.Add(new ValidationFailure(nameof(Settings.InstallDirectory), ex.Message));
            }
        }

        private async Task<string?> ContinueTidalLoginAsync(CancellationToken token)
        {
            string key = Settings.InstallDirectory ?? string.Empty;

            if (PendingTidalLogins.TryGetValue(key, out (StreamripDeviceLogin Login, DateTime Started) pending) && DateTime.UtcNow - pending.Started < TidalLoginLifetime)
            {
                bool? ready = await streamrip.PollTidalLoginAsync(Settings, pending.Login.DeviceCode, token);
                if (ready == true)
                {
                    PendingTidalLogins.TryRemove(key, out _);
                    _logger.Info("Tidal login completed");
                    return null;
                }

                if (ready == null)
                    return TidalInstruction(pending.Login.Url);
            }

            StreamripDeviceLogin login = await streamrip.StartTidalLoginAsync(Settings, token);
            PendingTidalLogins[key] = (login, DateTime.UtcNow);
            return TidalInstruction(login.Url);
        }

        private static string TidalInstruction(string url) =>
            $"Log in to Tidal: open {url} in your browser, approve the login, then press Test again within 5 minutes.";

        private async Task<IList<ReleaseInfo>> SearchAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return [];

            try
            {
                IReadOnlyList<StreamripAlbum> albums = await streamrip.SearchAsync(Settings, query);
                _logger.Debug("Streamrip found {0} album(s) on {1} for '{2}'", albums.Count, Settings.SourceType, query);
                IList<ReleaseInfo> releases = CleanupReleases(albums.Select(ToRelease));
                _indexerStatusService.RecordSuccess(Definition.Id);
                return releases;
            }
            catch (PythonWorkerException ex)
            {
                _indexerStatusService.RecordFailure(Definition.Id);
                _logger.Warn("Streamrip search on {0} failed for '{1}': {2}", Settings.SourceType, query, ex.Message);
                return [];
            }
        }

        private ReleaseInfo ToRelease(StreamripAlbum album)
        {
            (AudioFormat codec, int bitrate, int bitDepth, int sampleRate) = GetQuality(album);

            AlbumData data = new(Name, nameof(StreamripDownloadProtocol))
            {
                Guid = $"streamrip-{Settings.SourceName}-{album.Type}-{album.Id}",
                AlbumId = $"{Settings.SourceName}/{album.Type}/{album.Id}",
                AlbumName = album.Title,
                ArtistName = album.Artist,
                InfoUrl = album.Url,
                TotalTracks = Math.Max(album.Tracks, 1),
                ExplicitContent = album.Explicit,
                Duration = album.Duration,
                Codec = codec,
                Bitrate = bitrate,
                BitDepth = bitDepth,
                SampleRate = sampleRate,
                SourceTag = Settings.SourceType.ToString(),
                CustomString = album.Cover
            };

            if (DateTime.TryParse(album.Date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime released))
                data.ReleaseDateTime = released;

            return data.ToReleaseInfo();
        }

        private (AudioFormat Codec, int Bitrate, int BitDepth, int SampleRate) GetQuality(StreamripAlbum album)
        {
            StreamripQuality quality = (StreamripQuality)Settings.Quality;

            return Settings.SourceType switch
            {
                StreamripSource.SoundCloud => (AudioFormat.MP3, 128, 0, 0),
                StreamripSource.Tidal when quality == StreamripQuality.Lossy => (AudioFormat.AAC, 320, 0, 0),
                _ when quality == StreamripQuality.Lossy => (AudioFormat.MP3, 320, 0, 0),
                StreamripSource.Deezer => (AudioFormat.FLAC, 1411, 16, 44100),
                _ when quality == StreamripQuality.Lossless || album.BitDepth <= 16 => (AudioFormat.FLAC, 1411, 16, 44100),
                _ => (AudioFormat.FLAC, 2304, album.BitDepth, (int)Math.Round(Math.Min(album.SampleRate, quality == StreamripQuality.HiRes ? 96 : 192) * 1000))
            };
        }
    }
}
