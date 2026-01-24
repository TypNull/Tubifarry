using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Music;

namespace Tubifarry.Indexers.Soulseek.Search.Core;

[Flags]
public enum QueryType
{
    Normal = 0,
    SelfTitled = 1 << 0,
    ShortName = 1 << 1,
    VariousArtists = 1 << 2,
    HasVolume = 1 << 3,
    HasRomanNumeral = 1 << 4,
    NeedsNormalization = 1 << 5,
    NeedsTypeDisambiguation = 1 << 6
}

public sealed record SearchContext
{
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Year { get; init; }
    public bool Interactive { get; init; }
    public int TrackCount { get; init; }
    public PrimaryAlbumType PrimaryType { get; init; }
    public IReadOnlyList<string> Aliases { get; init; }
    public IReadOnlyList<string> Tracks { get; init; }
    public SlskdSettings Settings { get; init; }
    public HashSet<string> ProcessedSearches { get; init; }
    public SearchCriteriaBase? SearchCriteria { get; init; }

    public QueryType QueryType { get; init; } = QueryType.Normal;
    public string? NormalizedArtist { get; init; }
    public string? NormalizedAlbum { get; init; }

    public string? SearchArtist => IsVariousArtists ? null : (NormalizedArtist ?? Artist);

    public string? SearchAlbum => NormalizedAlbum ?? Album;

    public bool IsVariousArtists => QueryType.HasFlag(QueryType.VariousArtists);
    public bool IsSelfTitled => QueryType.HasFlag(QueryType.SelfTitled);
    public bool IsShortName => QueryType.HasFlag(QueryType.ShortName);
    public bool HasValidYear => !string.IsNullOrEmpty(Year) && Year != "0";
    public bool NeedsTypeDisambiguation => QueryType.HasFlag(QueryType.NeedsTypeDisambiguation);

    public string? ReleaseTypeTag => PrimaryType switch
    {
        var t when t == PrimaryAlbumType.EP => "EP",
        var t when t == PrimaryAlbumType.Single => "Single",
        _ => null
    };

    public SearchContext(
        string? Artist,
        string? Album,
        string? Year,
        bool Interactive,
        int TrackCount,
        PrimaryAlbumType PrimaryType,
        IReadOnlyList<string> Aliases,
        IReadOnlyList<string> Tracks,
        SlskdSettings Settings,
        HashSet<string> ProcessedSearches,
        SearchCriteriaBase? SearchCriteria = null)
    {
        this.Artist = Artist;
        this.Album = Album;
        this.Year = Year;
        this.Interactive = Interactive;
        this.TrackCount = TrackCount;
        this.PrimaryType = PrimaryType;
        this.Aliases = Aliases;
        this.Tracks = Tracks;
        this.Settings = Settings;
        this.ProcessedSearches = ProcessedSearches;
        this.SearchCriteria = SearchCriteria;
    }
}

public sealed record SearchQuery
{
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public bool Interactive { get; init; }
    public bool ExpandDirectory { get; init; }
    public int TrackCount { get; init; }
    public string? SearchText { get; init; }

    public static SearchQuery FromContext(SearchContext context) => new()
    {
        Artist = context.SearchArtist,
        Album = context.SearchAlbum,
        Interactive = context.Interactive,
        ExpandDirectory = false,
        TrackCount = context.TrackCount,
        SearchText = null
    };
}

public delegate IEnumerable<IndexerRequest> SearchExecutor(SearchQuery query);

public enum SearchTier
{
    Special = 0,
    Base = 1,
    Variation = 2,
    Fallback = 3
}
