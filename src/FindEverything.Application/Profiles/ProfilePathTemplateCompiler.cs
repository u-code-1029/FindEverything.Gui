using System.Text;
using System.Text.RegularExpressions;
using FindEverything.Profile.Runtime;

namespace FindEverything.Application.Profiles;

public sealed record ProfilePathTemplateDiagnostic(
    string Code,
    string Message);

public sealed record ProfilePathTemplateCompileResult(
    string? Pattern,
    IReadOnlyList<ProfilePathTemplateDiagnostic> Diagnostics)
{
    public bool IsValid => Pattern is not null && Diagnostics.Count == 0;
}

public interface IProfilePathTemplateCompiler
{
    ProfilePathTemplateCompileResult Compile(
        string? pathTemplate,
        IReadOnlyList<ProfileFieldManifest>? fields);
}

public sealed class ProfilePathTemplateCompiler : IProfilePathTemplateCompiler
{
    private const string PathSeparatorPattern = @"[\\/]";

    private static readonly Regex FieldIdPattern = new(
        "^[a-z0-9][a-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex GroupNamePattern = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public ProfilePathTemplateCompileResult Compile(
        string? pathTemplate,
        IReadOnlyList<ProfileFieldManifest>? fields)
    {
        var diagnostics = new List<ProfilePathTemplateDiagnostic>();
        var fieldsById = ValidateFields(fields, diagnostics);

        if (string.IsNullOrWhiteSpace(pathTemplate))
        {
            diagnostics.Add(Error(
                "template_missing",
                "경로 템플릿을 입력하세요."));
            return Failure(diagnostics);
        }

        var usedGroupNames = new HashSet<string>(StringComparer.Ordinal);
        var segments = pathTemplate
            .Split(['/', '\\'], StringSplitOptions.None)
            .Select(segment => ParseSegment(
                segment,
                fieldsById,
                usedGroupNames,
                diagnostics))
            .ToArray();

        ValidateOptionalSegments(segments, diagnostics);
        foreach (var field in fieldsById.Values)
        {
            var usedGroupCount = field.GroupNames.Count(usedGroupNames.Contains);
            if (usedGroupCount > 0 && usedGroupCount < field.GroupNames.Count)
            {
                diagnostics.Add(Error(
                    "template_composite_field_incomplete",
                    $"복합 필드 '{field.FieldId}'의 모든 경로 조각을 템플릿에 추가하세요."));
            }
            else if (field.Required && usedGroupCount == 0)
            {
                diagnostics.Add(Error(
                    "template_required_field_missing",
                    $"필수 필드 '{field.FieldId}'이(가) 경로 템플릿에 없습니다."));
            }
        }

        if (diagnostics.Count > 0)
        {
            return Failure(diagnostics);
        }

        return new ProfilePathTemplateCompileResult(
            CompileSegments(segments),
            Array.Empty<ProfilePathTemplateDiagnostic>());
    }

    private static IReadOnlyDictionary<string, TemplateField> ValidateFields(
        IReadOnlyList<ProfileFieldManifest>? fields,
        ICollection<ProfilePathTemplateDiagnostic> diagnostics)
    {
        var fieldsById = new Dictionary<string, TemplateField>(StringComparer.OrdinalIgnoreCase);
        if (fields is null || fields.Count == 0)
        {
            diagnostics.Add(Error(
                "template_fields_missing",
                "경로 템플릿에는 하나 이상의 결과 필드가 필요합니다."));
            return fieldsById;
        }

        for (var index = 0; index < fields.Count; index++)
        {
            var source = fields[index];
            if (source is null)
            {
                diagnostics.Add(Error(
                    "template_field_id_invalid",
                    $"필드 #{index + 1}의 fieldId가 올바르지 않습니다."));
                continue;
            }

            var fieldId = Normalize(source.FieldId);
            var displayId = fieldId ?? $"#{index + 1}";

            if (fieldId is null || !FieldIdPattern.IsMatch(fieldId))
            {
                diagnostics.Add(Error(
                    "template_field_id_invalid",
                    $"필드 #{index + 1}의 fieldId가 올바르지 않습니다."));
                continue;
            }

            var groupNames = ValidateGroupNames(source, displayId, diagnostics);
            var field = new TemplateField(fieldId, groupNames, source.Required, source.Kind);
            if (!fieldsById.TryAdd(fieldId, field))
            {
                diagnostics.Add(Error(
                    "template_field_definition_duplicate",
                    $"경로 템플릿 필드 정의가 중복되었습니다: {fieldId}"));
                continue;
            }

            if (!Enum.IsDefined(source.Kind))
            {
                diagnostics.Add(Error(
                    "template_field_kind_invalid",
                    $"필드 '{displayId}'의 값 타입이 지원되지 않습니다."));
            }

        }

        return fieldsById;
    }

