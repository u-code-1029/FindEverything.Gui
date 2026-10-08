using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FindEverything.Profile.Runtime;

internal sealed record ValidatedRegexRule(
    string Id,
    string Pattern,
    ProfileRegexMatchMode MatchMode,
    bool IgnoreCase,
    int TimeoutMilliseconds,
    IReadOnlyList<string> StopTraversalWhenCapturedGroups);

internal sealed record ValidatedDirectoryNameExclusionRule(
    string Id,
    string Pattern,
    ProfileRegexMatchMode MatchMode,
    bool IgnoreCase,
    int TimeoutMilliseconds);

internal sealed record ValidatedProfileField(
    string FieldId,
    IReadOnlyList<string> GroupNames,
    string Header,
    int Order,
    bool Required,
    ProfileFieldValueKind Kind,
    bool IsNullable,
    string? ParseFormat,
    string? DisplayFormat,
    IReadOnlyDictionary<string, string> ValueMappings);

internal sealed record ValidatedTextFileField(
    string FieldId,
    string Header,
    int Order,
    bool Required,
    string FileNamePattern,
    ProfileRegexMatchMode MatchMode,
    bool IgnoreCase,
    int TimeoutMilliseconds,
    long MaxBytes);

internal sealed record ValidatedProfileManifest(
    string SourceDirectory,
    string Id,
    string Version,
    string DisplayName,
    ProfileKind Kind,
    string? EntryAssemblyPath,
    string? ModelType,
    ProfileCandidateKind CandidateKind,
    IReadOnlyList<ValidatedProfileField> Fields,
    IReadOnlyList<ValidatedTextFileField> TextFileFields,
    IReadOnlyList<ValidatedDirectoryNameExclusionRule> ExcludedDirectoryNameRules,
    IReadOnlyList<ValidatedRegexRule> Rules);

internal sealed record ManifestReadResult(
    ValidatedProfileManifest? Manifest,
    string? ProfileId,
    string? DisplayName,
    IReadOnlyList<ProfileDiagnostic> Diagnostics);

internal sealed class ProfileManifestReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly ProfileManifestValidator _validator = new();

    public async Task<ManifestReadResult> ReadAsync(
        string sourceDirectory,
        int supportedContractMajor,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(sourceDirectory, "profile.json");
        if (!File.Exists(manifestPath))
        {
            return Failure(
                "manifest_missing",
                "profile.json 파일을 찾을 수 없습니다.",
                manifestPath);
        }

        try
        {
            var manifestFile = new FileInfo(manifestPath);
            if (manifestFile.Length > ProfileManifestLimits.MaximumLengthBytes)
            {
                return Failure(
                    "manifest_too_large",
                    "profile.json 파일이 허용 크기(1 MiB)를 초과했습니다.",
                    manifestPath);
            }

            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var manifest = await JsonSerializer.DeserializeAsync<ProfileManifest>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);

            if (manifest is null)
            {
                return Failure(
                    "manifest_empty",
                    "profile.json에 프로필 정의가 없습니다.",
                    manifestPath);
            }

            return _validator.Validate(sourceDirectory, manifest, supportedContractMajor);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException exception)
        {
            return Failure(
                "manifest_invalid_json",
                "profile.json의 JSON 형식이 올바르지 않습니다.",
                exception.Message);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Failure(
                "manifest_read_failed",
                "profile.json을 읽을 수 없습니다.",
                exception.Message);
        }
    }

    private static ManifestReadResult Failure(string code, string message, string? detail) =>
        new(
            null,
            null,
            null,
            new[]
            {
                new ProfileDiagnostic(ProfileDiagnosticSeverity.Error, code, message, detail),
            });
}

