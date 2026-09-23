using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using System.Threading.Channels;
using Tubifarry.Core.Rsgain;

namespace Tubifarry.Notifications.ReplayGain;

public interface IReplayGainService
{
    void Enqueue(Album album, ReplayGainSettings settings);
    RsgainExecutable GetExecutable(ReplayGainSettings settings);
}

public sealed class ReplayGainService(IRsgainInstallation rsgainInstallation, IMediaFileService mediaFileService, Logger logger) : IReplayGainService, IHandle<ApplicationShutdownRequested>
{
    private sealed record ReplayGainRequest(int AlbumId, string AlbumTitle, ReplayGainSettings Settings);

    private readonly Channel<ReplayGainRequest> _queue = Channel.CreateUnbounded<ReplayGainRequest>();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _workerLock = new();

    private Task? _worker;

    public void Enqueue(Album album, ReplayGainSettings settings)
    {
        if (!_queue.Writer.TryWrite(new ReplayGainRequest(album.Id, album.Title, settings.Clone())))
        {
            logger.Warn("ReplayGain queue is closed, skipping '{0}'", album.Title);
            return;
        }

        logger.Debug("Queued ReplayGain for '{0}'", album.Title);

        lock (_workerLock)
            _worker ??= Task.Run(ProcessQueueAsync);
    }

    public RsgainExecutable GetExecutable(ReplayGainSettings settings) =>
        GetExecutableAsync(settings, CancellationToken.None).GetAwaiter().GetResult();

    public void Handle(ApplicationShutdownRequested message)
    {
        _queue.Writer.TryComplete();

        if (_queue.Reader.Count > 0)
            logger.Info("Shutting down, {0} queued ReplayGain album(s) will not be processed", _queue.Reader.Count);

        _shutdown.Cancel();
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (ReplayGainRequest request in _queue.Reader.ReadAllAsync(_shutdown.Token))
            {
                try
                {
                    await ProcessAlbumAsync(request, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Failed to write ReplayGain tags for '{0}'", request.AlbumTitle);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            logger.Debug("ReplayGain worker stopped");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "ReplayGain worker stopped unexpectedly");
            lock (_workerLock)
                _worker = null;
        }
    }

    private async Task ProcessAlbumAsync(ReplayGainRequest request, CancellationToken token)
    {
        List<string> files = mediaFileService.GetFilesByAlbum(request.AlbumId)
            .Select(file => file.Path)
            .Where(File.Exists)
            .Distinct()
            .ToList();

        if (files.Count == 0)
        {
            logger.Debug("No files found for '{0}', skipping ReplayGain", request.AlbumTitle);
            return;
        }

        RsgainExecutable executable = await GetExecutableAsync(request.Settings, token);
        List<string> arguments = BuildArguments(request.Settings, files);

        logger.Debug("Running rsgain on {0} file(s) of '{1}'", files.Count, request.AlbumTitle);
        RsgainResult result = await rsgainInstallation.RunAsync(executable, arguments, TimeSpan.FromMinutes(request.Settings.TimeoutMinutes), token);

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            logger.Debug("rsgain output for '{0}':\n{1}", request.AlbumTitle, result.StandardOutput.TrimEnd());

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"rsgain exited with code {result.ExitCode}: {result.StandardError.Trim()}");

        logger.Info("ReplayGain tags written for {0} file(s) of '{1}'", files.Count, request.AlbumTitle);
    }

    private static List<string> BuildArguments(ReplayGainSettings settings, List<string> files)
    {
        List<string> arguments = ["custom", "--tagmode=i", "--output", "--quiet"];

        if (settings.AlbumGain)
            arguments.Add("--album");

        if (settings.SkipExisting)
            arguments.Add("--skip-existing");

        if (settings.TruePeak)
            arguments.Add("--true-peak");

        arguments.Add($"--clip-mode={(ReplayGainClipMode)settings.ClipMode switch
        {
            ReplayGainClipMode.Disabled => "n",
            ReplayGainClipMode.Always => "a",
            _ => "p"
        }}");

        arguments.Add($"--opus-mode={(ReplayGainOpusMode)settings.OpusMode switch
        {
            ReplayGainOpusMode.R128 => "r",
            ReplayGainOpusMode.R128Standard => "s",
            ReplayGainOpusMode.HeaderTrack => "t",
            ReplayGainOpusMode.HeaderAlbum => "a",
            _ => "d"
        }}");

        arguments.Add($"--loudness={settings.TargetLoudness}");
        arguments.Add("--");
        arguments.AddRange(files);
        return arguments;
    }

    private async Task<RsgainExecutable> GetExecutableAsync(ReplayGainSettings settings, CancellationToken token)
    {
        string? installDirectory = string.IsNullOrWhiteSpace(settings.InstallDirectory) ? null : settings.InstallDirectory;

        RsgainExecutable? executable = rsgainInstallation.Find(installDirectory);
        if (executable != null)
            return executable;

        if (!settings.AutoDownload)
            throw new InvalidOperationException("No usable rsgain binary was found in the install directory, plugin directory or PATH. Enable 'Download Automatically' or install rsgain manually.");

        return await rsgainInstallation.InstallAsync(installDirectory, token);
    }
}