    private static IReadOnlyList<string> ValidateGroupNames(
        ProfileFieldManifest source,
        string displayId,
        ICollection<ProfilePathTemplateDiagnostic> diagnostics)
    {
        var groupName = Normalize(source.GroupName);
        if (groupName is not null && source.GroupNames is not null)
        {
            diagnostics.Add(Error(
                "template_group_sources_conflict",
                $"필드 '{displayId}'에는 groupName과 groupNames를 동시에 사용할 수 없습니다."));
            return Array.Empty<string>();
        }

        if (source.GroupNames is null)
        {
            if (groupName is null || !GroupNamePattern.IsMatch(groupName))
            {
                diagnostics.Add(Error(
                    "template_group_name_invalid",
                    $"필드 '{displayId}'의 정규식 그룹 이름이 올바르지 않습니다."));
                return Array.Empty<string>();
            }

            return new[] { groupName };
        }

        if (source.GroupNames.Count == 0)
        {
            diagnostics.Add(Error(
                "template_group_names_missing",
                $"필드 '{displayId}'의 groupNames에는 하나 이상의 그룹이 필요합니다."));
            return Array.Empty<string>();
        }

        if (source.GroupNames.Count > ProfileManifestLimits.MaximumCompositeGroupCount)
        {
            diagnostics.Add(Error(
                "template_group_names_limit_exceeded",
                $"필드 '{displayId}'의 groupNames는 최대 {ProfileManifestLimits.MaximumCompositeGroupCount}개까지 사용할 수 있습니다."));
        }

        var result = new List<string>(source.GroupNames.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceGroupName in source.GroupNames)
        {
            var normalized = Normalize(sourceGroupName);
            if (normalized is null || !GroupNamePattern.IsMatch(normalized))
            {
                diagnostics.Add(Error(
                    "template_group_name_invalid",
                    $"필드 '{displayId}'의 groupNames에 올바르지 않은 그룹 이름이 있습니다."));
                continue;
            }

            if (!seen.Add(normalized))
            {
                diagnostics.Add(Error(
                    "template_group_name_duplicate",
                    $"필드 '{displayId}'의 groupNames에 중복된 그룹이 있습니다: {normalized}"));
                continue;
            }

            result.Add(normalized);
        }

        return result;
    }

