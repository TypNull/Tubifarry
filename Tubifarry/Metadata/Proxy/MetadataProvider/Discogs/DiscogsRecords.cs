using System.Text.Json.Serialization;
using Tubifarry.Core.Records;

namespace Tubifarry.Metadata.Proxy.MetadataProvider.Discogs
{
    public record DiscogsArtist(
      [property: JsonPropertyName("id")] int Id,
      [property: JsonPropertyName("name")] string? Name,
      [property: JsonPropertyName("profile")] string? Profile,
      [property: JsonPropertyName("releases_url")] string? ReleasesUrl,
      [property: JsonPropertyName("resource_url")] string? ResourceUrl,
      [property: JsonPropertyName("uri")] string? Uri,
      [property: JsonPropertyName("urls")] List<string>? Urls,
      [property: JsonPropertyName("data_quality")] string? DataQuality,
      [property: JsonPropertyName("namevariations")] List<string>? NameVariations,
      [property: JsonPropertyName("images")] List<DiscogsImage>? Images,
      [property: JsonPropertyName("members")] List<DiscogsMember>? Members,
      [property: JsonPropertyName("join")] string? Join,
      [property: JsonPropertyName("role")] string? Role,
      [property: JsonPropertyName("tracks")] string? Tracks
  ) : MappingAgent;

