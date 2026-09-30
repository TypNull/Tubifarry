using NLog;
using NzbDrone.Core.Download.History;
using System.Collections.Concurrent;
using Tubifarry.Download.Clients.Soulseek.Models;

namespace Tubifarry.Download.Clients.Soulseek;

public record SlskdTransferGroup(SlskdDownloadDirectory Directory, SlskdDownloadItem? Owner, DownloadHistory? Grab);

public class SlskdGrabMatcher(Logger logger)
{
    private static readonly TimeSpan _grabTolerance = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _unmatchedRetention = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _removedRetention = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, DateTime> _unmatchedHashes = new();
    private readonly ConcurrentDictionary<string, (HashSet<string> Names, DateTime RemovedAt)> _removedGrabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Logger _logger = logger;

    public static HashSet<string> ToNameSet(IEnumerable<string?> filenames) =>
        filenames.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool ContainsAll(HashSet<string> names, IReadOnlyCollection<string> filenames) =>
        filenames.Count > 0 && filenames.All(names.Contains);

    public SlskdDownloadItem? FindOwner(IEnumerable<SlskdDownloadItem> items, string username, string filename, string? batchId, DateTime requestedAt) =>
        SelectOwner(GetOwners(items, username), filename, batchId, ToUtc(requestedAt));

    public void MarkRemoved(SlskdDownloadItem item) =>
        _removedGrabs[item.ID] = (ToNameSet(item.FileData.Select(f => f.Filename)), DateTime.UtcNow);

    public bool IsRemoved(string downloadId) => _removedGrabs.ContainsKey(downloadId);