internal sealed class ProfileManifestValidator
{
    private static readonly Regex StableIdPattern = new(
        "^[a-z0-9][a-z0-9.-]{0,63}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex RuleIdPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex FieldIdPattern = new(
        "^[a-z0-9][a-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex GroupNamePattern = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public ManifestReadResult Validate(
        string sourceDirectory,
        ProfileManifest manifest,
        int supportedContractMajor)
    {
        var diagnostics = new List<ProfileDiagnostic>();
        var id = NormalizeRequired(manifest.Id);
        var version = NormalizeRequired(manifest.Version);
        var displayName = NormalizeRequired(manifest.DisplayName);
        var entryAssembly = NormalizeRequired(manifest.EntryAssembly);
        var modelType = NormalizeRequired(manifest.ModelType);

        if (manifest.ContractVersion != supportedContractMajor)
        {
            diagnostics.Add(Error(
                "contract_unsupported",
                $"프로필 계약 버전 {manifest.ContractVersion}은(는) 지원되지 않습니다. 지원 버전: {supportedContractMajor}."));
        }

        if (id is null || !StableIdPattern.IsMatch(id))
        {
            diagnostics.Add(Error(
                "profile_id_invalid",
                "프로필 id는 소문자 영숫자로 시작하고 소문자 영숫자, 점, 하이픈만 포함해야 합니다."));
        }

        if (version is null)
        {
            diagnostics.Add(Error("profile_version_missing", "프로필 version이 필요합니다."));
        }

        if (displayName is null)
        {
            diagnostics.Add(Error("profile_display_name_missing", "프로필 displayName이 필요합니다."));
        }

        if (!Enum.IsDefined(manifest.Kind))
        {
            diagnostics.Add(Error("profile_kind_invalid", "지원되지 않는 profile kind입니다."));
        }

        if (!Enum.IsDefined(manifest.CandidateKind))
        {
            diagnostics.Add(Error("candidate_kind_invalid", "지원되지 않는 candidateKind입니다."));
        }

        string? entryAssemblyPath = null;
        var validatedFields = new List<ValidatedProfileField>();
        var validatedTextFileFields = new List<ValidatedTextFileField>();
        if (manifest.Kind == ProfileKind.Assembly)
        {
            if (modelType is null)
            {
                diagnostics.Add(Error("profile_model_type_missing", "Assembly 프로필에는 modelType이 필요합니다."));
            }

            entryAssemblyPath = ValidateEntryAssembly(
                sourceDirectory,
                entryAssembly,
                diagnostics);

            if (manifest.Fields is { Count: > 0 })
            {
                diagnostics.Add(Error(
                    "assembly_fields_not_allowed",
                    "Assembly 프로필의 필드는 모델의 CaptureFieldAttribute에서 정의되므로 fields를 사용할 수 없습니다."));
            }

            if (manifest.TextFileFields is { Count: > 0 })
            {
                diagnostics.Add(Error(
                    "assembly_text_file_fields_not_allowed",
                    "textFileFields는 Declarative 프로필에서만 사용할 수 있습니다."));
            }
        }
        else if (manifest.Kind == ProfileKind.Declarative)
        {
            if (entryAssembly is not null)
            {
                diagnostics.Add(Error(
                    "declarative_entry_assembly_not_allowed",
                    "Declarative 프로필에는 entryAssembly를 사용할 수 없습니다."));
            }

            if (modelType is not null)
            {
                diagnostics.Add(Error(
                    "declarative_model_type_not_allowed",
                    "Declarative 프로필에는 modelType을 사용할 수 없습니다."));
            }

            var fieldIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            validatedFields = ValidateFields(
                manifest.Fields,
                fieldIds,
                diagnostics);
            validatedTextFileFields = ValidateTextFileFields(
                manifest.TextFileFields,
                fieldIds,
                diagnostics);
            if (manifest.Fields is not { Count: > 0 }
                && manifest.TextFileFields is not { Count: > 0 })
            {
                diagnostics.Add(Error(
                    "capture_fields_missing",
                    "Declarative 프로필에는 fields 또는 textFileFields 항목이 하나 이상 필요합니다."));
            }
        }

        var validatedExcludedDirectoryNameRules = ValidateExcludedDirectoryNameRules(
            manifest.ExcludedDirectoryNameRules,
            diagnostics);
        var validatedRules = ValidateRules(manifest.Rules, diagnostics);
        ValidateAggregateRegexTimeoutBudget(
            manifest.Rules,
            manifest.ExcludedDirectoryNameRules,
            manifest.TextFileFields,
            diagnostics);

        if (diagnostics.Any(static diagnostic =>
                diagnostic.Severity == ProfileDiagnosticSeverity.Error))
        {
            return new ManifestReadResult(
                null,
                id,
                displayName,
                Array.AsReadOnly(diagnostics.ToArray()));
        }

        return new ManifestReadResult(
            new ValidatedProfileManifest(
                Path.GetFullPath(sourceDirectory),
                id!,
                version!,
                displayName!,
                manifest.Kind,
                entryAssemblyPath,
                modelType,
                manifest.CandidateKind,
                Array.AsReadOnly(validatedFields.ToArray()),
                Array.AsReadOnly(validatedTextFileFields.ToArray()),
                Array.AsReadOnly(validatedExcludedDirectoryNameRules.ToArray()),
                Array.AsReadOnly(validatedRules.ToArray())),
            id,
            displayName,
            Array.AsReadOnly(diagnostics.ToArray()));
    }

