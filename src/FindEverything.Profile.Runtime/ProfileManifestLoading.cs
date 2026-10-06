using System.Text.Json;
using System.Text.RegularExpressions;

namespace FindEverything.Profile.Runtime;

internal sealed record ValidatedRegexRule(
    string Id,
    string Pattern,
    ProfileRegexMatchMode MatchMode,
    bool IgnoreCase,
    int TimeoutMilliseconds);

internal sealed record ValidatedProfileManifest(
    string SourceDirectory,
    string Id,
    string Version,
    string DisplayName,
    string EntryAssemblyPath,
    string ModelType,
    ProfileCandidateKind CandidateKind,
    ProfilePathInput PathInput,
    IReadOnlyList<ValidatedRegexRule> Rules);

internal sealed record ManifestReadResult(
    ValidatedProfileManifest? Manifest,
    string? ProfileId,
    string? DisplayName,
    IReadOnlyList<ProfileDiagnostic> Diagnostics);

internal sealed class ProfileManifestReader
{
    private const long MaximumManifestLength = 1024 * 1024;

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
            if (manifestFile.Length > MaximumManifestLength)
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

        if (modelType is null)
        {
            diagnostics.Add(Error("profile_model_type_missing", "프로필 modelType이 필요합니다."));
        }

        if (!Enum.IsDefined(manifest.CandidateKind))
        {
            diagnostics.Add(Error("candidate_kind_invalid", "지원되지 않는 candidateKind입니다."));
        }

        if (!Enum.IsDefined(manifest.PathInput))
        {
            diagnostics.Add(Error("path_input_invalid", "지원되지 않는 pathInput입니다."));
        }

        string? entryAssemblyPath = null;
        if (entryAssembly is null)
        {
            diagnostics.Add(Error("entry_assembly_missing", "프로필 entryAssembly가 필요합니다."));
        }
        else
        {
            try
            {
                entryAssemblyPath = Path.GetFullPath(Path.Combine(sourceDirectory, entryAssembly));
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
                else if (!string.Equals(Path.GetExtension(entryAssemblyPath), ".dll", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(Error(
                        "entry_assembly_not_dll",
                        "entryAssembly는 .dll 파일이어야 합니다."));
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                diagnostics.Add(Error(
                    "entry_assembly_invalid_path",
                    "entryAssembly 경로가 올바르지 않습니다.",
                    exception.Message));
            }
        }

        var validatedRules = ValidateRules(manifest.Rules, diagnostics);

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
                entryAssemblyPath!,
                modelType!,
                manifest.CandidateKind,
                manifest.PathInput,
                Array.AsReadOnly(validatedRules.ToArray())),
            id,
            displayName,
            Array.AsReadOnly(diagnostics.ToArray()));
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

        var ruleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < rules.Count; index++)
        {
            var source = rules[index];
            var ruleId = NormalizeRequired(source.Id) ?? $"rule-{index + 1}";
            var pattern = source.Pattern;

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
                && RuleIdPattern.IsMatch(ruleId)
                && Enum.IsDefined(source.MatchMode)
                && source.TimeoutMilliseconds is >= 1 and <= 10_000)
            {
                validated.Add(new ValidatedRegexRule(
                    ruleId,
                    pattern,
                    source.MatchMode,
                    source.IgnoreCase,
                    source.TimeoutMilliseconds));
            }
        }

        return validated;
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
