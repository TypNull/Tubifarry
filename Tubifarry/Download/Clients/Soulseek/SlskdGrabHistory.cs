using NLog;
using NzbDrone.Core.Download.History;
using System.Text.Json;
using Tubifarry.Download.Clients.Soulseek.Models;
using Tubifarry.Indexers.Soulseek;

namespace Tubifarry.Download.Clients.Soulseek;

public record SlskdGrab(DownloadHistory History, string? Username, HashSet<string> Files);

public class SlskdGrabHistory(IDownloadHistoryRepository repository, int definitionId, Logger logger)
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IDownloadHistoryRepository _repository = repository;
    private readonly int _definitionId = definitionId;
    private readonly Logger _logger = logger;
    private List<SlskdGrab>? _grabs;

    public List<SlskdGrab> ForUser(string username) =>
        Load()
            .Where(g => g.Username == null || string.Equals(g.Username, username, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private List<SlskdGrab> Load()
    {
        if (_grabs != null)
            return _grabs;

        _grabs = [];
        try
        {
            foreach (DownloadHistory grab in _repository.All()
                .Where(h => h.EventType == DownloadHistoryEventType.DownloadGrabbed && h.DownloadClientId == _definitionId))
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
                _grabs.Add(new SlskdGrab(grab, username, SlskdGrabMatcher.ToNameSet(files.Select(f => f.Filename))));
            }

            _logger.Trace($"[def={_definitionId}] Loaded {_grabs.Count} slskd grabs from download history");
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, $"[def={_definitionId}] Reading slskd grab history failed");
        }

        return _grabs;
    }
}
