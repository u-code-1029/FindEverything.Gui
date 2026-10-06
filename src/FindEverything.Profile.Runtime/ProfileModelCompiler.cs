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

    public ProfileCompilationResult Compile(Assembly assembly, ValidatedProfileManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(manifest);

        var diagnostics = new List<ProfileDiagnostic>();
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

        var rules = CompileRules(manifest.Rules, fields, diagnostics);
        if (diagnostics.Any(static diagnostic =>
                diagnostic.Severity == ProfileDiagnosticSeverity.Error))
        {
            return new ProfileCompilationResult(
                null,
                Array.AsReadOnly(diagnostics.ToArray()));
        }

        var createModel = CompileConstructor(constructor);
        var descriptors = fields.Select(static field => field.Descriptor).ToArray();
        var descriptor = new ProfileDescriptor(
            manifest.Id,
            manifest.Version,
            manifest.DisplayName,
            manifest.CandidateKind,
            manifest.PathInput,
            Array.AsReadOnly(descriptors),
            Array.AsReadOnly(manifest.Rules
                .Select(static (rule, index) => new ProfileRegexRuleDescriptor(
                    index + 1,
                    rule.Id,
                    rule.Pattern,
                    rule.MatchMode,
                    rule.IgnoreCase,
                    rule.TimeoutMilliseconds))
                .ToArray()));

        var profile = new CompiledProfile(
            descriptor,
            createModel,
            fields.ToArray(),
            rules.ToArray());

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

            if (string.IsNullOrWhiteSpace(attribute.GroupName))
            {
                diagnostics.Add(Error(
                    "capture_group_name_missing",
                    $"정규식 그룹 이름이 필요합니다: {property.Name}"));
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

            var header = string.IsNullOrWhiteSpace(attribute.Header)
                ? property.Name
                : attribute.Header.Trim();
            var descriptor = new ProfileFieldDescriptor(
                attribute.FieldId,
                attribute.GroupName,
                header,
                attribute.Order,
                attribute.Required,
                kind,
                isNullable,
                NormalizeOptional(attribute.ParseFormat),
                NormalizeOptional(attribute.DisplayFormat));

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
                var groupNumbers = new int[fields.Count];

                for (var index = 0; index < fields.Count; index++)
                {
                    var field = fields[index];
                    var groupNumber = regex.GroupNumberFromName(field.Descriptor.GroupName);
                    groupNumbers[index] = groupNumber;
                    if (groupNumber < 0 && field.Descriptor.Required)
                    {
                        diagnostics.Add(Error(
                            "required_group_not_defined",
                            $"규칙 '{sourceRule.Id}'에 필수 그룹 '{field.Descriptor.GroupName}'이(가) 없습니다."));
                    }
                }

                rules.Add(new CompiledRegexRule(
                    sourceRule.Id,
                    sourceRule.MatchMode,
                    regex,
                    groupNumbers));
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
    int[] GroupNumbers);