    private static TemplateSegment ParseSegment(
        string source,
        IReadOnlyDictionary<string, TemplateField> fieldsById,
        ISet<string> usedGroupNames,
        ICollection<ProfilePathTemplateDiagnostic> diagnostics)
    {
        var nodes = new List<TemplateNode>();
        var literal = new StringBuilder();
        var placeholderCount = 0;
        var optionalPlaceholderCount = 0;

        void FlushLiteral()
        {
            if (literal.Length == 0)
            {
                return;
            }

            nodes.Add(new LiteralNode(literal.ToString()));
            literal.Clear();
        }

        for (var index = 0; index < source.Length;)
        {
            var current = source[index];
            if (current == '{')
            {
                if (index + 1 < source.Length && source[index + 1] == '{')
                {
                    literal.Append('{');
                    index += 2;
                    continue;
                }

                FlushLiteral();
                var closingBrace = source.IndexOf('}', index + 1);
                if (closingBrace < 0)
                {
                    diagnostics.Add(Error(
                        "template_placeholder_unclosed",
                        "경로 템플릿의 필드 플레이스홀더에 닫는 중괄호가 없습니다."));
                    break;
                }

                var token = source[(index + 1)..closingBrace];
                var isOptional = token.EndsWith('?');
                var tokenBody = isOptional ? token[..^1] : token;
                var separatorIndex = tokenBody.IndexOf('@');
                var fieldId = separatorIndex < 0
                    ? tokenBody
                    : tokenBody[..separatorIndex];
                var groupName = separatorIndex < 0
                    ? null
                    : tokenBody[(separatorIndex + 1)..];
                if (!FieldIdPattern.IsMatch(fieldId)
                    || (groupName is not null && !GroupNamePattern.IsMatch(groupName))
                    || (separatorIndex >= 0
                        && tokenBody.IndexOf('@', separatorIndex + 1) >= 0))
                {
                    diagnostics.Add(Error(
                        "template_placeholder_invalid",
                        $"경로 템플릿 플레이스홀더 '{{{token}}}'의 형식이 올바르지 않습니다."));
                    index = closingBrace + 1;
                    continue;
                }

                placeholderCount++;
                if (isOptional)
                {
                    optionalPlaceholderCount++;
                }

                if (!fieldsById.TryGetValue(fieldId, out var field))
                {
                    diagnostics.Add(Error(
                        "template_field_unknown",
                        $"경로 템플릿에서 알 수 없는 필드 '{fieldId}'을(를) 사용했습니다."));
                    index = closingBrace + 1;
                    continue;
                }

                string captureGroupName;
                if (groupName is null)
                {
                    if (field.GroupNames.Count != 1)
                    {
                        diagnostics.Add(Error(
                            "template_composite_field_requires_component",
                            $"복합 필드 '{field.FieldId}'은(는) '{{필드@그룹}}' 형식으로 경로 조각을 선택해야 합니다."));
                        index = closingBrace + 1;
                        continue;
                    }

                    captureGroupName = field.GroupNames[0];
                }
                else if (!field.GroupNames.Contains(groupName, StringComparer.Ordinal))
                {
                    diagnostics.Add(Error(
                        "template_component_unknown",
                        $"필드 '{field.FieldId}'에 그룹 '{groupName}'이(가) 정의되어 있지 않습니다."));
                    index = closingBrace + 1;
                    continue;
                }
                else
                {
                    captureGroupName = groupName;
                }

                if (!usedGroupNames.Add(captureGroupName))
                {
                    diagnostics.Add(Error(
                        groupName is null
                            ? "template_field_duplicate"
                            : "template_component_duplicate",
                        groupName is null
                            ? $"필드 '{field.FieldId}'은(는) 경로 템플릿에서 한 번만 사용할 수 있습니다."
                            : $"경로 조각 '{field.FieldId}@{groupName}'은(는) 템플릿에서 한 번만 사용할 수 있습니다."));
                }

                if (isOptional && field.Required)
                {
                    diagnostics.Add(Error(
                        "template_optional_field_required",
                        $"필수 필드 '{field.FieldId}'에는 선택 플레이스홀더를 사용할 수 없습니다."));
                }
                else if (!isOptional && !field.Required)
                {
                    diagnostics.Add(Error(
                        "template_optional_field_not_optional",
                        $"선택 필드 '{field.FieldId}'에는 '?'가 있는 선택 플레이스홀더를 사용해야 합니다."));
                }

                nodes.Add(new FieldNode(field, captureGroupName));
                index = closingBrace + 1;
                continue;
            }

            if (current == '}')
            {
                if (index + 1 < source.Length && source[index + 1] == '}')
                {
                    literal.Append('}');
                    index += 2;
                    continue;
                }

                diagnostics.Add(Error(
                    "template_literal_brace_unescaped",
                    "경로 템플릿의 리터럴 중괄호는 '{{' 또는 '}}'로 입력해야 합니다."));
                literal.Append('}');
                index++;
                continue;
            }

            literal.Append(current);
            index++;
        }

        FlushLiteral();
        return new TemplateSegment(
            nodes,
            placeholderCount,
            optionalPlaceholderCount);
    }