    private static string? ValidateEntryAssembly(
        string sourceDirectory,
        string? entryAssembly,
        ICollection<ProfileDiagnostic> diagnostics)
    {
        if (entryAssembly is null)
        {
            diagnostics.Add(Error(
                "entry_assembly_missing",
                "Assembly 프로필에는 entryAssembly가 필요합니다."));
            return null;
        }

        try
        {
            var entryAssemblyPath = Path.GetFullPath(Path.Combine(sourceDirectory, entryAssembly));
            if (!IsWithinDirectory(sourceDirectory, entryAssemblyPath))
            {
                diagnostics.Add(Error(
                    "entry_assembly_outside_profile",
                    "entryAssembly는 프로필 디렉터리 내부에 있어야 합니다."));
            }
            else if (!File.Exists(entryAssemblyPath))
            {
                diagnostics.Add(Error(
                    "entry_assembly_missing_file",
                    $"entryAssembly 파일을 찾을 수 없습니다: {entryAssembly}"));
            }
            else if (!string.Equals(
                         Path.GetExtension(entryAssemblyPath),
                         ".dll",
                         StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error(
                    "entry_assembly_not_dll",
                    "entryAssembly는 .dll 파일이어야 합니다."));
            }

            return entryAssemblyPath;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostics.Add(Error(
                "entry_assembly_invalid_path",
                "entryAssembly 경로가 올바르지 않습니다.",
                exception.Message));
            return null;
        }
    }

    private static List<ValidatedProfileField> ValidateFields(
        IReadOnlyList<ProfileFieldManifest>? fields,
        ISet<string> fieldIds,
        ICollection<ProfileDiagnostic> diagnostics)
    {
        var validated = new List<ValidatedProfileField>();
        if (fields is null || fields.Count == 0)
        {
            return validated;
        }

        for (var index = 0; index < fields.Count; index++)
        {
            var source = fields[index];
            var fieldId = NormalizeRequired(source.FieldId);
            var displayId = fieldId ?? $"#{index + 1}";
            var valid = true;

            if (fieldId is null || !FieldIdPattern.IsMatch(fieldId))
            {
                diagnostics.Add(Error(
                    "capture_field_id_invalid",
                    $"필드 #{index + 1}의 fieldId는 소문자 영숫자로 시작하고 소문자 영숫자, 점, 밑줄, 하이픈만 포함해야 합니다."));
                valid = false;
            }
            else if (!fieldIds.Add(fieldId))
            {
                diagnostics.Add(Error(
                    "capture_field_id_duplicate",
                    $"fieldId가 중복되었습니다: {fieldId}"));
                valid = false;
            }

            var groupNames = ValidateGroupNames(source, displayId, diagnostics, ref valid);

            if (!Enum.IsDefined(source.Kind))
            {
                diagnostics.Add(Error(
                    "capture_field_type_unsupported",
                    $"필드 '{displayId}'의 kind가 지원되지 않습니다."));
                valid = false;
            }

            var parseFormat = NormalizeRequired(source.ParseFormat);
            if (parseFormat is not null && source.Kind != ProfileFieldValueKind.DateTime)
            {
                diagnostics.Add(Error(
                    "capture_parse_format_unsupported",
                    $"필드 '{displayId}'의 parseFormat은 DateTime 필드에만 사용할 수 있습니다."));
                valid = false;
            }
            else if (parseFormat is not null)
            {
                try
                {
                    _ = DateTime.UnixEpoch.ToString(parseFormat, CultureInfo.InvariantCulture);
                }
                catch (FormatException exception)
                {
                    diagnostics.Add(Error(
                        "capture_parse_format_invalid",
                        $"필드 '{displayId}'의 날짜 입력 형식이 올바르지 않습니다.",
                        exception.Message));
                    valid = false;
                }
            }

            if (groupNames.Count > 1
                && source.Kind == ProfileFieldValueKind.DateTime
                && parseFormat is null)
            {
                diagnostics.Add(Error(
                    "capture_composite_datetime_parse_format_missing",
                    $"복합 날짜 필드 '{displayId}'에는 parseFormat이 필요합니다."));
                valid = false;
            }

            var displayFormat = NormalizeRequired(source.DisplayFormat);
            if (displayFormat is not null
                && !ValidateDisplayFormat(source.Kind, displayFormat, out var formatError))
            {
                diagnostics.Add(Error(
                    "capture_display_format_invalid",
                    $"필드 '{displayId}'의 표시 형식이 올바르지 않습니다.",
                    formatError));
                valid = false;
            }

            var valueMappings = ValidateValueMappings(
                source,
                displayId,
                diagnostics,
                ref valid);

            if (!valid)
            {
                continue;
            }

            validated.Add(new ValidatedProfileField(
                fieldId!,
                Array.AsReadOnly(groupNames.ToArray()),
                NormalizeRequired(source.Header) ?? fieldId!,
                source.Order,
                source.Required,
                source.Kind,
                IsNullable: !source.Required,
                parseFormat,
                displayFormat,
                valueMappings));
        }

        validated.Sort(static (left, right) =>
        {
            var orderComparison = left.Order.CompareTo(right.Order);
            return orderComparison != 0
                ? orderComparison
                : StringComparer.Ordinal.Compare(left.FieldId, right.FieldId);
        });

        return validated;
    }

