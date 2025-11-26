using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using System.Text;
using System.Text.Json;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.SubSonic;

public interface ISubSonicParser : IParseIndexerResponse
{
    void SetSettings(SubSonicIndexerSettings settings);
}

public class SubSonicIndexerParser(Logger logger, IHttpClient httpClient) : ISubSonicParser
{
    private const string ContentTypeSearch3 = "search3";
    private const string ContentTypeSearch3WithSongs = "search3_with_songs";
    private const string SubSonicStatusOk = "ok";
    private const string JsonFormat = "json";
    private const long DefaultMaxSongSize = 10 * 1024 * 1024; // 10MB

    private readonly Logger _logger = logger;
    private readonly IHttpClient _httpClient = httpClient;
    private SubSonicIndexerSettings? _settings;

    public void SetSettings(SubSonicIndexerSettings settings) => _settings = settings;

    public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
    {
        var releases = new List<ReleaseInfo>();

        try
        {
            var contentType = indexerResponse.Request.HttpRequest.ContentSummary;
            var responseContent = indexerResponse.Content;

            if (contentType == ContentTypeSearch3 || contentType == ContentTypeSearch3WithSongs)
            {
                var includeSongs = contentType == ContentTypeSearch3WithSongs;
                ParseSearch3Response(responseContent, releases, includeSongs);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error parsing SubSonic response");
        }

        return releases;
    }

    private void ParseSearch3Response(string responseContent, List<ReleaseInfo> releases, bool includeSongs)
    {
        var searchWrapper = JsonSerializer.Deserialize<SubSonicResponseWrapper>(
            responseContent,
            IndexerParserHelper.StandardJsonOptions);

        if (searchWrapper?.SubsonicResponse == null)
        {
            _logger.Warn("Invalid search3 response structure");
            return;
        }

        if (!ValidateResponse(searchWrapper.SubsonicResponse))
            return;

        var searchResult = searchWrapper.SubsonicResponse.SearchResult3;
        if (searchResult == null)
        {
            _logger.Warn("No search results in response");
            return;
        }

        ProcessAlbums(searchResult.Albums, releases);

        if (includeSongs)
            ProcessSongs(searchResult.Songs, releases);
    }

    private bool ValidateResponse(SubSonicResponse response)
    {
        if (response.Status != SubSonicStatusOk)
        {
            if (response.Error != null)
            {
                _logger.Warn("SubSonic API error: {Message}", response.Error.Message);
            }
            return false;
        }
        return true;
    }

    private void ProcessAlbums(List<SubSonicSearchAlbum>? albums, List<ReleaseInfo> releases)
    {
        if (albums == null || albums.Count == 0)
            return;

        _logger.Trace("Processing {Count} albums from search3", albums.Count);

        foreach (var albumFromSearch in albums)
        {
            try
            {
                var fullAlbum = FetchFullAlbum(albumFromSearch.Id);
                if (fullAlbum != null)
                {
                    var albumData = CreateAlbumData(fullAlbum);
                    albumData.ParseReleaseDate();
                    releases.Add(albumData.ToReleaseInfo());
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to fetch album details for {AlbumId}: {AlbumName}",
                    albumFromSearch.Id, albumFromSearch.Name);
            }
        }
    }

    private void ProcessSongs(List<SubSonicSearchSong>? songs, List<ReleaseInfo> releases)
    {
        if (songs == null || songs.Count == 0)
        {
            return;
        }

        _logger.Trace("Processing {Count} songs from search3", songs.Count);
        IndexerParserHelper.ProcessItems(songs, CreateTrackData, releases);
    }

    private SubSonicAlbumFull? FetchFullAlbum(string albumId)
    {
        if (_settings == null)
        {
            _logger.Error("Settings not initialized");
            return null;
        }

        var url = BuildAlbumUrl(albumId);
        var request = CreateHttpRequest(url);

        _logger.Trace("Fetching full album details: {AlbumId}", albumId);

        var response = ExecuteRequest(request);
        if (response == null)
        {
            return null;
        }

        return ParseAlbumResponse(response.Content, albumId);
    }

    private string BuildAlbumUrl(string albumId)
    {
        var baseUrl = _settings!.BaseUrl.TrimEnd('/');
        var urlBuilder = new StringBuilder($"{baseUrl}/rest/getAlbum.view");

        urlBuilder.Append($"?id={Uri.EscapeDataString(albumId)}");
        AddAuthParameters(urlBuilder);
        urlBuilder.Append($"&f={JsonFormat}");

        return urlBuilder.ToString();
    }

    private HttpRequest CreateHttpRequest(string url)
    {
        var request = new HttpRequest(url)
        {
            RequestTimeout = TimeSpan.FromSeconds(_settings!.RequestTimeout)
        };
        request.Headers["User-Agent"] = Tubifarry.UserAgent;
        return request;
    }

    private HttpResponse? ExecuteRequest(HttpRequest request)
    {
        var response = _httpClient.ExecuteAsync(request).GetAwaiter().GetResult();

        if (response.HasHttpError)
        {
            _logger.Warn("HTTP error: {StatusCode}", response.StatusCode);
            return null;
        }

        return response;
    }

    private SubSonicAlbumFull? ParseAlbumResponse(string content, string albumId)
    {
        var albumWrapper = JsonSerializer.Deserialize<SubSonicAlbumResponseWrapper>(
            content,
            IndexerParserHelper.StandardJsonOptions);

        if (albumWrapper?.SubsonicResponse?.Status == SubSonicStatusOk &&
            albumWrapper.SubsonicResponse.Album != null)
        {
            return albumWrapper.SubsonicResponse.Album;
        }

        if (albumWrapper?.SubsonicResponse?.Error != null)
        {
            _logger.Warn("SubSonic API error for album {AlbumId}: {Message}",
                albumId, albumWrapper.SubsonicResponse.Error.Message);
        }

        return null;
    }

    private void AddAuthParameters(StringBuilder urlBuilder)
    {
        urlBuilder.Append($"&u={Uri.EscapeDataString(_settings!.Username)}");
        urlBuilder.Append($"&v={Uri.EscapeDataString(SubSonicAuthHelper.ApiVersion)}");
        urlBuilder.Append($"&c={Uri.EscapeDataString(SubSonicAuthHelper.ClientName)}");

        if (_settings.UseTokenAuth)
        {
            var (salt, token) = SubSonicAuthHelper.GenerateToken(_settings.Password);
            urlBuilder.Append($"&t={token}");
            urlBuilder.Append($"&s={salt}");
        }
        else
        {
            urlBuilder.Append($"&p={Uri.EscapeDataString(_settings.Password)}");
        }
    }

    private AlbumData CreateAlbumData(SubSonicAlbumFull album)
    {
        if (album.Songs == null || album.Songs.Count == 0)
        {
            _logger.Warn("Album '{Name}' (ID: {Id}) has no songs", album.Name, album.Id);
            throw new InvalidOperationException($"Album {album.Id} has no songs");
        }

        var firstSong = album.Songs[0];
        var (format, bitrate, bitDepth) = IndexerParserHelper.GetQualityInfo(
            firstSong.Suffix,
            firstSong.ContentType,
            firstSong.BitRate);

        var totalSize = CalculateTotalAlbumSize(album.Songs, bitrate);

        _logger.Trace("Parsed album '{Name}' with {TrackCount} tracks, total size: {Size} bytes, format: {Format} {BitDepth}bit",
            album.Name, album.Songs.Count, totalSize, format, bitDepth);

        return new AlbumData("SubSonic", nameof(SubSonicDownloadProtocol))
        {
            AlbumId = $"{_settings?.BaseUrl}/album/{album.Id}",
            AlbumName = album.Name,
            ArtistName = album.Artist,
            InfoUrl = BuildInfoUrl("album", album.Id),
            TotalTracks = album.SongCount > 0 ? album.SongCount : album.Songs.Count,
            ReleaseDate = album.YearString,
            ReleaseDatePrecision = album.Year.HasValue ? "year" : "day",
            CustomString = album.CoverArt ?? string.Empty,
            Codec = format,
            Bitrate = bitrate,
            BitDepth = bitDepth,
            Size = totalSize
        };
    }

    private static long CalculateTotalAlbumSize(List<SubSonicSearchSong> songs, int bitrate)
    {
        long totalSize = 0;

        foreach (var song in songs)
        {
            var songSize = IndexerParserHelper.EstimateSize(
                song.Size,
                song.Duration,
                bitrate,
                1,
                DefaultMaxSongSize);
            totalSize += songSize;
        }

        return totalSize;
    }

    private AlbumData CreateTrackData(SubSonicSearchSong song)
    {
        var (format, bitrate, bitDepth) = IndexerParserHelper.GetQualityInfo(
            song.Suffix,
            song.ContentType,
            song.BitRate);

        var actualSize = song.Size > 0
            ? song.Size
            : IndexerParserHelper.EstimateSize(0, song.Duration, bitrate, 1, DefaultMaxSongSize);

        return new AlbumData("SubSonic", nameof(SubSonicDownloadProtocol))
        {
            AlbumId = $"{_settings?.BaseUrl}/track/{song.Id}",
            AlbumName = song.DisplayAlbum,
            ArtistName = song.Artist,
            InfoUrl = BuildInfoUrl("track", song.Id),
            TotalTracks = 1,
            ReleaseDate = song.Year?.ToString() ?? DateTime.Now.Year.ToString(),
            ReleaseDatePrecision = "year",
            Duration = song.Duration,
            CustomString = song.CoverArt ?? string.Empty,
            Codec = format,
            Bitrate = bitrate,
            BitDepth = bitDepth,
            Size = actualSize
        };
    }

    private string BuildInfoUrl(string type, string id) =>
        string.IsNullOrWhiteSpace(_settings?.ExternalUrl)
        ? $"{_settings?.BaseUrl}://{type}/{id}"
        : $"{_settings.ExternalUrl.TrimEnd('/')}/rest/browse?type={type}&id={Uri.EscapeDataString(id)}";
}