    private static void ValidateOptionalSegments(
        IReadOnlyList<TemplateSegment> segments,
        ICollection<ProfilePathTemplateDiagnostic> diagnostics)
    {
        var foundOptionalSegment = false;
        foreach (var segment in segments)
        {
            if (segment.IsOptional)
            {
                foundOptionalSegment = true;
                if (segment.PlaceholderCount > 1)
                {
                    diagnostics.Add(Error(
                        "template_optional_segment_multiple_placeholders",
                        "선택 경로 세그먼트에는 플레이스홀더를 하나만 사용할 수 있습니다."));
                }

                continue;
            }

            if (foundOptionalSegment)
            {
                diagnostics.Add(Error(
                    "template_optional_segment_not_trailing",
                    "선택 경로 세그먼트는 템플릿 끝에 연속해서 배치해야 합니다."));
                return;
            }
        }
    }

    private static string CompileSegments(IReadOnlyList<TemplateSegment> segments)
    {
        var firstOptionalSegment = -1;
        for (var index = 0; index < segments.Count; index++)
        {
            if (segments[index].IsOptional)
            {
                firstOptionalSegment = index;
                break;
            }
        }

        if (firstOptionalSegment < 0)
        {
            return string.Join(
                PathSeparatorPattern,
                segments.Select(CompileSegment));
        }

        var pattern = new StringBuilder();
        if (firstOptionalSegment > 0)
        {
            pattern.Append(string.Join(
                PathSeparatorPattern,
                segments.Take(firstOptionalSegment).Select(CompileSegment)));
        }

        var optionalSuffix = string.Empty;
        for (var index = segments.Count - 1; index >= firstOptionalSegment; index--)
        {
            var separator = index == 0 ? string.Empty : PathSeparatorPattern;
            optionalSuffix = $"(?:{separator}{CompileSegment(segments[index])}{optionalSuffix})?";
        }

        pattern.Append(optionalSuffix);
        return pattern.ToString();
    }

    private static string CompileSegment(TemplateSegment segment)
    {
        var pattern = new StringBuilder();
        foreach (var node in segment.Nodes)
        {
            switch (node)
            {
                case LiteralNode literal:
                    pattern.Append(Regex.Escape(literal.Value));
                    break;

                case FieldNode field:
                    pattern
                        .Append("(?<")
                        .Append(field.GroupName)
                        .Append('>')
                        .Append(ValuePattern(field.Field.Kind))
                        .Append(')');
                    break;
            }
        }

        return pattern.ToString();
    }

    private static string ValuePattern(ProfileFieldValueKind kind) => kind switch
    {
        ProfileFieldValueKind.String => @"[^\\/]+",
        ProfileFieldValueKind.Int32 => @"[+-]?\d+",
        ProfileFieldValueKind.Decimal => @"[+-]?\d+(?:\.\d+)?",
        ProfileFieldValueKind.DateTime => @"[^\\/]+",
        ProfileFieldValueKind.Boolean => @"(?:true|false)",
        _ => "(?!)",
    };

    private static ProfilePathTemplateCompileResult Failure(
        IEnumerable<ProfilePathTemplateDiagnostic> diagnostics) =>
        new(
            null,
            Array.AsReadOnly(diagnostics.ToArray()));

    private static ProfilePathTemplateDiagnostic Error(string code, string message) =>
        new(code, message);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record TemplateField(
        string FieldId,
        IReadOnlyList<string> GroupNames,
        bool Required,
        ProfileFieldValueKind Kind);

    private sealed record TemplateSegment(
        IReadOnlyList<TemplateNode> Nodes,
        int PlaceholderCount,
        int OptionalPlaceholderCount)
    {
        public bool IsOptional => OptionalPlaceholderCount > 0;
    }

    private abstract record TemplateNode;

    private sealed record LiteralNode(string Value) : TemplateNode;

    private sealed record FieldNode(
        TemplateField Field,
        string GroupName) : TemplateNode;
}
