using System.Text.Json.Serialization;

namespace FindEverything.Profile.Runtime;

/// <summary>A portable snapshot, not machine paths, database contents, or application-wide preferences.</summary>
public sealed class ProfileWorkspaceManifest
{
    public int Version { get; set; } = 1;
    public List<ProfileOutputFormatManifest>? OutputFormats { get; set; }
    public string? SelectedOutputFormatId { get; set; }
    public List<ProfileColumnPresentationManifest>? Columns { get; set; }
}

public sealed class ProfileOutputFormatManifest
{
    // Identifiers are scoped to the containing profile, never to another installed profile.
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public string? Template { get; set; }
    public string ItemSeparator { get; set; } = "\r\n";
    public string Prefix { get; set; } = "";
    public string Suffix { get; set; } = "";
}

public enum ProfileColumnWidthPreset { Default, Narrow, Wide }
public enum ProfileColumnWidthMode { Auto, Manual }

public sealed class ProfileColumnPresentationManifest
{
    public string? FieldId { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter<ProfileColumnWidthPreset>))]
    public ProfileColumnWidthPreset WidthPreset { get; set; }
    public int? DisplayIndex { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter<ProfileColumnWidthMode>))]
    public ProfileColumnWidthMode WidthMode { get; set; }
    public double? Width { get; set; }
    public bool IsHidden { get; set; }
}

public static class ProfileWorkspaceManifestValidator
{
    public const int MaximumFormats = 64;
    public const int MaximumColumns = 512;
    public const string BuiltInFullPathFormatId = "builtin.full-path-lines";

    public static IReadOnlyList<ProfileDiagnostic> Validate(ProfileManifest manifest)
    {
        if (manifest.Workspace is not { } workspace) return [];
        var errors = new List<ProfileDiagnostic>();
        void Error(string message) => errors.Add(new(ProfileDiagnosticSeverity.Error, "workspace_invalid", message));
        if (workspace.Version != 1) Error("프로필의 화면·출력 설정 버전은 1이어야 합니다.");
        if (workspace.OutputFormats is { Count: > MaximumFormats }) Error("프로필의 출력 포맷은 최대 64개입니다.");
        if (workspace.Columns is { Count: > MaximumColumns }) Error("프로필의 컬럼 표시 설정은 최대 512개입니다.");
        var formatIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var format in (workspace.OutputFormats ?? []).Take(MaximumFormats + 1))
        {
            if (format is null) { Error("출력 포맷은 null일 수 없습니다."); continue; }
            if (string.IsNullOrWhiteSpace(format.Id) || format.Id.Length > 128 || format.Id != format.Id.Trim()
                || !formatIds.Add(format.Id) || string.Equals(format.Id, BuiltInFullPathFormatId, StringComparison.OrdinalIgnoreCase))
                Error("출력 포맷 ID는 중복되지 않는 1~128자여야 하며 기본 포맷 ID를 사용할 수 없습니다.");
            if (string.IsNullOrWhiteSpace(format.DisplayName) || format.DisplayName.Length > 100)
                Error("출력 포맷 이름은 1~100자여야 합니다.");
            if (string.IsNullOrEmpty(format.Template) || format.Template.Length > 16_384
                || format.ItemSeparator is null || format.ItemSeparator.Length > 1_024
                || format.Prefix is null || format.Prefix.Length > 16_384
                || format.Suffix is null || format.Suffix.Length > 16_384)
                Error("출력 포맷의 템플릿, 구분자 또는 앞·뒤 문구가 허용 길이를 벗어났습니다.");
        }
        if (workspace.SelectedOutputFormatId is { } selected && !formatIds.Contains(selected)
            && !string.Equals(selected, BuiltInFullPathFormatId, StringComparison.OrdinalIgnoreCase))
            Error("선택한 출력 포맷은 이 프로필에 포함된 포맷 또는 기본 전체 경로 포맷이어야 합니다.");
        var knownFields = (manifest.Fields ?? []).Where(f => f is not null).Select(f => f.FieldId)
            .Concat((manifest.TextFileFields ?? []).Where(f => f is not null).Select(f => f.FieldId))
            .Concat((manifest.UserInputFields ?? []).Where(f => f is not null).Select(f => f.FieldId))
            .Concat((manifest.ChildDirectoryFields ?? []).Where(f => f is not null).Select(f => f.FieldId))
            .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        knownFields.Add("$ui:path"); knownFields.Add("$ui:status");
        var columnIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indices = new HashSet<int>();
        foreach (var column in (workspace.Columns ?? []).Take(MaximumColumns + 1))
        {
            if (column is null) { Error("컬럼 표시 설정은 null일 수 없습니다."); continue; }
            if (string.IsNullOrWhiteSpace(column.FieldId) || !knownFields.Contains(column.FieldId) || !columnIds.Add(column.FieldId))
                Error("컬럼 표시 설정은 프로필에 정의된 컬럼을 중복 없이 참조해야 합니다.");
            if (!Enum.IsDefined(column.WidthPreset) || !Enum.IsDefined(column.WidthMode))
                Error("컬럼 너비 설정 값이 올바르지 않습니다.");
            if (column.DisplayIndex is { } index && (index < 0 || index > MaximumColumns || !indices.Add(index)))
                Error("컬럼 표시 순서는 중복되지 않는 0~512 정수여야 합니다.");
            if (column.Width is { } width && (!double.IsFinite(width) || width is < 24 or > 16_384)
                || column.WidthMode == ProfileColumnWidthMode.Manual && column.Width is null)
                Error("수동 컬럼 너비는 24~16,384 사이의 유한한 수여야 합니다.");
        }
        return errors;
    }
}
