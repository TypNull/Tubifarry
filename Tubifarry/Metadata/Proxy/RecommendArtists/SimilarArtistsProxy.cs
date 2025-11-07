using NLog;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;
using Tubifarry.Metadata.Proxy.MetadataProvider.Mixed;

namespace Tubifarry.Metadata.Proxy.RecommendArtists
{
    /// <summary>
    /// Proxy for injecting Last.fm similar artists into search results
    /// </summary>
    [Proxy(ProxyMode.Internal)]
    [ProxyFor(typeof(ISearchForNewEntity))]
    public class SimilarArtistsProxy(
        ILastFmSimilarArtistsService lastFmService,
        Logger logger) : ProxyBase<SimilarArtistsProxySettings>, ISupportMetadataMixing
    {
        private readonly ILastFmSimilarArtistsService _lastFmService = lastFmService;
        private readonly Logger _logger = logger;

        public override string Name => "Similar Artists (Last.fm)";

        private static readonly string[] SIMILAR_SEARCH_PREFIX = ["similar:", "~"];

        public List<object> SearchForNewEntity(string title)
        {
            _logger.Trace($"SearchForNewEntity called with: {title}");

            if (string.IsNullOrWhiteSpace(title))
            {
                _logger.Trace("Search title is null or empty");
                return [];
            }

            string? matchedPrefix = SIMILAR_SEARCH_PREFIX.FirstOrDefault(prefix => title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            if (matchedPrefix == null)
            {
                _logger.Trace("Search doesn't use 'similar:' prefix, skipping");
                return [];
            }

            string targetArtistIdentifier = title[matchedPrefix.Length..].Trim();

            if (string.IsNullOrWhiteSpace(targetArtistIdentifier))
            {
                _logger.Trace("No artist identifier provided after prefix");
                return [];
            }

            return _lastFmService
                .GetSimilarArtistsWithMetadata(targetArtistIdentifier, Settings!)
                .Cast<object>()
                .ToList();
        }

        public MetadataSupportLevel CanHandleSearch(string? albumTitle = null, string? artistName = null) =>
            SIMILAR_SEARCH_PREFIX.Any(prefix =>
                albumTitle?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true ||
                artistName?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
            ? MetadataSupportLevel.Supported
            : MetadataSupportLevel.Unsupported;

        public MetadataSupportLevel CanHandleId(string id) => MetadataSupportLevel.Unsupported;

        public MetadataSupportLevel CanHandleIRecordingIds(params string[] recordingIds) => MetadataSupportLevel.Unsupported;

        public MetadataSupportLevel CanHandleChanged() => MetadataSupportLevel.Unsupported;

        public string? SupportsLink(List<Links> links) => null;
    }
}