    private static IReadOnlyDictionary<string, string> ValidateValueMappings(
        ProfileFieldManifest source,
        string displayId,
        ICollection<ProfileDiagnostic> diagnostics,
        ref bool valid)
    {
        if (source.ValueMappings is null || source.ValueMappings.Count == 0)
        {
            return new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        if (source.Kind != ProfileFieldValueKind.String)
        {
            diagnostics.Add(Error(
                "capture_value_mapping_kind_unsupported",
                $"필드 '{displayId}'의 값 매핑은 텍스트 필드에서만 사용할 수 있습니다."));
            valid = false;
        }

        if (source.ValueMappings.Count > ProfileManifestLimits.MaximumValueMappingCount)
        {
            diagnostics.Add(Error(
                "capture_value_mapping_limit_exceeded",
                $"필드 '{displayId}'의 값 매핑은 최대 {ProfileManifestLimits.MaximumValueMappingCount:N0}개까지 사용할 수 있습니다."));
            valid = false;
        }

        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < source.ValueMappings.Count; index++)
        {
            var mapping = source.ValueMappings[index];
            var valueId = $"{displayId} #{index + 1}";
            if (mapping is null)
            {
                diagnostics.Add(Error(
                    "capture_value_mapping_missing",
                    $"필드 '{valueId}' 값 매핑 항목이 비어 있습니다."));
                valid = false;
                continue;
            }

            var rawSource = mapping.Source;
            var display = mapping.Display;
            if (string.IsNullOrWhiteSpace(rawSource))
            {
                diagnostics.Add(Error(
                    "capture_value_mapping_source_missing",
                    $"필드 '{valueId}' 값 매핑의 원본 값이 비어 있습니다."));
                valid = false;
                continue;
            }

            if (rawSource.Length > ProfileManifestLimits.MaximumValueMappingSourceLength)
            {
                diagnostics.Add(Error(
                    "capture_value_mapping_source_too_long",
                    $"필드 '{valueId}' 값 매핑의 원본 값은 최대 {ProfileManifestLimits.MaximumValueMappingSourceLength:N0}자까지 사용할 수 있습니다."));
                valid = false;
                continue;
            }

            if (string.IsNullOrWhiteSpace(display))
            {
                diagnostics.Add(Error(
                    "capture_value_mapping_display_missing",
                    $"필드 '{valueId}' 값 매핑의 표시 값이 비어 있습니다."));
                valid = false;
                continue;
            }

            if (display.Length > ProfileManifestLimits.MaximumValueMappingDisplayLength)
            {
                diagnostics.Add(Error(
                    "capture_value_mapping_display_too_long",
                    $"필드 '{valueId}' 값 매핑의 표시 값은 최대 {ProfileManifestLimits.MaximumValueMappingDisplayLength:N0}자까지 사용할 수 있습니다."));
                valid = false;
                continue;
            }

            if (!mappings.TryAdd(rawSource, display))
            {
                diagnostics.Add(Error(
                    "capture_value_mapping_source_duplicate",
                    $"필드 '{displayId}'의 값 매핑에 같은 원본 값이 두 번 있습니다: {rawSource}"));
                valid = false;
            }
        }

        return new ReadOnlyDictionary<string, string>(mappings);
    }

