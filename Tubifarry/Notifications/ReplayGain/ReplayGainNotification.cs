using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.ThingiProvider;
using Tubifarry.Core.Rsgain;

namespace Tubifarry.Notifications.ReplayGain;

public sealed class ReplayGainNotification(IRsgainInstallation rsgainInstallation, IReplayGainService replayGainService, Logger logger) : NotificationBase<ReplayGainSettings>
{
    public override string Name => "ReplayGain";

    public override string Link => "https://github.com/complexlogic/rsgain";

    public override ProviderMessage Message =>
        new("Writes ReplayGain 2.0 tags with rsgain after an album is imported or upgraded.", ProviderMessageType.Info);

    public override void OnReleaseImport(AlbumDownloadMessage message)
    {
        if (message.Album == null)
            return;

        try
        {
            replayGainService.Enqueue(message.Album, Settings);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to queue ReplayGain for '{0}'", message.Album.Title);
        }
    }

    public override ValidationResult Test()
    {
        List<ValidationFailure> failures = [];
        rsgainInstallation.Reset();

        try
        {
            RsgainExecutable executable = replayGainService.GetExecutable(Settings);
            logger.Info("rsgain is ready at {0}", executable.Path);
        }
        catch (Exception ex)
        {
            logger.Warn(ex, "rsgain is not available");
            failures.Add(new ValidationFailure(nameof(Settings.InstallDirectory), ex.Message));
        }

        return new ValidationResult(failures);
    }
}
