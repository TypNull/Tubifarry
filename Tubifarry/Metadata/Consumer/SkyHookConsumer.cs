using FluentValidation;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using Tubifarry.Proxy;

namespace Tubifarry.Metadata.Consumer
{
    public class SkyHookConsumerSettingssValidator : AbstractValidator<SkyHookConsumerSettings> { }

    public class SkyHookConsumerSettings : IProviderConfig
    {
        private static readonly SkyHookConsumerSettingssValidator Validator = new();

        public SkyHookConsumerSettings() { }


        [FieldDefinition(0, Label = "Track Metadatas", Type = FieldType.Checkbox, Section = "Test")]
        public bool TrackMetadata { get; set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }

    public class SkyHookConsumer : ConsumerProxyPlaceholder<SkyHookConsumerSettings>, IMetadata
    {
        public override string Name => "SkyHook";
        private readonly Logger _logger;

        public SkyHookConsumer(Logger logger)
        {
            _logger = logger;
        }

        public override ValidationResult Test()
        {
            _logger.Info("Test");
            return new();
        }
    }
}