    private static List<ValidatedTextFileField> ValidateTextFileFields(
        IReadOnlyList<ProfileTextFileFieldManifest>? fields,
        ISet<string> fieldIds,
        ICollection<ProfileDiagnostic> diagnostics)
    {
        var validated = new List<ValidatedTextFileField>();
        if (fields is null)
        {
            return validated;
        }

        if (fields.Count > ProfileManifestLimits.MaximumTextFileFieldCount)
        {
            diagnostics.Add(Error(
                "text_file_field_limit_exceeded",
                $"텍스트 파일 필드는 최대 {ProfileManifestLimits.MaximumTextFileFieldCount}개까지 사용할 수 있습니다."));
        }

        for (var index = 0; index < fields.Count; index++)
        {
            var source = fields[index];
            var fieldId = NormalizeRequired(source.FieldId);
            var displayId = fieldId ?? $"#{index + 1}";
            var pattern = source.FileNamePattern;
            var valid = true;

            if (fieldId is null || !FieldIdPattern.IsMatch(fieldId))
            {
                diagnostics.Add(Error(
                    "text_file_field_id_invalid",
                    $"텍스트 파일 필드 #{index + 1}의 fieldId는 소문자 영숫자로 시작하고 소문자 영숫자, 점, 밑줄, 하이픈만 포함해야 합니다."));
                valid = false;
            }
            else if (!fieldIds.Add(fieldId))
            {
                diagnostics.Add(Error(
                    "text_file_field_id_duplicate",
                    $"경로 값과 텍스트 파일 값을 포함해 fieldId가 중복되었습니다: {fieldId}"));
                valid = false;
            }

            if (string.IsNullOrWhiteSpace(pattern))
            {
                diagnostics.Add(Error(
                    "text_file_name_pattern_missing",
                    $"텍스트 파일 필드 '{displayId}'에 fileNamePattern이 필요합니다."));
                valid = false;
            }
            else if (pattern.Length > ProfileManifestLimits.MaximumTextFileNamePatternLength)
            {
                diagnostics.Add(Error(
                    "text_file_name_pattern_too_long",
                    $"텍스트 파일 필드 '{displayId}'의 fileNamePattern은 최대 {ProfileManifestLimits.MaximumTextFileNamePatternLength:N0}자까지 사용할 수 있습니다."));
                valid = false;
            }

            if (!Enum.IsDefined(source.MatchMode))
            {
                diagnostics.Add(Error(
                    "text_file_name_match_mode_invalid",
                    $"텍스트 파일 필드 '{displayId}'의 matchMode가 올바르지 않습니다."));
                valid = false;
            }

            if (source.TimeoutMilliseconds is < 1 or > 10_000)
            {
                diagnostics.Add(Error(
                    "text_file_name_timeout_invalid",
                    $"텍스트 파일 필드 '{displayId}'의 timeoutMilliseconds는 1~10000이어야 합니다."));
                valid = false;
            }

            if (source.MaxBytes is < 1 or > ProfileManifestLimits.MaximumTextFileMaximumBytes)
            {
                diagnostics.Add(Error(
                    "text_file_max_bytes_invalid",
                    $"텍스트 파일 필드 '{displayId}'의 maxBytes는 1~{ProfileManifestLimits.MaximumTextFileMaximumBytes:N0}바이트여야 합니다."));
                valid = false;
            }

            if (!valid)
            {
                continue;
            }

            validated.Add(new ValidatedTextFileField(
                fieldId!,
                NormalizeRequired(source.Header) ?? fieldId!,
                source.Order,
                source.Required,
                pattern!,
                source.MatchMode,
                source.IgnoreCase,
                source.TimeoutMilliseconds,
                source.MaxBytes));
        }

        validated.Sort(static (left, right) =>
        {
            var orderComparison = left.Order.CompareTo(right.Order);
            return orderComparison != 0
                ? orderComparison
                : StringComparer.Ordinal.Compare(left.FieldId, right.FieldId);
        });
        return validated;
    }

