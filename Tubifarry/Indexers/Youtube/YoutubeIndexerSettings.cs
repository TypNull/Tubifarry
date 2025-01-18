﻿
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Validation;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Indexers.Youtube
{

    public class YoutubeIndexerSettingsValidator : AbstractValidator<YoutubeIndexerSettings>
    {
        public YoutubeIndexerSettingsValidator()
        {
            // Validate CookiePath (if provided)
            RuleFor(x => x.CookiePath)
                .Must(path => string.IsNullOrEmpty(path) || File.Exists(path))
                .WithMessage("Cookie file does not exist. Please provide a valid path to the cookies file.")
                .Must(path => string.IsNullOrEmpty(path) || CookieManager.ParseCookieFile(path).Any())
                .WithMessage("Cookie file is invalid or contains no valid cookies.");
        }
    }

    public class YoutubeIndexerSettings : IIndexerSettings
    {
        private static readonly YoutubeIndexerSettingsValidator Validator = new();

        [FieldDefinition(0, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; } = null;

        [FieldDefinition(1, Label = "Cookie Path", Type = FieldType.FilePath, Placeholder = "/path/to/cookies.txt", HelpText = "Specify the path to the Spotify cookies file. This is optional but required for accessing restricted content.", Advanced = true)]
        public string CookiePath { get; set; } = string.Empty;

        public string BaseUrl { get; set; } = string.Empty;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}
