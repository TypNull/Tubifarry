using NLog;
using NzbDrone.Common.Instrumentation;
using System.Text.RegularExpressions;
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

        public bool IsGlobalRule => SourceFormat.ToString().Equals(RuleParser.GlobalRuleIdentifier, StringComparison.OrdinalIgnoreCase);

        public bool MatchesBitrate(int? currentBitrate)
        {
            if (!HasBitrateConstraints())
                return true;

            if (!currentBitrate.HasValue)
                return false;
            return EvaluateBitrateCondition(currentBitrate.Value);
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
            string source = SourceFormat.ToString();
            if (HasBitrateConstraints())
                source += GetOperatorSymbol() + SourceBitrateValue!.Value;
            return source;
        }

        private string FormatTargetPart()
        {
            string target = TargetFormat.ToString();
            if (TargetBitrate.HasValue)
                target += TargetBitrate.Value.ToString();
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

    public static class RuleParser
    {
        public const string GlobalRuleIdentifier = "all";
        private static readonly Regex SourceFormatPattern = new(@"^([a-zA-Z0-9]+)(?:([!<>=]{1,2})(\d+))?$", RegexOptions.Compiled);
        private static readonly Regex TargetFormatPattern = new(@"^([a-zA-Z0-9]+)(?::(\d+)k?)?$", RegexOptions.Compiled);
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

        private static bool ParseSourcePart(string sourceKey, ConversionRule rule)
        {
            Match sourceMatch = SourceFormatPattern.Match(sourceKey);
            if (!sourceMatch.Success)
            {
                _logger.Debug("Invalid source format pattern: {0}", sourceKey);
                return false;
            }

            if (!ParseSourceFormat(sourceMatch.Groups[1].Value, rule))
                return false;

            if (sourceMatch.Groups[2].Success && sourceMatch.Groups[3].Success)
            {
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
            Match targetMatch = TargetFormatPattern.Match(targetValue);
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
    }
}