    private static List<string> ValidateGroupNames(
        ProfileFieldManifest source,
        string displayId,
        ICollection<ProfileDiagnostic> diagnostics,
        ref bool valid)
    {
        var groupName = NormalizeRequired(source.GroupName);
        var hasGroupNamesProperty = source.GroupNames is not null;
        if (groupName is not null && hasGroupNamesProperty)
        {
            diagnostics.Add(Error(
                "capture_group_sources_conflict",
                $"필드 '{displayId}'에는 groupName과 groupNames를 동시에 사용할 수 없습니다."));
            valid = false;
            return [];
        }

        if (!hasGroupNamesProperty)
        {
            if (groupName is null)
            {
                diagnostics.Add(Error(
                    "capture_group_name_missing",
                    $"필드 '{displayId}'에 groupName 또는 groupNames가 필요합니다."));
                valid = false;
                return [];
            }

            if (!GroupNamePattern.IsMatch(groupName))
            {
                diagnostics.Add(Error(
                    "capture_group_name_invalid",
                    $"필드 '{displayId}'의 groupName은 문자 또는 밑줄로 시작하고 영숫자와 밑줄만 포함해야 합니다."));
                valid = false;
                return [];
            }

            return [groupName];
        }

        if (source.GroupNames!.Count == 0)
        {
            diagnostics.Add(Error(
                "capture_group_names_missing",
                $"필드 '{displayId}'의 groupNames에는 하나 이상의 그룹이 필요합니다."));
            valid = false;
            return [];
        }

        if (source.GroupNames.Count > ProfileManifestLimits.MaximumCompositeGroupCount)
        {
            diagnostics.Add(Error(
                "capture_group_names_limit_exceeded",
                $"필드 '{displayId}'의 groupNames는 최대 {ProfileManifestLimits.MaximumCompositeGroupCount}개까지 사용할 수 있습니다."));
            valid = false;
        }

        var result = new List<string>(source.GroupNames.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceGroupName in source.GroupNames)
        {
            var normalized = NormalizeRequired(sourceGroupName);
            if (normalized is null || !GroupNamePattern.IsMatch(normalized))
            {
                diagnostics.Add(Error(
                    "capture_group_name_invalid",
                    $"필드 '{displayId}'의 groupNames에는 올바른 정규식 그룹 이름만 사용할 수 있습니다."));
                valid = false;
                continue;
            }

            if (!seen.Add(normalized))
            {
                diagnostics.Add(Error(
                    "capture_group_name_duplicate",
                    $"필드 '{displayId}'의 groupNames에 중복된 그룹이 있습니다: {normalized}"));
                valid = false;
                continue;
            }

            result.Add(normalized);
        }

        return result;
    }

