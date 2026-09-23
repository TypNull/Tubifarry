using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace Tubifarry.Notifications.ReplayGain;

public class ReplayGainSettingsValidator : AbstractValidator<ReplayGainSettings>
{
    public ReplayGainSettingsValidator()
    {
        // Install Directory validation
        RuleFor(x => x.InstallDirectory)
            .Must(path => string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            .WithMessage("Install directory must be an absolute path or empty.");

        // Target Loudness validation
        RuleFor(x => x.TargetLoudness)
            .InclusiveBetween(-30, -5)
            .WithMessage("Target loudness must be between -30 and -5 LUFS.");

        // Timeout validation
        RuleFor(x => x.TimeoutMinutes)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Timeout must be at least 1 minute.");
    }
}

public class ReplayGainSettings : IProviderConfig
{
    private static readonly ReplayGainSettingsValidator Validator = new();

    [FieldDefinition(0, Label = "Install Directory", Type = FieldType.Path, Placeholder = "/config/plugins/TypNull/Tubifarry/rsgain", HelpText = "Directory containing the rsgain binary. Leave empty to use the plugin directory.")]
    public string InstallDirectory { get; set; } = string.Empty;

    [FieldDefinition(1, Label = "Download Automatically", Type = FieldType.Checkbox, HelpText = "Download rsgain into the install directory when no usable binary is found. Uses the official release on Windows, macOS and Linux x64, and apk without root on Alpine. Disable if rsgain is provided by the system or PATH.")]
    public bool AutoDownload { get; set; } = true;

    [FieldDefinition(2, Label = "Album Gain", Type = FieldType.Checkbox, HelpText = "Calculate album gain and peak across all tracks of the album.")]
    public bool AlbumGain { get; set; } = true;

    [FieldDefinition(3, Label = "Skip Existing", Type = FieldType.Checkbox, HelpText = "Do not scan files that already have ReplayGain tags.")]
    public bool SkipExisting { get; set; }

    [FieldDefinition(4, Label = "Clipping Protection", Type = FieldType.Select, SelectOptions = typeof(ReplayGainClipMode), HelpText = "Lower the gain when it would cause clipping.")]
    public int ClipMode { get; set; } = (int)ReplayGainClipMode.Positive;

    [FieldDefinition(5, Label = "True Peak", Type = FieldType.Checkbox, HelpText = "Use true peak instead of sample peak.", Advanced = true)]
    public bool TruePeak { get; set; }

    [FieldDefinition(6, Label = "Target Loudness", Type = FieldType.Number, Unit = "LUFS", Placeholder = "-18", HelpText = "Reference loudness. ReplayGain 2.0 uses -18 LUFS.", Advanced = true)]
    public int TargetLoudness { get; set; } = -18;

    [FieldDefinition(7, Label = "Opus Mode", Type = FieldType.Select, SelectOptions = typeof(ReplayGainOpusMode), HelpText = "How gain is written to Opus files.", Advanced = true)]
    public int OpusMode { get; set; } = (int)ReplayGainOpusMode.Standard;

    [FieldDefinition(8, Label = "Timeout", Type = FieldType.Number, Unit = "minutes", Placeholder = "30", HelpText = "Maximum time rsgain may take for one album.", Advanced = true)]
    public int TimeoutMinutes { get; set; } = 30;

    public NzbDroneValidationResult Validate() => new(Validator.Validate(this));

    public ReplayGainSettings Clone() => (ReplayGainSettings)MemberwiseClone();
}

public enum ReplayGainClipMode
{
    [FieldOption(Label = "Disabled")]
    Disabled = 0,

    [FieldOption(Label = "Positive Gain Only")]
    Positive = 1,

    [FieldOption(Label = "Always")]
    Always = 2
}

public enum ReplayGainOpusMode
{
    [FieldOption(Label = "Standard ReplayGain Tags")]
    Standard = 0,

    [FieldOption(Label = "R128 Tags")]
    R128 = 1,

    [FieldOption(Label = "R128 Tags (-23 LUFS)")]
    R128Standard = 2,

    [FieldOption(Label = "Header Gain (Track)")]
    HeaderTrack = 3,

    [FieldOption(Label = "Header Gain (Album)")]
    HeaderAlbum = 4
}
