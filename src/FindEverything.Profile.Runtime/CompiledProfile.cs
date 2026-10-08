using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FindEverything.Profile.Runtime;

internal sealed class CompiledProfile : ILoadedProfile
{
    private readonly Func<IReadOnlyDictionary<string, object?>, object> _createModel;
    private readonly CompiledField[] _fields;
    private readonly CompiledRegexRule[] _rules;
    private readonly CompiledDirectoryNameExclusionRule[] _excludedDirectoryNameRules;
    private readonly CompiledTextFileField[] _textFileFields;

    public CompiledProfile(
        ProfileDescriptor descriptor,
        Func<IReadOnlyDictionary<string, object?>, object> createModel,
        CompiledField[] fields,
        CompiledRegexRule[] rules,
        CompiledDirectoryNameExclusionRule[] excludedDirectoryNameRules,
        CompiledTextFileField[] textFileFields)
    {
        Descriptor = descriptor;
        _createModel = createModel;
        _fields = fields;
        _rules = rules;
        _excludedDirectoryNameRules = excludedDirectoryNameRules;
        _textFileFields = textFileFields;
    }

    public ProfileDescriptor Descriptor { get; }

    public ProfileDirectoryNameExclusionResult EvaluateDirectoryName(string directoryName)
    {
        ArgumentException.ThrowIfNullOrEmpty(directoryName);

        foreach (var rule in _excludedDirectoryNameRules)
        {
            try
            {
                if (rule.Regex.IsMatch(directoryName))
                {
                    return ProfileDirectoryNameExclusionResult.Excluded(rule.Id);
                }
            }
            catch (RegexMatchTimeoutException exception)
            {
                // Stop after the first timeout so a list of pathological patterns
                // cannot multiply the per-rule timeout into a minutes-long,
                // non-cancellable traversal callback. This is intentionally
                // fail-open: the directory is still mapped and traversed.
                return new ProfileDirectoryNameExclusionResult(
                    false,
                    null,
                    [new ProfileMappingIssue(
                        "directory_exclusion_regex_timeout",
                        null,
                        $"폴더 이름 제외 규칙 '{rule.Id}'의 실행 시간이 제한을 초과했습니다: {exception.MatchTimeout.TotalMilliseconds:0} ms")]);
            }
        }

        return ProfileDirectoryNameExclusionResult.NotExcluded();
    }

