using NLog;
using NzbDrone.Common.Instrumentation;
using System.Text.RegularExpressions;
using Tubifarry.Core.Model;
using Tubifarry.Core.Utilities;

namespace Tubifarry.Metadata.Converter
{
    public class ConversionRule
    {
        public AudioFormat SourceFormat { get; set; }
        public ComparisonOperator? SourceBitrateOperator { get; set; }
        public int? SourceBitrateValue { get; set; }
        public AudioFormat TargetFormat { get; set; }
        public int? TargetBitrate { get; set; }
        public bool IsArtistRule { get; set; }

        // Track the type of category rule
        public bool IsGlobalRule { get; set; }

        public bool IsLossyRule { get; set; }
        public bool IsLosslessRule { get; set; }

        public bool IsCategoryRule => IsGlobalRule || IsLossyRule || IsLosslessRule;

        public bool MatchesBitrate(int? currentBitrate)
        {
            if (!HasBitrateConstraints())
                return true;

            if (!currentBitrate.HasValue)
                return false;
            return EvaluateBitrateCondition(currentBitrate.Value);
        }

        public bool MatchesFormat(AudioFormat trackFormat)
        {
            if (IsGlobalRule)
                return true;

            if (IsLossyRule)
                return AudioFormatHelper.IsLossyFormat(trackFormat);

            if (IsLosslessRule)
                return !AudioFormatHelper.IsLossyFormat(trackFormat);

            return SourceFormat == trackFormat;
        }

        private bool HasBitrateConstraints() => SourceBitrateOperator.HasValue && SourceBitrateValue.HasValue;

        private bool EvaluateBitrateCondition(int currentBitrate)
        {
            if (!SourceBitrateOperator.HasValue || !SourceBitrateValue.HasValue)
                return false;

            return SourceBitrateOperator.Value switch
            {
                ComparisonOperator.Equal => currentBitrate == SourceBitrateValue.Value,
                ComparisonOperator.NotEqual => currentBitrate != SourceBitrateValue.Value,
                ComparisonOperator.LessThan => currentBitrate < SourceBitrateValue.Value,
                ComparisonOperator.LessThanOrEqual => currentBitrate <= SourceBitrateValue.Value,
                ComparisonOperator.GreaterThan => currentBitrate > SourceBitrateValue.Value,
                ComparisonOperator.GreaterThanOrEqual => currentBitrate >= SourceBitrateValue.Value,
                _ => false
            };
        }

        private string GetOperatorSymbol() => SourceBitrateOperator.HasValue ? OperatorSymbols.GetSymbol(SourceBitrateOperator.Value) : string.Empty;

        public override string ToString() => $"{FormatSourcePart()}->{FormatTargetPart()}";

        private string FormatSourcePart()
        {
            string source;
            if (IsGlobalRule)
                source = RuleParser.GlobalRuleIdentifier;
            else if (IsLossyRule)
                source = RuleParser.LossyRuleIdentifier;
            else if (IsLosslessRule)
                source = RuleParser.LosslessRuleIdentifier;
            else
                source = SourceFormat.ToString();

            if (HasBitrateConstraints())
                source += GetOperatorSymbol() + SourceBitrateValue!.Value;
            return source;
        }

        private string FormatTargetPart()
        {
            string target = TargetFormat.ToString();
            if (TargetBitrate.HasValue)
                target += ":" + TargetBitrate.Value.ToString();
            return target;
        }
    }

    public static class OperatorSymbols
    {
        public const string Equal = "=";
        public const string NotEqual = "!=";
        public const string LessThan = "<";
        public const string LessThanOrEqual = "<=";
        public const string GreaterThan = ">";
        public const string GreaterThanOrEqual = ">=";

        public static string GetSymbol(ComparisonOperator op)
        {
            return op switch
            {
                ComparisonOperator.Equal => Equal,
                ComparisonOperator.NotEqual => NotEqual,
                ComparisonOperator.LessThan => LessThan,
                ComparisonOperator.LessThanOrEqual => LessThanOrEqual,
                ComparisonOperator.GreaterThan => GreaterThan,
                ComparisonOperator.GreaterThanOrEqual => GreaterThanOrEqual,
                _ => string.Empty
            };
        }

