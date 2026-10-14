using NLog;
using NzbDrone.Core.Download.History;
using System.Text.Json;
using Tubifarry.Download.Clients.Soulseek.Models;
using Tubifarry.Indexers.Soulseek;

namespace Tubifarry.Download.Clients.Soulseek;

public record SlskdGrab(DownloadHistory History, string? Username, HashSet<string> Files);

public class SlskdGrabHistory(SlskdGrabRepository repository, int definitionId, Logger logger)
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan _window = TimeSpan.FromDays(30);
    private const int MaxGrabs = 1000;
    private const int OlderPageSize = 1000;

    private readonly SlskdGrabRepository _repository = repository;
    private readonly int _definitionId = definitionId;
    private readonly Logger _logger = logger;
    private List<SlskdGrab>? _grabs;
    private List<SlskdGrab>? _olderGrabs;
    private readonly Dictionary<int, SlskdGrab?> _resolved = [];

    public List<SlskdGrab> ForUser(string username) =>
        Load().Where(g => IsFor(g, username)).ToList();

    public List<SlskdGrab> OlderForUser(string username) =>
        LoadOlder().Where(g => IsFor(g, username)).ToList();

    public SlskdGrab? Resolve(SlskdGrab grab)
    {
        if (grab.History.Release != null)
            return grab;
        if (_resolved.TryGetValue(grab.History.Id, out SlskdGrab? resolved))
            return resolved;

        resolved = _repository.Find(grab.History.Id) is DownloadHistory history ? grab with { History = history } : null;
        _resolved[grab.History.Id] = resolved;
        return resolved;
    }

    private List<SlskdGrab> LoadOlder()
    {
        if (_olderGrabs != null)
            return _olderGrabs;

        _olderGrabs = [];
        try
        {
            HashSet<int> loaded = Load().Select(g => g.History.Id).ToHashSet();
            int beforeId = int.MaxValue;
            while (true)
            {
                List<DownloadHistory> page = _repository.GetGrabs(_definitionId, h => h.Id < beforeId, OlderPageSize);
                if (page.Count == 0)
                    break;

                beforeId = page[^1].Id;
                _olderGrabs.AddRange(ToGrabs(page.Where(h => !loaded.Contains(h.Id)))
                    .Select(g => g with { History = Stub(g.History) }));
            }

            _logger.Trace($"[def={_definitionId}] Indexed {_olderGrabs.Count} older slskd grabs from download history");
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, $"[def={_definitionId}] Reading older slskd grab history failed");
        }

        return _olderGrabs;
    }

    private static DownloadHistory Stub(DownloadHistory history) => new()
    {
        Id = history.Id,
        EventType = history.EventType,
        DownloadId = history.DownloadId,
        SourceTitle = history.SourceTitle,
        Date = history.Date,
        DownloadClientId = history.DownloadClientId,
        Release = null!
    };

    private static bool IsFor(SlskdGrab grab, string username) =>
        grab.Username == null || string.Equals(grab.Username, username, StringComparison.OrdinalIgnoreCase);

    private List<SlskdGrab> Load()
    {
        if (_grabs != null)
            return _grabs;

        _grabs = [];
        try
        {
            DateTime since = DateTime.UtcNow - _window;
            _grabs = ToGrabs(_repository.GetGrabs(_definitionId, h => h.Date >= since, MaxGrabs));
            _logger.Trace($"[def={_definitionId}] Loaded {_grabs.Count} slskd grabs from download history");
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, $"[def={_definitionId}] Reading slskd grab history failed");
        }

        return _grabs;
    }

    private static List<SlskdGrab> ToGrabs(IEnumerable<DownloadHistory> history)
    {
        List<SlskdGrab> grabs = [];
        foreach (DownloadHistory grab in history)
        {
            if (grab.Release?.Source is not { Length: > 0 } source || source[0] != '[')
                continue;

            List<SlskdFileData> files;
            try
            {
                files = JsonSerializer.Deserialize<List<SlskdFileData>>(source, _jsonOptions) ?? [];
            }
            catch (JsonException)
            {
                continue;
            }

            string? username = string.IsNullOrEmpty(grab.Release.DownloadUrl) ? null : SlskdDownloadItem.GetUsername(grab.Release.DownloadUrl);
            grabs.Add(new SlskdGrab(grab, username, SlskdGrabMatcher.ToNameSet(files.Select(f => f.Filename))));
        }
        return grabs;
    }
}
