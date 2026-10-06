using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FindEverything.Profile.Runtime;

internal sealed class CompiledProfile : ILoadedProfile
{
    private readonly Func<object> _createModel;
    private readonly CompiledField[] _fields;
    private readonly CompiledRegexRule[] _rules;

    public CompiledProfile(
        ProfileDescriptor descriptor,
        Func<object> createModel,
        CompiledField[] fields,
        CompiledRegexRule[] rules)
    {
        Descriptor = descriptor;
        _createModel = createModel;
        _fields = fields;
        _rules = rules;
    }

    public ProfileDescriptor Descriptor { get; }

    public ProfileMapResult Map(ProfilePathCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.FullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.RelativePath);

        var input = Descriptor.PathInput == ProfilePathInput.Full
            ? candidate.FullPath
            : candidate.RelativePath;

        foreach (var rule in _rules)
        {
            Match match;
            try
            {
                match = rule.Regex.Match(input);
            }
            catch (RegexMatchTimeoutException exception)
            {
                return ProfileMapResult.Invalid(new[]
                {
                    new ProfileMappingIssue(
                        "regex_timeout",
                        null,
                        $"정규식 규칙 '{rule.Id}'의 실행 시간이 제한을 초과했습니다: {exception.MatchTimeout.TotalMilliseconds:0} ms"),
                });
            }

            if (!match.Success)
            {
                continue;
            }

            if (rule.MatchMode == ProfileRegexMatchMode.Full
                && (match.Index != 0 || match.Length != input.Length))
            {
                continue;
            }

            return MapMatch(candidate, rule, match);
        }

        return ProfileMapResult.NoMatch();
    }

    private ProfileMapResult MapMatch(
        ProfilePathCandidate candidate,
        CompiledRegexRule rule,
        Match match)
    {
        var parsedValues = new object?[_fields.Length];
        var issues = new List<ProfileMappingIssue>();

        for (var index = 0; index < _fields.Length; index++)
        {
            var field = _fields[index];
            var groupNumber = rule.GroupNumbers[index];
            var group = groupNumber >= 0 ? match.Groups[groupNumber] : null;

            if (group is null || !group.Success || string.IsNullOrWhiteSpace(group.Value))
            {
                if (field.Descriptor.Required)
                {
                    issues.Add(new ProfileMappingIssue(
                        "required_capture_missing",
                        field.Descriptor.FieldId,
                        $"필수 값 '{field.Descriptor.Header}'을(를) 찾을 수 없습니다."));
                }

                parsedValues[index] = null;
                continue;
            }

            if (!InvariantValueParser.TryParse(
                    group.Value,
                    field.Descriptor,
                    out var parsedValue))
            {
                issues.Add(new ProfileMappingIssue(
                    "capture_conversion_failed",
                    field.Descriptor.FieldId,
                    $"'{group.Value}'을(를) {field.Descriptor.Kind} 값으로 변환할 수 없습니다."));
                continue;
            }

            parsedValues[index] = parsedValue;
        }

        if (issues.Count > 0)
        {
            return ProfileMapResult.Invalid(issues);
        }

        try
        {
            var model = _createModel();
            var values = new Dictionary<string, object?>(
                _fields.Length,
                StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < _fields.Length; index++)
            {
                var field = _fields[index];
                var value = parsedValues[index];
                field.SetValue(model, value);
                values.Add(field.Descriptor.FieldId, value);
            }

            return ProfileMapResult.Success(new MappedProfileItem(
                Descriptor.Id,
                candidate.FullPath,
                candidate.RelativePath,
                rule.Id,
                model,
                new ReadOnlyDictionary<string, object?>(values)));
        }
        catch (Exception exception)
        {
            return ProfileMapResult.Invalid(new[]
            {
                new ProfileMappingIssue(
                    "model_creation_failed",
                    null,
                    $"프로필 모델을 만들 수 없습니다: {exception.GetBaseException().Message}"),
            });
        }
    }
}

internal static class InvariantValueParser
{
    public static bool TryParse(
        string source,
        ProfileFieldDescriptor descriptor,
        out object? value)
    {
        if (descriptor.Kind == ProfileFieldValueKind.String)
        {
            value = source;
            return true;
        }

        var trimmed = source.Trim();
        switch (descriptor.Kind)
        {
            case ProfileFieldValueKind.Int32:
                if (int.TryParse(
                        trimmed,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var integer))
                {
                    value = integer;
                    return true;
                }

                break;

            case ProfileFieldValueKind.Decimal:
                if (decimal.TryParse(
                        trimmed,
                        NumberStyles.Number,
                        CultureInfo.InvariantCulture,
                        out var decimalValue))
                {
                    value = decimalValue;
                    return true;
                }

                break;

            case ProfileFieldValueKind.DateTime:
                var parsedDate = descriptor.ParseFormat is { Length: > 0 }
                    ? DateTime.TryParseExact(
                        trimmed,
                        descriptor.ParseFormat,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces,
                        out var exactDate)
                        ? exactDate
                        : (DateTime?)null
                    : DateTime.TryParse(
                        trimmed,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces,
                        out var flexibleDate)
                        ? flexibleDate
                        : (DateTime?)null;

                if (parsedDate.HasValue)
                {
                    value = parsedDate.Value;
                    return true;
                }

                break;

            case ProfileFieldValueKind.Boolean:
                if (bool.TryParse(trimmed, out var boolean))
                {
                    value = boolean;
                    return true;
                }

                break;

            default:
                break;
        }

        value = null;
        return false;
    }
}