        public static ComparisonOperator? FromSymbol(string symbol)
        {
            return symbol switch
            {
                Equal => ComparisonOperator.Equal,
                NotEqual => ComparisonOperator.NotEqual,
                LessThan => ComparisonOperator.LessThan,
                LessThanOrEqual => ComparisonOperator.LessThanOrEqual,
                GreaterThan => ComparisonOperator.GreaterThan,
                GreaterThanOrEqual => ComparisonOperator.GreaterThanOrEqual,
                _ => null
            };
        }
    }

    public enum ComparisonOperator
    {
        Equal,
        NotEqual,
        LessThan,
        LessThanOrEqual,
        GreaterThan,
        GreaterThanOrEqual
    }

    public static partial class RuleParser
    {
        public const string GlobalRuleIdentifier = "all";
        public const string LossyRuleIdentifier = "lossy";
        public const string LosslessRuleIdentifier = "lossless";
        public const string NoConversionTag = "no-conversion";
        private static readonly Logger _logger = NzbDroneLogger.GetLogger(typeof(RuleParser));

        public static bool TryParseRule(string sourceKey, string targetValue, out ConversionRule rule)
        {
            _logger.Debug("Parsing rule: {0} -> {1}", sourceKey, targetValue);
            rule = new ConversionRule();

            if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(targetValue))
            {
                _logger.Debug("Rule parsing failed: Empty source or target");
                return false;
            }

            return ParseSourcePart(sourceKey.Trim(), rule) && ParseTargetPart(targetValue.Trim(), rule);
        }

