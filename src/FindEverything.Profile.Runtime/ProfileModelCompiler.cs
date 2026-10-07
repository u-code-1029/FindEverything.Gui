using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using FindEverything.Profile.Abstractions;

namespace FindEverything.Profile.Runtime;

internal sealed record ProfileCompilationResult(
    ILoadedProfile? Profile,
    IReadOnlyList<ProfileDiagnostic> Diagnostics);

internal sealed class ProfileModelCompiler
{
    private static readonly Regex FieldIdPattern = new(
        "^[a-z0-9][a-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex GroupNamePattern = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public ProfileCompilationResult Compile(Assembly assembly, ValidatedProfileManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(manifest);

        var diagnostics = new List<ProfileDiagnostic>();
        if (manifest.Kind != ProfileKind.Assembly || manifest.ModelType is null)
        {
            return Failure(
                "assembly_profile_contract_invalid",
                "Assembly 컴파일러에는 modelType이 있는 Assembly 프로필이 필요합니다.");
        }

        var modelType = assembly.GetType(manifest.ModelType, throwOnError: false, ignoreCase: false);
        if (modelType is null)
        {
            return Failure(
                "model_type_not_found",
                $"모델 타입을 찾을 수 없습니다: {manifest.ModelType}");
        }

        if (!modelType.IsClass || modelType.IsAbstract || !(modelType.IsPublic || modelType.IsNestedPublic))
        {
            return Failure(
                "model_type_invalid",
                "모델 타입은 public concrete class여야 합니다.");
        }

        var constructor = modelType.GetConstructor(Type.EmptyTypes);
        if (constructor is null || !constructor.IsPublic)
        {
            return Failure(
                "model_constructor_missing",
                "모델 타입에는 public 매개 변수 없는 생성자가 필요합니다.");
        }

        var fields = CompileFields(modelType, diagnostics);
        if (fields.Count == 0)
        {
            diagnostics.Add(Error(
                "capture_fields_missing",
                "CaptureFieldAttribute가 지정된 public 속성이 하나 이상 필요합니다."));
        }

        Func<IReadOnlyDictionary<string, object?>, object>? createModel = null;
        if (!diagnostics.Any(static diagnostic =>
                diagnostic.Severity == ProfileDiagnosticSeverity.Error))
        {
            var constructorFactory = CompileConstructor(constructor);
            var compiledFields = fields.ToArray();
            createModel = values =>
            {
                var model = constructorFactory();
                foreach (var field in compiledFields)
                {
                    field.SetValue(model, values[field.Descriptor.FieldId]);
                }

                return model;
            };
        }

        return CompileProfile(manifest, fields, createModel, diagnostics);
    }

    public ProfileCompilationResult Compile(ValidatedProfileManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.Kind != ProfileKind.Declarative)
        {
            return Failure(
                "declarative_profile_contract_invalid",
                "선언형 컴파일러에는 Declarative 프로필이 필요합니다.");
        }

        var fields = manifest.Fields
            .Select(static field => new CompiledField(
                new ProfileFieldDescriptor(
                    field.FieldId,
                    field.GroupNames[0],
                    field.Header,
                    field.Order,
                    field.Required,
                    field.Kind,
                    field.IsNullable,
                    field.ParseFormat,
                    field.DisplayFormat)
                {
                    GroupNames = field.GroupNames,
                },
                static (_, _) => { }))
            .ToList();