    public ProfileMapResult Map(ProfilePathCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var input = candidate.AbsolutePath;

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
                }, matchedRuleId: null, shouldPruneDescendants: false);
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

            var shouldPruneDescendants = rule.StopTraversalGroupNumbers.Length > 0
                && rule.StopTraversalGroupNumbers.All(groupNumber =>
                    HasCapturedValue(match, groupNumber));
            return MapMatch(candidate, rule, match, shouldPruneDescendants);
        }

        return ProfileMapResult.NoMatch();
    }

    private ProfileMapResult MapMatch(
        ProfilePathCandidate candidate,
        CompiledRegexRule rule,
        Match match,
        bool shouldPruneDescendants)
    {
        var parsedValues = new object?[_fields.Length];
        var issues = new List<ProfileMappingIssue>();

        for (var index = 0; index < _fields.Length; index++)
        {
            var field = _fields[index];
            var groupNumbers = rule.FieldGroupNumbers[index];
            var capturedValues = new string?[groupNumbers.Length];
            var capturedCount = 0;
            for (var groupIndex = 0; groupIndex < groupNumbers.Length; groupIndex++)
            {
                var groupNumber = groupNumbers[groupIndex];
                if (!HasCapturedValue(match, groupNumber))
                {
                    continue;
                }

                capturedValues[groupIndex] = match.Groups[groupNumber].Value;
                capturedCount++;
            }

            if (capturedCount == 0)
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

            if (capturedCount != groupNumbers.Length)
            {
                issues.Add(new ProfileMappingIssue(
                    "composite_capture_incomplete",
                    field.Descriptor.FieldId,
                    $"'{field.Descriptor.Header}'을(를) 구성하는 값 중 일부를 찾을 수 없습니다."));
                continue;
            }

            var source = string.Concat(capturedValues);

            if (!InvariantValueParser.TryParse(
                    source,
                    field.Descriptor,
                    out var parsedValue))
            {
                issues.Add(new ProfileMappingIssue(
                    "capture_conversion_failed",
                    field.Descriptor.FieldId,
                    $"'{source}'을(를) {field.Descriptor.Kind} 값으로 변환할 수 없습니다."));
                continue;
            }

            parsedValues[index] = parsedValue;
        }

        if (issues.Count > 0)
        {
            return ProfileMapResult.Invalid(
                issues,
                rule.Id,
                shouldPruneDescendants);
        }

        try
        {
            var values = new Dictionary<string, object?>(
                _fields.Length + _textFileFields.Length,
                StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < _fields.Length; index++)
            {
                var field = _fields[index];
                var value = parsedValues[index];
                values.Add(field.Descriptor.FieldId, value);
            }

            IReadOnlyList<string> immediateFilePaths = Array.Empty<string>();
            var canReadTextFiles = true;
            if (_textFileFields.Length > 0
                && !TryEnumerateImmediateFiles(
                    candidate.AbsolutePath,
                    out immediateFilePaths,
                    out var enumerationError))
            {
                canReadTextFiles = false;
                foreach (var textFileField in _textFileFields)
                {
                    issues.Add(new ProfileMappingIssue(
                        "text_file_enumeration_failed",
                        textFileField.Descriptor.FieldId,
                        $"'{textFileField.Descriptor.Header}' 값을 찾기 위해 폴더의 파일을 확인할 수 없습니다: {enumerationError}"));
                }
            }

            foreach (var textFileField in _textFileFields)
            {
                var value = canReadTextFiles
                    ? ReadTextFileValue(
                        immediateFilePaths,
                        textFileField,
                        issues)
                    : null;
                values.Add(textFileField.Descriptor.FieldId, value);
            }

            if (issues.Count > 0)
            {
                return ProfileMapResult.Invalid(
                    issues,
                    rule.Id,
                    shouldPruneDescendants);
            }

            var readOnlyValues = new ReadOnlyDictionary<string, object?>(values);
            var model = _createModel(readOnlyValues);

            return ProfileMapResult.Success(
                new MappedProfileItem(
                    Descriptor.Id,
                    candidate.AbsolutePath,
                    rule.Id,
                    model,
                    readOnlyValues),
                shouldPruneDescendants);
        }
        catch (Exception exception)
        {
            return ProfileMapResult.Invalid(
                new[]
                {
                    new ProfileMappingIssue(
                        "model_creation_failed",
                        null,
                        $"프로필 모델을 만들 수 없습니다: {exception.GetBaseException().Message}"),
                },
                rule.Id,
                shouldPruneDescendants);
        }
    }

    private static string? ReadTextFileValue(
        IReadOnlyList<string> immediateFilePaths,
        CompiledTextFileField field,
        ICollection<ProfileMappingIssue> issues)
    {
        string? selectedPath;
        try
        {
            selectedPath = immediateFilePaths
                .Where(path => field.FileNameRegex.IsMatch(Path.GetFileName(path)))
                .FirstOrDefault();
        }
        catch (RegexMatchTimeoutException exception)
        {
            issues.Add(new ProfileMappingIssue(
                "text_file_name_regex_timeout",
                field.Descriptor.FieldId,
                $"'{field.Descriptor.Header}' 파일 이름 규칙의 실행 시간이 제한을 초과했습니다: {exception.MatchTimeout.TotalMilliseconds:0} ms"));
            return null;
        }
        if (selectedPath is null)
        {
            if (field.Descriptor.Required)
            {
                issues.Add(new ProfileMappingIssue(
                    "required_text_file_missing",
                    field.Descriptor.FieldId,
                    $"필수 값 '{field.Descriptor.Header}'에 해당하는 바로 아래 텍스트 파일을 찾을 수 없습니다."));
            }

            return null;
        }

        try
        {
            using var stream = new FileStream(
                selectedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length > field.MaxBytes)
            {
                issues.Add(CreateFileTooLargeIssue(field, selectedPath));
                return null;
            }

            using var content = new MemoryStream((int)Math.Min(stream.Length, field.MaxBytes));
            var buffer = new byte[16 * 1024];
            var remaining = field.MaxBytes + 1;
            while (remaining > 0)
            {
                var read = stream.Read(
                    buffer,
                    0,
                    (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    break;
                }

                content.Write(buffer, 0, read);
                remaining -= read;
            }

            if (content.Length > field.MaxBytes)
            {
                issues.Add(CreateFileTooLargeIssue(field, selectedPath));
                return null;
            }

            content.Position = 0;
            using var reader = new StreamReader(
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: false);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException exception)
        {
            issues.Add(new ProfileMappingIssue(
                "text_file_encoding_invalid",
                field.Descriptor.FieldId,
                $"'{Path.GetFileName(selectedPath)}' 파일을 UTF-8 또는 BOM이 있는 텍스트로 읽을 수 없습니다: {exception.Message}"));
            return null;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException)
        {
            issues.Add(new ProfileMappingIssue(
                "text_file_read_failed",
                field.Descriptor.FieldId,
                $"'{Path.GetFileName(selectedPath)}' 파일을 읽을 수 없습니다: {exception.GetBaseException().Message}"));
            return null;
        }
    }

    private static bool TryEnumerateImmediateFiles(
        string directoryPath,
        out IReadOnlyList<string> paths,
        out string? error)
    {
        try
        {
            paths = Directory
                .EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
                .ThenBy(static path => path, StringComparer.Ordinal)
                .ToArray();
            error = null;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException)
        {
            paths = Array.Empty<string>();
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    private static ProfileMappingIssue CreateFileTooLargeIssue(
        CompiledTextFileField field,
        string selectedPath) =>
        new(
            "text_file_too_large",
            field.Descriptor.FieldId,
            $"'{Path.GetFileName(selectedPath)}' 파일이 이 필드의 읽기 제한({field.MaxBytes:N0}바이트)을 초과했습니다.");

    private static bool HasCapturedValue(Match match, int groupNumber)
    {
        if (groupNumber < 0)
        {
            return false;
        }

        var group = match.Groups[groupNumber];
        return group.Success && !string.IsNullOrWhiteSpace(group.Value);
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
                DateTime? parsedDate;
                try
                {
                    parsedDate = descriptor.ParseFormat is { Length: > 0 }
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
                }
                catch (FormatException)
                {
                    parsedDate = null;
                }

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