        public static bool TryParseArtistTag(string tagLabel, out ConversionRule rule)
        {
            _logger.Debug("Parsing artist tag: {0}", tagLabel);
            rule = new ConversionRule { IsArtistRule = true };

            if (string.IsNullOrWhiteSpace(tagLabel))
                return false;

            // Handle no-conversion tag
            if (string.Equals(tagLabel, NoConversionTag, StringComparison.OrdinalIgnoreCase))
            {
                rule.TargetFormat = AudioFormat.Unknown;
                return true;
            }

            // Match format like "opus" or format+bitrate like "opus192"
            Match match = ArtistTagRegex().Match(tagLabel.Trim());
            if (!match.Success)
            {
                _logger.Debug("Invalid artist tag format: {0}", tagLabel);
                return false;
            }

            string formatName = match.Groups[1].Value;
            if (!Enum.TryParse(formatName, true, out AudioFormat targetFormat))
            {
                _logger.Debug("Invalid format in artist tag: {0}", formatName);
                return false;
            }

            rule.TargetFormat = targetFormat;

            // Parse bitrate if present
            if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out int bitrate))
            {
                int clampedBitrate = AudioFormatHelper.ClampBitrate(targetFormat, bitrate);
                rule.TargetBitrate = AudioFormatHelper.RoundToStandardBitrate(clampedBitrate);
            }

            return true;
        }

        private static bool ParseSourcePart(string sourceKey, ConversionRule rule)
        {
            Match sourceMatch = SourceFormatRegex().Match(sourceKey);
            if (!sourceMatch.Success)
            {
                _logger.Debug("Invalid source format pattern: {0}", sourceKey);
                return false;
            }

            if (!ParseSourceFormat(sourceMatch.Groups[1].Value, rule))
                return false;

            if (sourceMatch.Groups[2].Success && sourceMatch.Groups[3].Success)
            {
                // Category rules (all, lossy, lossless) cannot have bitrate constraints
                if (rule.IsCategoryRule)
                {
                    _logger.Warn("Invalid: Bitrate constraints not applicable to category rules (all, lossy, lossless)");
                    return false;
                }

                if (!AudioFormatHelper.IsLossyFormat(rule.SourceFormat))
                {
                    _logger.Warn("Invalid: Bitrate constraints not applicable to lossless format");
                    return false;
                }

                if (!ParseSourceBitrateConstraints(sourceMatch.Groups[2].Value, sourceMatch.Groups[3].Value, rule))
                    return false;
            }

            return true;
        }

        private static bool ParseSourceFormat(string formatName, ConversionRule rule)
        {
            if (string.Equals(formatName, GlobalRuleIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                rule.SourceFormat = AudioFormat.Unknown;
                rule.IsGlobalRule = true;
                return true;
            }

            if (string.Equals(formatName, LossyRuleIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                rule.SourceFormat = AudioFormat.Unknown;
                rule.IsLossyRule = true;
                return true;
            }

            if (string.Equals(formatName, LosslessRuleIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                rule.SourceFormat = AudioFormat.Unknown;
                rule.IsLosslessRule = true;
                return true;
            }

            if (!Enum.TryParse(formatName, true, out AudioFormat sourceFormat))
            {
                _logger.Debug("Invalid source format: {0}", formatName);
                return false;
            }

            rule.SourceFormat = sourceFormat;
            return true;
        }

        private static bool ParseSourceBitrateConstraints(string operatorStr, string bitrateStr, ConversionRule rule)
        {
            if (!int.TryParse(bitrateStr, out int bitrateValue))
            {
                _logger.Debug("Invalid source bitrate value: {0}", bitrateStr);
                return false;
            }

            ComparisonOperator? comparisonOp = OperatorSymbols.FromSymbol(operatorStr);
            if (!comparisonOp.HasValue)
            {
                _logger.Debug("Invalid comparison operator: {0}", operatorStr);
                return false;
            }

            rule.SourceBitrateOperator = comparisonOp.Value;
            rule.SourceBitrateValue = bitrateValue;
            return true;
        }

        private static bool ParseTargetPart(string targetValue, ConversionRule rule)
        {
            Match targetMatch = TargetFormatRegex().Match(targetValue);
            if (!targetMatch.Success)
            {
                _logger.Debug("Invalid target format pattern: {0}", targetValue);
                return false;
            }

            return ParseTargetFormat(targetMatch.Groups[1].Value, rule) && (!targetMatch.Groups[2].Success || ParseTargetBitrate(targetMatch.Groups[2].Value, rule));
        }

        private static bool ParseTargetFormat(string formatName, ConversionRule rule)
        {
            if (!Enum.TryParse(formatName, true, out AudioFormat targetFormat))
            {
                _logger.Debug("Invalid target format: {0}", formatName);
                return false;
            }

            if (!AudioMetadataHandler.IsTargetFormatSupportedForEncoding(targetFormat))
            {
                _logger.Warn("Target format {0} is not supported for encoding by FFmpeg", targetFormat);
                return false;
            }

            rule.TargetFormat = targetFormat;
            return true;
        }

        private static bool ParseTargetBitrate(string bitrateStr, ConversionRule rule)
        {
            if (!int.TryParse(bitrateStr, out int targetBitrate))
            {
                _logger.Debug("Invalid target bitrate value: {0}", bitrateStr);
                return false;
            }

            int clampedBitrate = AudioFormatHelper.ClampBitrate(rule.TargetFormat, targetBitrate);
            if (clampedBitrate != targetBitrate)
            {
                _logger.Debug("Target bitrate ({0}) outside of valid range for format {1}",
                    targetBitrate, rule.TargetFormat);
                return false;
            }

            rule.TargetBitrate = AudioFormatHelper.RoundToStandardBitrate(targetBitrate);
            return true;
        }

        [GeneratedRegex(@"^([a-zA-Z0-9]+)(?:([!<>=]{1,2})(\d+))?$", RegexOptions.Compiled)]
        private static partial Regex SourceFormatRegex();

        [GeneratedRegex(@"^([a-zA-Z0-9]+)(?::(\d+)k?)?$", RegexOptions.Compiled)]
        private static partial Regex TargetFormatRegex();

        [GeneratedRegex(@"^([a-zA-Z]+)(?:-(\d+)k?)?$", RegexOptions.Compiled)]
        private static partial Regex ArtistTagRegex();
    }
}