    public List<SlskdTransferGroup> Assign(string username, IEnumerable<SlskdDownloadDirectory> directories, IEnumerable<SlskdDownloadItem> items, SlskdGrabHistory history)
    {
        PruneExpired();
        List<(SlskdDownloadItem Item, HashSet<string> Names)> owners = GetOwners(items, username);
        List<(SlskdDownloadDirectory Directory, string Key, SlskdDownloadItem? Owner, DownloadHistory? Grab, List<SlskdDownloadFile> Files)> parts = [];

        foreach (SlskdDownloadDirectory dir in directories)
        {
            if (dir.Files is not { Count: > 0 } files)
                continue;

            List<SlskdDownloadFile> unowned = [];
            foreach (IGrouping<SlskdDownloadItem?, SlskdDownloadFile> group in files.GroupBy(f => SelectOwner(owners, f.Filename, f.BatchId, ToUtc(f.RequestedAt))))
            {
                if (group.Key == null)
                    unowned.AddRange(group.Where(f => !BelongsToRemovedGrab(f)));
                else
                    parts.Add((dir, group.Key.ID, group.Key, null, group.ToList()));
            }

            if (unowned.Count == 0)
                continue;

            foreach ((SlskdGrab? grab, List<SlskdDownloadFile> grabFiles) in MatchGrabs(username, unowned, history))
                parts.Add((dir, grab?.History.DownloadId ?? $"{dir.Directory}|{parts.Count}", null, grab?.History, grabFiles));
        }

        return parts
            .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                List<SlskdDownloadFile> files = group.SelectMany(p => p.Files).ToList();
                SlskdDownloadDirectory first = group.First().Directory;
                SlskdDownloadDirectory directory = first.Files?.Count == files.Count && group.Count() == 1 ? first : first with { FileCount = files.Count, Files = files };
                return new SlskdTransferGroup(directory, group.Select(p => p.Owner).FirstOrDefault(o => o != null), group.Select(p => p.Grab).FirstOrDefault(g => g != null));
            })
            .ToList();
    }

    private List<(SlskdGrab? Grab, List<SlskdDownloadFile> Files)> MatchGrabs(string username, List<SlskdDownloadFile> files, SlskdGrabHistory history)
    {
        string hash = SlskdDownloadItem.GetStableMD5Id(files.Select(f => f.Filename));
        if (_unmatchedHashes.TryGetValue(hash, out DateTime lastAttempt) && DateTime.UtcNow - lastAttempt < _unmatchedRetention)
            return [(null, files)];

        List<SlskdGrab> grabs = history.ForUser(username);
        IEnumerable<List<SlskdDownloadFile>> units = files
            .Where(f => f.BatchId != null)
            .GroupBy(f => f.BatchId!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.ToList())
            .Concat(files.Where(f => f.BatchId == null).Select(f => new List<SlskdDownloadFile> { f }));

        List<(SlskdGrab? Grab, List<SlskdDownloadFile> Files)> matches = units
            .Select(unit => (Grab: FindGrab(grabs, unit), Files: unit))
            .GroupBy(m => m.Grab)
            .Select(group => (group.Key, group.SelectMany(m => m.Files).ToList()))
            .ToList();

        List<SlskdDownloadFile> unmatched = matches.Where(m => m.Grab == null).SelectMany(m => m.Files).ToList();
        if (unmatched.Count > 0)
            _unmatchedHashes[SlskdDownloadItem.GetStableMD5Id(unmatched.Select(f => f.Filename))] = DateTime.UtcNow;

        foreach ((SlskdGrab? grab, List<SlskdDownloadFile> grabFiles) in matches.Where(m => m.Grab != null))
            _logger.Debug($"Matched {grabFiles.Count} slskd transfers from {username} to grab {grab!.History.DownloadId} ({grab.History.SourceTitle}, {grab.History.Date:u})");

        return matches;
    }

    private SlskdGrab? FindGrab(List<SlskdGrab> grabs, List<SlskdDownloadFile> unit)
    {
        List<string> names = unit.Select(f => f.Filename).ToList();
        return PickNewest(grabs.Where(g => !_removedGrabs.ContainsKey(g.History.DownloadId) && ContainsAll(g.Files, names)), g => g.History.Date, GetEarliestRequest(unit));
    }

    private bool BelongsToRemovedGrab(SlskdDownloadFile file)
    {
        DateTime requestedAt = ToUtc(file.RequestedAt);
        return _removedGrabs.Values.Any(r => r.Names.Contains(file.Filename) && (requestedAt == DateTime.MinValue || requestedAt <= r.RemovedAt + _grabTolerance));
    }

    private void PruneExpired()
    {
        DateTime now = DateTime.UtcNow;
        foreach (KeyValuePair<string, DateTime> entry in _unmatchedHashes.Where(e => now - e.Value >= _unmatchedRetention).ToList())
            _unmatchedHashes.TryRemove(entry.Key, out _);
        foreach (KeyValuePair<string, (HashSet<string> Names, DateTime RemovedAt)> entry in _removedGrabs.Where(e => now - e.Value.RemovedAt >= _removedRetention).ToList())
            _removedGrabs.TryRemove(entry.Key, out _);
    }

    private static List<(SlskdDownloadItem Item, HashSet<string> Names)> GetOwners(IEnumerable<SlskdDownloadItem> items, string username) =>
        items
            .Where(i => i.Username == null || username.Length == 0 || string.Equals(i.Username, username, StringComparison.OrdinalIgnoreCase))
            .Select(i => (i, ToNameSet(i.FileData.Select(f => f.Filename))))
            .ToList();

    private static SlskdDownloadItem? SelectOwner(List<(SlskdDownloadItem Item, HashSet<string> Names)> owners, string filename, string? batchId, DateTime requestedAt)
    {
        if (batchId != null && owners.FirstOrDefault(o => string.Equals(o.Item.BatchId, batchId, StringComparison.OrdinalIgnoreCase)).Item is SlskdDownloadItem batchOwner)
            return batchOwner;

        return PickNewest(owners.Where(o => o.Names.Contains(filename)).Select(o => o.Item), i => i.GrabbedAt, requestedAt);
    }

    private static T? PickNewest<T>(IEnumerable<T> candidates, Func<T, DateTime> getDate, DateTime transferTime) where T : class
    {
        List<T> ordered = candidates.OrderByDescending(getDate).ToList();
        return transferTime == DateTime.MinValue
            ? ordered.FirstOrDefault()
            : ordered.FirstOrDefault(c => getDate(c) <= transferTime + _grabTolerance);
    }

    private static DateTime GetEarliestRequest(IEnumerable<SlskdDownloadFile> files) =>
        files
            .Select(f => ToUtc(f.RequestedAt))
            .Where(t => t != DateTime.MinValue)
            .DefaultIfEmpty(DateTime.MinValue)
            .Min();

    private static DateTime ToUtc(DateTime value) => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
}