    private static bool ValidateDisplayFormat(
        ProfileFieldValueKind kind,
        string displayFormat,
        out string? error)
    {
        IFormattable? sample = kind switch
        {
            ProfileFieldValueKind.Int32 => 1234,
            ProfileFieldValueKind.Decimal => 1234.5m,
            ProfileFieldValueKind.DateTime => DateTime.UnixEpoch,
            _ => null,
        };
        if (sample is null)
        {
            error = "표시 형식은 정수, 소수, 날짜/시간 필드에만 사용할 수 있습니다.";
            return false;
        }

        try
        {
            _ = sample.ToString(displayFormat, CultureInfo.InvariantCulture);
            error = null;
            return true;
        }
        catch (FormatException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static List<ValidatedRegexRule> ValidateRules(
        IReadOnlyList<ProfileRegexRuleManifest>? rules,
        ICollection<ProfileDiagnostic> diagnostics)
    {
        var validated = new List<ValidatedRegexRule>();
        if (rules is null || rules.Count == 0)
        {
            diagnostics.Add(Error("regex_rules_missing", "하나 이상의 정규식 규칙이 필요합니다."));
            return validated;
        }

        if (rules.Count > ProfileManifestLimits.MaximumRegexRuleCount)
        {
            diagnostics.Add(Error(
                "regex_rule_limit_exceeded",
                $"정규식 규칙은 최대 {ProfileManifestLimits.MaximumRegexRuleCount}개까지 사용할 수 있습니다."));
        }

        var ruleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < rules.Count; index++)
        {
            var source = rules[index];
            var ruleId = NormalizeRequired(source.Id) ?? $"rule-{index + 1}";
            var pattern = source.Pattern;
            var stopTraversalGroups = ValidateStopTraversalGroups(
                source.StopTraversalWhenCapturedGroups,
                ruleId,
                diagnostics);

            if (!RuleIdPattern.IsMatch(ruleId))
            {
                diagnostics.Add(Error(
                    "regex_rule_id_invalid",
                    $"정규식 규칙 #{index + 1}의 id가 올바르지 않습니다."));
            }
            else if (!ruleIds.Add(ruleId))
            {
                diagnostics.Add(Error(
                    "regex_rule_id_duplicate",
                    $"정규식 규칙 id가 중복되었습니다: {ruleId}"));
            }

            if (string.IsNullOrWhiteSpace(pattern))
            {
                diagnostics.Add(Error(
                    "regex_pattern_missing",
                    $"정규식 규칙 '{ruleId}'에 pattern이 필요합니다."));
            }
            else if (pattern.Length > ProfileManifestLimits.MaximumRegexPatternLength)
            {
                diagnostics.Add(Error(
                    "regex_pattern_too_long",
                    $"정규식 규칙 '{ruleId}'의 pattern은 최대 "
                    + $"{ProfileManifestLimits.MaximumRegexPatternLength:N0}자까지 사용할 수 있습니다."));
            }

            if (!Enum.IsDefined(source.MatchMode))
            {
                diagnostics.Add(Error(
                    "regex_match_mode_invalid",
                    $"정규식 규칙 '{ruleId}'의 matchMode가 올바르지 않습니다."));
            }

            if (source.TimeoutMilliseconds is < 1 or > 10_000)
            {
                diagnostics.Add(Error(
                    "regex_timeout_invalid",
                    $"정규식 규칙 '{ruleId}'의 timeoutMilliseconds는 1~10000이어야 합니다."));
            }

            if (!string.IsNullOrWhiteSpace(pattern)
                && pattern.Length <= ProfileManifestLimits.MaximumRegexPatternLength
                && RuleIdPattern.IsMatch(ruleId)
                && Enum.IsDefined(source.MatchMode)
                && source.TimeoutMilliseconds is >= 1 and <= 10_000)
            {
                validated.Add(new ValidatedRegexRule(
                    ruleId,
                    pattern,
                    source.MatchMode,
                    source.IgnoreCase,
                    source.TimeoutMilliseconds,
                    Array.AsReadOnly(stopTraversalGroups.ToArray())));
            }
        }

        return validated;
    }

    private static List<ValidatedDirectoryNameExclusionRule>
        ValidateExcludedDirectoryNameRules(
            IReadOnlyList<ProfileDirectoryNameExclusionRuleManifest>? rules,
            ICollection<ProfileDiagnostic> diagnostics)
    {
        var validated = new List<ValidatedDirectoryNameExclusionRule>();
        if (rules is null)
        {
            return validated;
        }

        if (rules.Count > ProfileManifestLimits.MaximumExcludedDirectoryNameRuleCount)
        {
            diagnostics.Add(Error(
                "excluded_directory_name_rule_limit_exceeded",
                $"폴더 이름 제외 규칙은 최대 {ProfileManifestLimits.MaximumExcludedDirectoryNameRuleCount}개까지 사용할 수 있습니다."));
        }

        var ruleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < rules.Count; index++)
        {
            var source = rules[index];
            var ruleId = NormalizeRequired(source.Id) ?? $"exclude-{index + 1}";
            var pattern = source.Pattern;
            var valid = true;

            if (!RuleIdPattern.IsMatch(ruleId))
            {
                diagnostics.Add(Error(
                    "excluded_directory_name_rule_id_invalid",
                    $"폴더 이름 제외 규칙 #{index + 1}의 id가 올바르지 않습니다."));
                valid = false;
            }
            else if (!ruleIds.Add(ruleId))
            {
                diagnostics.Add(Error(
                    "excluded_directory_name_rule_id_duplicate",
                    $"폴더 이름 제외 규칙 id가 중복되었습니다: {ruleId}"));
                valid = false;
            }

            if (string.IsNullOrWhiteSpace(pattern))
            {
                diagnostics.Add(Error(
                    "excluded_directory_name_pattern_missing",
                    $"폴더 이름 제외 규칙 '{ruleId}'에 pattern이 필요합니다."));
                valid = false;
            }
            else if (pattern.Length
                     > ProfileManifestLimits.MaximumExcludedDirectoryNamePatternLength)
            {
                diagnostics.Add(Error(
                    "excluded_directory_name_pattern_too_long",
                    $"폴더 이름 제외 규칙 '{ruleId}'의 pattern은 최대 {ProfileManifestLimits.MaximumExcludedDirectoryNamePatternLength:N0}자까지 사용할 수 있습니다."));
                valid = false;
            }

            if (!Enum.IsDefined(source.MatchMode))
            {
                diagnostics.Add(Error(
                    "excluded_directory_name_match_mode_invalid",
                    $"폴더 이름 제외 규칙 '{ruleId}'의 matchMode가 올바르지 않습니다."));
                valid = false;
            }

            if (source.TimeoutMilliseconds is < 1 or > 10_000)
            {
                diagnostics.Add(Error(
                    "excluded_directory_name_timeout_invalid",
                    $"폴더 이름 제외 규칙 '{ruleId}'의 timeoutMilliseconds는 1~10000이어야 합니다."));
                valid = false;
            }

            if (valid)
            {
                validated.Add(new ValidatedDirectoryNameExclusionRule(
                    ruleId,
                    pattern!,
                    source.MatchMode,
                    source.IgnoreCase,
                    source.TimeoutMilliseconds));
            }
        }

        return validated;
    }

