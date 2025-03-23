﻿using FluentValidation;
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

            // Validate poToken (optional)
            RuleFor(x => x.PoToken)
                .Length(32, 256)
                .When(x => !string.IsNullOrEmpty(x.PoToken))
                .WithMessage("Proof of Origin (poToken) must be between 32 and 128 characters if provided.");

            // Validate TrustedSessionGeneratorUrl (optional)
            RuleFor(x => x.TrustedSessionGeneratorUrl)
                .Must(url => string.IsNullOrEmpty(url) || Uri.IsWellFormedUriString(url, UriKind.Absolute))
                .WithMessage("Trusted Session Generator URL must be a valid URL if provided.");
        }
    }

    public class YoutubeIndexerSettings : IIndexerSettings
    {
        private static readonly YoutubeIndexerSettingsValidator Validator = new();

        [FieldDefinition(0, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; } = null;

        [FieldDefinition(1, Label = "Cookie Path", Type = FieldType.FilePath, Hidden = HiddenType.Visible, Placeholder = "/path/to/cookies.txt", HelpText = "Specify the path to the YouTube cookies file. This is optional but helps with accessing restricted content.", Advanced = true)]
        public string CookiePath { get; set; } = string.Empty;

        [FieldDefinition(2, Label = "PoToken", Type = FieldType.Textbox, HelpText = "A unique token to verify the origin of the request.", Advanced = true)]
        public string PoToken { get; set; } = string.Empty;

        [FieldDefinition(3, Label = "Visitor Data", Type = FieldType.Textbox, HelpText = "YouTube session data.", Advanced = true)]
        public string VisitorData { get; set; } = string.Empty;

        [FieldDefinition(4, Label = "Generator URL", Type = FieldType.Textbox, Placeholder = "http://localhost:8080", HelpText = "URL to the YouTube Trusted Session Generator service. When provided, PoToken and Visitor Data will be fetched automatically.", Advanced = true)]
        public string TrustedSessionGeneratorUrl { get; set; } = string.Empty;

        public string BaseUrl { get; set; } = string.Empty;

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }
}