using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Instrumentation;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;
using System.Text.Json;
using Tubifarry.Indexers.Soulseek;

namespace Tubifarry.Download.Clients.Soulseek.Models;

public class SlskdDownloadItem
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly Logger _logger;

    public string ID { get; set; }
    public List<SlskdFileData> FileData { get; set; } = [];
    public string? Username { get; set; }
    public ReleaseInfo ReleaseInfo { get; set; }

    public event EventHandler<SlskdFileState>? FileStateChanged;

    private SlskdDownloadDirectory? _slskdDownloadDirectory;
    private readonly Dictionary<string, SlskdFileState> _previousFileStates = [];

    public List<Task> PostProcessTasks { get; } = [];
    public DownloadItemStatus? LastReportedStatus { get; set; }
    public string? ConfirmedSubdirectory { get; set; }
    public string? DerivedSubdirectory { get; set; }
    public string? BatchId { get; set; }
    public bool DiscMergeScheduled { get; set; }
    public bool FolderRenameScheduled { get; set; }
    public IReadOnlyDictionary<string, SlskdFileState> FileStates => _previousFileStates;

    public SlskdDownloadDirectory? SlskdDownloadDirectory
    {
        get => _slskdDownloadDirectory;
        set
        {
            if (_slskdDownloadDirectory == value)
                return;
            CompareFileStates(value);
            _slskdDownloadDirectory = value;
        }
    }

    public SlskdDownloadItem(ReleaseInfo releaseInfo)
    {
        _logger = NzbDroneLogger.GetLogger(this);
        ReleaseInfo = releaseInfo;
        FileData = JsonSerializer.Deserialize<List<SlskdFileData>>(ReleaseInfo.Source, _jsonOptions) ?? [];
        ID = GetStableMD5Id(FileData.Select(file => file.Filename));
        _logger.Trace($"Created SlskdDownloadItem with ID: {ID}");
    }

    public static string GetStableMD5Id(IEnumerable<string?> filenames)
    {
        string combined = string.Join("|", filenames.Order());
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(combined);
        return BitConverter.ToString(System.Security.Cryptography.MD5.HashData(bytes)).Replace("-", "").ToLowerInvariant();
    }

    private void CompareFileStates(SlskdDownloadDirectory? newDirectory)
    {
        if (newDirectory?.Files == null)
            return;

        IEnumerable<SlskdDownloadFile> latestFiles = newDirectory.Files
            .GroupBy(f => f.Filename, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(f => f.RequestedAt).First());

        foreach (SlskdDownloadFile file in latestFiles)
        {
            if (_previousFileStates.TryGetValue(file.Filename, out SlskdFileState? fileState) && fileState != null)
            {
                string previousState = fileState.State;
                fileState.UpdateFile(file);
                if (fileState.State != previousState)
                {
                    _logger.Trace($"State change: {Path.GetFileName(file.Filename)} | {previousState} -> {fileState.State}");
                    FileStateChanged?.Invoke(this, fileState);
                }
            }
            else
            {
                SlskdFileState newFileState = new(file);
                _previousFileStates.Add(file.Filename, newFileState);

                DownloadItemStatus initialStatus = SlskdFileState.GetStatus(file.State);
                if (initialStatus == DownloadItemStatus.Failed)
                {
                    _logger.Trace($"Initial state is failed: {Path.GetFileName(file.Filename)} | {file.State}");
                    FileStateChanged?.Invoke(this, newFileState);
                }
            }
        }
    }

    public OsPath GetFullFolderPath(OsPath downloadPath)
    {
        string subdirectory = ConfirmedSubdirectory
            ?? DerivedSubdirectory
            ?? SlskdDownloadDirectory?.Directory
                .Replace('\\', '/')
                .TrimEnd('/')
                .Split('/')
                .LastOrDefault()
            ?? string.Empty;

        return subdirectory.Length == 0
            ? downloadPath
            : downloadPath + new OsPath(subdirectory);
    }
}