        return CompileProfile(
            manifest,
            fields,
            static values => values,
            new List<ProfileDiagnostic>());
    }

    private static ProfileCompilationResult CompileProfile(
        ValidatedProfileManifest manifest,
        IReadOnlyList<CompiledField> fields,
        Func<IReadOnlyDictionary<string, object?>, object>? createModel,
        List<ProfileDiagnostic> diagnostics)
    {
        var rules = CompileRules(manifest.Rules, fields, diagnostics);
        var excludedDirectoryNameRules = CompileExcludedDirectoryNameRules(
            manifest.ExcludedDirectoryNameRules,
            diagnostics);
        if (diagnostics.Any(static diagnostic =>
                diagnostic.Severity == ProfileDiagnosticSeverity.Error))
        {
            return new ProfileCompilationResult(
                null,
                Array.AsReadOnly(diagnostics.ToArray()));
        }

        if (createModel is null)
        {
            return Failure(
                "model_factory_missing",
                "프로필 모델 팩터리를 만들 수 없습니다.");
        }

        var descriptors = fields.Select(static field => field.Descriptor).ToArray();
        var descriptor = new ProfileDescriptor(
            manifest.Id,
            manifest.Version,
            manifest.DisplayName,
            manifest.CandidateKind,
            Array.AsReadOnly(descriptors),
            Array.AsReadOnly(manifest.Rules
                .Select(static (rule, index) => new ProfileRegexRuleDescriptor(
                    index + 1,
                    rule.Id,
                    rule.Pattern,
                    rule.MatchMode,
                    rule.IgnoreCase,
                    rule.TimeoutMilliseconds)
                {
                    StopTraversalWhenCapturedGroups =
                        rule.StopTraversalWhenCapturedGroups,
                })
                .ToArray()))
        {
            Kind = manifest.Kind,
            ExcludedDirectoryNameRules = Array.AsReadOnly(manifest.ExcludedDirectoryNameRules
                .Select(static (rule, index) =>
                    new ProfileDirectoryNameExclusionRuleDescriptor(
                        index + 1,
                        rule.Id,
                        rule.Pattern,
                        rule.MatchMode,
                        rule.IgnoreCase,
                        rule.TimeoutMilliseconds))
                .ToArray()),
        };

        var profile = new CompiledProfile(
            descriptor,
            createModel,
            fields.ToArray(),
            rules.ToArray(),
            excludedDirectoryNameRules.ToArray());

        return new ProfileCompilationResult(
            profile,
            Array.AsReadOnly(diagnostics.ToArray()));
    }

    private static List<CompiledField> CompileFields(
        Type modelType,
        ICollection<ProfileDiagnostic> diagnostics)
    {
        var fields = new List<CompiledField>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in modelType
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .OrderBy(static property => property.MetadataToken))
        {
            var attribute = property.GetCustomAttribute<CaptureFieldAttribute>(inherit: true);
            if (attribute is null)
            {
                continue;
            }

            if (property.GetIndexParameters().Length != 0)
            {
                diagnostics.Add(Error(
                    "capture_field_indexer",
                    $"인덱서 속성은 캡처 필드로 사용할 수 없습니다: {property.Name}"));
                continue;
            }

            var setter = property.SetMethod;
            if (setter is null || !setter.IsPublic)
            {
                diagnostics.Add(Error(
                    "capture_field_setter_missing",
                    $"캡처 필드에는 public setter가 필요합니다: {property.Name}"));
                continue;
            }

            if (!FieldIdPattern.IsMatch(attribute.FieldId))
            {
                diagnostics.Add(Error(
                    "capture_field_id_invalid",
                    $"캡처 필드 id가 올바르지 않습니다: {attribute.FieldId}"));
                continue;
            }

            if (!ids.Add(attribute.FieldId))
            {
                diagnostics.Add(Error(
                    "capture_field_id_duplicate",
                    $"캡처 필드 id가 중복되었습니다: {attribute.FieldId}"));
                continue;
            }

            var groupNames = attribute.GroupNames;
            if (groupNames.Count == 0)
            {
                diagnostics.Add(Error(
                    "capture_group_name_missing",
                    $"정규식 그룹 이름이 필요합니다: {property.Name}"));
                continue;
            }

            if (groupNames.Count > ProfileManifestLimits.MaximumCompositeGroupCount)
            {
                diagnostics.Add(Error(
                    "capture_group_names_limit_exceeded",
                    $"캡처 필드 '{property.Name}'의 정규식 그룹은 최대 {ProfileManifestLimits.MaximumCompositeGroupCount}개까지 사용할 수 있습니다."));
                continue;
            }

            var groupNamesValid = true;
            var seenGroupNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var groupName in groupNames)
            {
                if (string.IsNullOrWhiteSpace(groupName)
                    || !GroupNamePattern.IsMatch(groupName))
                {
                    diagnostics.Add(Error(
                        "capture_group_name_invalid",
                        $"캡처 필드 '{property.Name}'의 정규식 그룹 이름이 올바르지 않습니다: {groupName}"));
                    groupNamesValid = false;
                    continue;
                }

                if (!seenGroupNames.Add(groupName))
                {
                    diagnostics.Add(Error(
                        "capture_group_name_duplicate",
                        $"캡처 필드 '{property.Name}'의 정규식 그룹 이름이 중복되었습니다: {groupName}"));
                    groupNamesValid = false;
                }
            }

            if (!groupNamesValid)
            {
                continue;
            }

            if (!TryGetFieldType(property.PropertyType, out var kind, out var isNullable))
            {
                diagnostics.Add(Error(
                    "capture_field_type_unsupported",
                    $"지원되지 않는 캡처 필드 타입입니다: {property.Name} ({property.PropertyType.FullName})"));
                continue;
            }

            if (!attribute.Required && property.PropertyType.IsValueType && !isNullable)
            {
                diagnostics.Add(Error(
                    "optional_field_not_nullable",
                    $"선택 값 형식 속성은 nullable이어야 합니다: {property.Name}"));
                continue;
            }

            var parseFormat = NormalizeOptional(attribute.ParseFormat);
            if (groupNames.Count > 1
                && kind == ProfileFieldValueKind.DateTime
                && parseFormat is null)
            {
                diagnostics.Add(Error(
                    "capture_composite_datetime_parse_format_missing",
                    $"복합 날짜 필드 '{property.Name}'에는 ParseFormat이 필요합니다."));
                continue;
            }

            var header = string.IsNullOrWhiteSpace(attribute.Header)
                ? property.Name
                : attribute.Header.Trim();
            var descriptor = new ProfileFieldDescriptor(
                attribute.FieldId,
                groupNames[0],
                header,
                attribute.Order,
                attribute.Required,
                kind,
                isNullable,
                parseFormat,
                NormalizeOptional(attribute.DisplayFormat))
            {
                GroupNames = Array.AsReadOnly(groupNames.ToArray()),
            };

            fields.Add(new CompiledField(
                descriptor,
                CompileSetter(modelType, property.PropertyType, setter)));
        }

        fields.Sort(static (left, right) =>
        {
            var orderComparison = left.Descriptor.Order.CompareTo(right.Descriptor.Order);
            return orderComparison != 0
                ? orderComparison
                : StringComparer.Ordinal.Compare(left.Descriptor.FieldId, right.Descriptor.FieldId);
        });

        return fields;
    }

    private static List<CompiledRegexRule> CompileRules(
        IReadOnlyList<ValidatedRegexRule> sourceRules,
        IReadOnlyList<CompiledField> fields,
        ICollection<ProfileDiagnostic> diagnostics)
    {
        var rules = new List<CompiledRegexRule>(sourceRules.Count);
        foreach (var sourceRule in sourceRules)
        {
            try
            {
                var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
                if (sourceRule.IgnoreCase)
                {
                    options |= RegexOptions.IgnoreCase;
                }

                var effectivePattern = sourceRule.MatchMode == ProfileRegexMatchMode.Full
                    ? $"\\A(?:{sourceRule.Pattern})\\z"
                    : sourceRule.Pattern;
                var regex = new Regex(
                    effectivePattern,
                    options,
                    TimeSpan.FromMilliseconds(sourceRule.TimeoutMilliseconds));
                var fieldGroupNumbers = new int[fields.Count][];

                for (var index = 0; index < fields.Count; index++)
                {
                    var field = fields[index];
                    var groupNames = field.Descriptor.EffectiveGroupNames;
                    var groupNumbers = new int[groupNames.Count];
                    fieldGroupNumbers[index] = groupNumbers;
                    for (var groupIndex = 0; groupIndex < groupNames.Count; groupIndex++)
                    {
                        var groupName = groupNames[groupIndex];
                        var groupNumber = regex.GroupNumberFromName(groupName);
                        groupNumbers[groupIndex] = groupNumber;
                        if (groupNumber < 0 && field.Descriptor.Required)
                        {
                            diagnostics.Add(Error(
                                "required_group_not_defined",
                                $"규칙 '{sourceRule.Id}'에 필수 그룹 '{groupName}'이(가) 없습니다."));
                        }
                    }
                }

                var stopTraversalGroupNumbers = sourceRule.StopTraversalWhenCapturedGroups
                    .Select(groupName => new
                    {
                        Name = groupName,
                        Number = regex.GroupNumberFromName(groupName),
                    })
                    .ToArray();
                foreach (var group in stopTraversalGroupNumbers)
                {
                    if (group.Number < 0)
                    {
                        diagnostics.Add(Error(
                            "stop_traversal_group_not_defined",
                            $"규칙 '{sourceRule.Id}'에 하위 탐색 중단 그룹 '{group.Name}'이(가) 없습니다."));
                    }
                }

                rules.Add(new CompiledRegexRule(
                    sourceRule.Id,
                    sourceRule.MatchMode,
                    regex,
                    fieldGroupNumbers,
                    stopTraversalGroupNumbers
                        .Select(static group => group.Number)
                        .ToArray()));
            }
            catch (ArgumentException exception)
            {
                diagnostics.Add(Error(
                    "regex_invalid",
                    $"정규식 규칙 '{sourceRule.Id}'을(를) 컴파일할 수 없습니다.",
                    exception.Message));
            }
        }

        return rules;
    }

    private static List<CompiledDirectoryNameExclusionRule>
        CompileExcludedDirectoryNameRules(
            IReadOnlyList<ValidatedDirectoryNameExclusionRule> sourceRules,
            ICollection<ProfileDiagnostic> diagnostics)
    {
        var rules = new List<CompiledDirectoryNameExclusionRule>(sourceRules.Count);
        foreach (var sourceRule in sourceRules)
        {
            try
            {
                var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
                if (sourceRule.IgnoreCase)
                {
                    options |= RegexOptions.IgnoreCase;
                }

                var effectivePattern = sourceRule.MatchMode == ProfileRegexMatchMode.Full
                    ? $"\\A(?:{sourceRule.Pattern})\\z"
                    : sourceRule.Pattern;
                rules.Add(new CompiledDirectoryNameExclusionRule(
                    sourceRule.Id,
                    new Regex(
                        effectivePattern,
                        options,
                        TimeSpan.FromMilliseconds(sourceRule.TimeoutMilliseconds))));
            }
            catch (ArgumentException exception)
            {
                diagnostics.Add(Error(
                    "excluded_directory_name_regex_invalid",
                    $"폴더 이름 제외 규칙 '{sourceRule.Id}'을(를) 컴파일할 수 없습니다.",
                    exception.Message));
            }
        }

        return rules;
    }

    private static Func<object> CompileConstructor(ConstructorInfo constructor)
    {
        var body = Expression.Convert(Expression.New(constructor), typeof(object));
        return Expression.Lambda<Func<object>>(body).Compile();
    }

    private static Action<object, object?> CompileSetter(
        Type modelType,
        Type propertyType,
        MethodInfo setter)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(object), "value");
        var call = Expression.Call(
            Expression.Convert(instance, modelType),
            setter,
            Expression.Convert(value, propertyType));
        return Expression.Lambda<Action<object, object?>>(call, instance, value).Compile();
    }

    private static bool TryGetFieldType(
        Type propertyType,
        out ProfileFieldValueKind kind,
        out bool isNullable)
    {
        var nullableUnderlyingType = Nullable.GetUnderlyingType(propertyType);
        var valueType = nullableUnderlyingType ?? propertyType;
        isNullable = !propertyType.IsValueType || nullableUnderlyingType is not null;

        if (valueType == typeof(string))
        {
            kind = ProfileFieldValueKind.String;
            return true;
        }

        if (valueType == typeof(int))
        {
            kind = ProfileFieldValueKind.Int32;
            return true;
        }

        if (valueType == typeof(decimal))
        {
            kind = ProfileFieldValueKind.Decimal;
            return true;
        }

        if (valueType == typeof(DateTime))
        {
            kind = ProfileFieldValueKind.DateTime;
            return true;
        }

        if (valueType == typeof(bool))
        {
            kind = ProfileFieldValueKind.Boolean;
            return true;
        }

        kind = default;
        return false;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ProfileCompilationResult Failure(string code, string message) =>
        new(
            null,
            new[]
            {
                Error(code, message),
            });

    private static ProfileDiagnostic Error(string code, string message, string? detail = null) =>
        new(ProfileDiagnosticSeverity.Error, code, message, detail);
}

internal sealed record CompiledField(
    ProfileFieldDescriptor Descriptor,
    Action<object, object?> SetValue);

internal sealed record CompiledRegexRule(
    string Id,
    ProfileRegexMatchMode MatchMode,
    Regex Regex,
    int[][] FieldGroupNumbers,
    int[] StopTraversalGroupNumbers);

internal sealed record CompiledDirectoryNameExclusionRule(
    string Id,
    Regex Regex);
