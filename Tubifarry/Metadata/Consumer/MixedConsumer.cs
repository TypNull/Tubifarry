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
    public class MetaMixConsumerSettingssValidator : AbstractValidator<MetaMixConsumerSettings> { }

    public class MetaMixConsumerSettings : IProviderConfig
    {
        private static readonly MetaMixConsumerSettingssValidator Validator = new();

        public MetaMixConsumerSettings() { }


        [FieldDefinition(0, Label = "Track Metadatas", Type = FieldType.Checkbox, Section = "Test")]
        public bool TrackMetadata { get; set; }

        public NzbDroneValidationResult Validate() => new(Validator.Validate(this));
    }

    public class MixedConsumer : ConsumerProxyPlaceholder<MetaMixConsumerSettings>, IMetadata
    {
        public override string Name => "MetaMix";
        private readonly Logger _logger;

        public MixedConsumer(Logger logger)
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