    public record DiscogsArtistRelease(
        [property: JsonPropertyName("artist")] string? Artist,
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("main_release")] int? MainRelease,
        [property: JsonPropertyName("resource_url")] string? ResourceUrl,
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("thumb")] string? Thumb,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("year")] int Year,
        [property: JsonPropertyName("format")] string? Format,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("status")] string? Status
    ) : MappingAgent;

    public record DiscogsCommunityInfo(
        [property: JsonPropertyName("contributors")] List<DiscogsContributor>? Contributors,
        [property: JsonPropertyName("data_quality")] string? DataQuality,
        [property: JsonPropertyName("have")] int? Have,
        [property: JsonPropertyName("rating")] DiscogsRating? Rating,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("submitter")] DiscogsContributor? Submitter,
        [property: JsonPropertyName("want")] int? Want
    );

    public record DiscogsCompany(
        [property: JsonPropertyName("catno")] string? Catno,
        [property: JsonPropertyName("entity_type")] string? EntityType,
        [property: JsonPropertyName("entity_type_name")] string? EntityTypeName,
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("resource_url")] string? ResourceUrl
    );

    public record DiscogsContributor(
        [property: JsonPropertyName("resource_url")] string? ResourceUrl,
        [property: JsonPropertyName("username")] string? Username
    );

    public record DiscogsFormat(
        [property: JsonPropertyName("descriptions")] List<string>? Descriptions,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("qty")] string? Qty
    );

    public record DiscogsImage(
        [property: JsonPropertyName("height")] int Height,
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("resource_url")] string? ResourceUrl,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("uri")] string? Uri,
        [property: JsonPropertyName("uri150")] string? Uri150
    );

    public record DiscogsIdentifier(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("value")] string? Value
    );

    public record DiscogsLabel(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("catno")] string? Catno,
        [property: JsonPropertyName("entity_type")] string? EntityType,
        [property: JsonPropertyName("entity_type_name")] string? EntityTypeName,
        [property: JsonPropertyName("resource_url")] string? ResourceUrl,
        [property: JsonPropertyName("profile")] string? Profile,
        [property: JsonPropertyName("contact_info")] string? ContactInfo,
        [property: JsonPropertyName("uri")] string? Uri,
        [property: JsonPropertyName("sublabels")] List<DiscogsSublabel>? Sublabels,
        [property: JsonPropertyName("urls")] List<string>? Urls,
        [property: JsonPropertyName("images")] List<DiscogsImage>? Images,
        [property: JsonPropertyName("data_quality")] string? DataQuality
    ) : MappingAgent;

    public record DiscogsLabelRelease(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("artist")] string? Artist,
        [property: JsonPropertyName("catno")] string? Catno,
        [property: JsonPropertyName("format")] string? Format,
        [property: JsonPropertyName("resource_url")] string? ResourceUrl,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("thumb")] string? Thumb,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("year")] string? Year
    ) : MappingAgent;

    public record DiscogsMasterRelease(
        [property: JsonPropertyName("styles")] List<string>? Styles,
        [property: JsonPropertyName("genres")] List<string>? Genres,
        [property: JsonPropertyName("videos")] List<DiscogsVideo>? Videos,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("main_release")] int MainRelease,
        [property: JsonPropertyName("main_release_url")] string? MainReleaseUrl,
        [property: JsonPropertyName("uri")] string? Uri,
        [property: JsonPropertyName("artists")] List<DiscogsArtist>? Artists,
        [property: JsonPropertyName("versions_url")] string? VersionsUrl,
        [property: JsonPropertyName("year")] int Year,
        [property: JsonPropertyName("images")] List<DiscogsImage>? Images,
        [property: JsonPropertyName("resource_url")] string? ResourceUrl,
        [property: JsonPropertyName("tracklist")] List<DiscogsTrack>? Tracklist,
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("num_for_sale")] int NumForSale,
        [property: JsonPropertyName("lowest_price")] decimal? LowestPrice,
        [property: JsonPropertyName("data_quality")] string? DataQuality
    ) : MappingAgent;

    public record DiscogsMasterReleaseVersion(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("stats")] DiscogsStats? Stats,
        [property: JsonPropertyName("thumb")] string? Thumb,
        [property: JsonPropertyName("format")] string? Format,
        [property: JsonPropertyName("country")] string? Country,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("released")] string? Released,
        [property: JsonPropertyName("major_formats")] List<string>? MajorFormats,
        [property: JsonPropertyName("catno")] string? Catno,
        [property: JsonPropertyName("resource_url")] string? ResourceUrl,
        [property: JsonPropertyName("id")] int Id
    ) : MappingAgent;

    public record DiscogsMember(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("active")] bool Active,
        [property: JsonPropertyName("resource_url")] string? ResourceUrl
    );

    public record DiscogsRating(
        [property: JsonPropertyName("average")] decimal Average,
        [property: JsonPropertyName("count")] int Count
    );

    public record DiscogsRelease(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("artists")] List<DiscogsArtist>? Artists,
        [property: JsonPropertyName("data_quality")] string? DataQuality,
        [property: JsonPropertyName("thumb")] string? Thumb,
        [property: JsonPropertyName("community")] DiscogsCommunityInfo? Community,
        [property: JsonPropertyName("companies")] List<DiscogsCompany>? Companies,
        [property: JsonPropertyName("country")] string? Country,
        [property: JsonPropertyName("date_added")] DateTime? DateAdded,
        [property: JsonPropertyName("date_changed")] DateTime? DateChanged,
        [property: JsonPropertyName("estimated_weight")] int? EstimatedWeight,
        [property: JsonPropertyName("extraartists")] List<DiscogsArtist>? ExtraArtists,
        [property: JsonPropertyName("format_quantity")] int? FormatQuantity,
        [property: JsonPropertyName("formats")] List<DiscogsFormat>? Formats,
        [property: JsonPropertyName("genres")] List<string>? Genres,
        [property: JsonPropertyName("identifiers")] List<DiscogsIdentifier>? Identifiers,
        [property: JsonPropertyName("images")] List<DiscogsImage>? Images,
        [property: JsonPropertyName("labels")] List<DiscogsLabel>? Labels,
        [property: JsonPropertyName("lowest_price")] decimal? LowestPrice,
        [property: JsonPropertyName("master_id")] int? MasterId,
        [property: JsonPropertyName("master_url")] string? MasterUrl,
        [property: JsonPropertyName("notes")] string? Notes,
        [property: JsonPropertyName("num_for_sale")] int? NumForSale,
        [property: JsonPropertyName("released")] string? Released,
        [property: JsonPropertyName("released_formatted")] string? ReleasedFormatted,
        [property: JsonPropertyName("resource_url")] string? ResourceUrl,
        [property: JsonPropertyName("series")] List<DiscogsSeries>? Series,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("styles")] List<string>? Styles,
        [property: JsonPropertyName("tracklist")] List<DiscogsTrack>? Tracklist,
        [property: JsonPropertyName("uri")] string? Uri,
        [property: JsonPropertyName("videos")] List<DiscogsVideo>? Videos,
        [property: JsonPropertyName("year")] int? Year
    ) : MappingAgent;

    public record DiscogsSearchItem(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("country")] string? Country,
        [property: JsonPropertyName("format")] List<string>? Format,
        [property: JsonPropertyName("uri")] string? Uri,
        [property: JsonPropertyName("resource_url")] string? ResourceUrl,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("style")] List<string>? Style,
        [property: JsonPropertyName("genre")] List<string>? Genre,
        [property: JsonPropertyName("label")] List<string>? Label,
        [property: JsonPropertyName("catno")] string? Catno,
        [property: JsonPropertyName("year")] string? Year,
        [property: JsonPropertyName("thumb")] string? Thumb,
        [property: JsonPropertyName("community")] DiscogsCommunityInfo? Community
    ) : MappingAgent;

    public record DiscogsSeries(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("catno")] string? Catno,
        [property: JsonPropertyName("entity_type")] string? EntityType,
        [property: JsonPropertyName("entity_type_name")] string? EntityTypeName,
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("resource_url")] string? ResourceUrl,
        [property: JsonPropertyName("thumbnail_url")] string? ThumbnailUrl
    ) : MappingAgent;

    public record DiscogsStats(
        [property: JsonPropertyName("user")] DiscogsUserStats? User,
        [property: JsonPropertyName("community")] DiscogsCommunityStats? Community
    );

    public record DiscogsUserStats(
        [property: JsonPropertyName("in_collection")] int InCollection,
        [property: JsonPropertyName("in_wantlist")] int InWantlist
    );

    public record DiscogsCommunityStats(
        [property: JsonPropertyName("in_collection")] int InCollection,
        [property: JsonPropertyName("in_wantlist")] int InWantlist
    );

    public record DiscogsSublabel(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("resource_url")] string? ResourceUrl
    );

    public record DiscogsTrack(
        [property: JsonPropertyName("duration")] string? Duration,
        [property: JsonPropertyName("position")] string? Position,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("type")] string? Type
    );
    public record DiscogsVideo(
       [property: JsonPropertyName("uri")] string? Uri,
       [property: JsonPropertyName("title")] string? Title,
       [property: JsonPropertyName("description")] string? Description,
       [property: JsonPropertyName("duration")] int Duration,
       [property: JsonPropertyName("embed")] bool Embed
   );

    public record DiscogsSearchParameter(
    string? Query = null,
    string? Type = null,
    string? Title = null,
    string? ReleaseTitle = null,
    string? Credit = null,
    string? Artist = null,
    string? Anv = null,
    string? Label = null,
    string? Genre = null,
    string? Style = null,
    string? Country = null,
    string? Year = null,
    string? Format = null,
    string? Catno = null,
    string? Barcode = null,
    string? Track = null,
    string? Submitter = null,
    string? Contributor = null)
    {
        private static readonly Dictionary<string, string> KeyMappings = new()
    {
        { nameof(Query), "q" },
        { nameof(Type), "type" },
        { nameof(Title), "title" },
        { nameof(ReleaseTitle), "release_title" },
        { nameof(Credit), "credit" },
        { nameof(Artist), "artist" },
        { nameof(Anv), "anv" },
        { nameof(Label), "label" },
        { nameof(Genre), "genre" },
        { nameof(Style), "style" },
        { nameof(Country), "country" },
        { nameof(Year), "year" },
        { nameof(Format), "format" },
        { nameof(Catno), "catno" },
        { nameof(Barcode), "barcode" },
        { nameof(Track), "track" },
        { nameof(Submitter), "submitter" },
        { nameof(Contributor), "contributor" }
    };

        public Dictionary<string, string> ToDictionary() => GetType()
                .GetProperties()
                .Where(prop => prop.PropertyType == typeof(string))
                .Select(prop => (Key: KeyMappings[prop.Name], Value: (string?)prop.GetValue(this)))
                .Where(pair => !string.IsNullOrEmpty(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value!);
    }
}