    private static void ValidateAggregateRegexTimeoutBudget(
        IReadOnlyList<ProfileRegexRuleManifest>? pathRules,
        IReadOnlyList<ProfileDirectoryNameExclusionRuleManifest>? exclusionRules,
        IReadOnlyList<ProfileTextFileFieldManifest>? textFileFields,
        ICollection<ProfileDiagnostic> diagnostics)
    {
        var pathTimeoutMilliseconds = pathRules?.Aggregate(
            0L,
            static (total, rule) => total + Math.Max(0L, rule.TimeoutMilliseconds)) ?? 0L;
        var exclusionTimeoutMilliseconds = exclusionRules?.Aggregate(
            0L,
            static (total, rule) => total + Math.Max(0L, rule.TimeoutMilliseconds)) ?? 0L;
        var textFileTimeoutMilliseconds = textFileFields?.Aggregate(
            0L,
            static (total, field) => total + Math.Max(0L, field.TimeoutMilliseconds)) ?? 0L;
        var aggregateTimeoutMilliseconds =
            pathTimeoutMilliseconds + exclusionTimeoutMilliseconds + textFileTimeoutMilliseconds;

        if (aggregateTimeoutMilliseconds
            <= ProfileManifestLimits.MaximumAggregateRegexTimeoutMilliseconds)
        {
            return;
        }

        diagnostics.Add(Error(
            "regex_timeout_budget_exceeded",
            "한 프로필의 정규식 제한 시간 합계(경로 규칙 + 폴더 이름 제외 규칙 + 텍스트 파일 이름 규칙)는 최대 "
            + $"{ProfileManifestLimits.MaximumAggregateRegexTimeoutMilliseconds:N0}ms여야 합니다. "
            + $"현재 합계: {aggregateTimeoutMilliseconds:N0}ms "
            + $"(경로: {pathTimeoutMilliseconds:N0}ms, "
            + $"폴더 이름 제외: {exclusionTimeoutMilliseconds:N0}ms, "
            + $"텍스트 파일: {textFileTimeoutMilliseconds:N0}ms)."));
    }

    private static List<string> ValidateStopTraversalGroups(
        IReadOnlyList<string>? sourceGroups,
        string ruleId,
        ICollection<ProfileDiagnostic> diagnostics)
    {
        var result = new List<string>();
        if (sourceGroups is null)
        {
            return result;
        }

        if (sourceGroups.Count > ProfileManifestLimits.MaximumStopTraversalGroupCount)
        {
            diagnostics.Add(Error(
                "stop_traversal_group_limit_exceeded",
                $"정규식 규칙 '{ruleId}'의 하위 탐색 중단 그룹은 최대 {ProfileManifestLimits.MaximumStopTraversalGroupCount}개까지 사용할 수 있습니다."));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceGroup in sourceGroups)
        {
            var groupName = NormalizeRequired(sourceGroup);
            if (groupName is null || !GroupNamePattern.IsMatch(groupName))
            {
                diagnostics.Add(Error(
                    "stop_traversal_group_name_invalid",
                    $"정규식 규칙 '{ruleId}'의 하위 탐색 중단 그룹 이름이 올바르지 않습니다."));
                continue;
            }

            if (!seen.Add(groupName))
            {
                diagnostics.Add(Error(
                    "stop_traversal_group_duplicate",
                    $"정규식 규칙 '{ruleId}'의 하위 탐색 중단 그룹이 중복되었습니다: {groupName}"));
                continue;
            }

            result.Add(groupName);
        }

        return result;
    }

    private static bool IsWithinDirectory(string directory, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return path.StartsWith(rootWithSeparator, comparison);
    }

    private static string? NormalizeRequired(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ProfileDiagnostic Error(string code, string message, string? detail = null) =>
        new(ProfileDiagnosticSeverity.Error, code, message, detail);
}
