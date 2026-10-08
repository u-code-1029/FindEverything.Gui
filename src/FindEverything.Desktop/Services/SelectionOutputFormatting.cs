using System.Globalization;
using System.Text;

namespace FindEverything.Desktop.Services;

public sealed record SelectionOutputItem(
    string FullPath,
    string FolderPath,
    string Name,
    IReadOnlyDictionary<string, object?> Fields)
{
    public static SelectionOutputItem FromPath(
        string fullPath,
        string? folderPath = null,
        IReadOnlyDictionary<string, object?>? fields = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        return new SelectionOutputItem(
            fullPath,
            folderPath ?? fullPath,
            GetLeafName(fullPath),
            fields ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
    }

    private static string GetLeafName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var separatorIndex = Math.Max(
            trimmed.LastIndexOf('\\'),
            trimmed.LastIndexOf('/'));
        return separatorIndex >= 0 ? trimmed[(separatorIndex + 1)..] : trimmed;
    }
}

public sealed record SelectionOutputTemplateValidation(
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public interface ISelectionOutputFormatter
{
    SelectionOutputTemplateValidation ValidateTemplate(
        string template,
        IEnumerable<string>? allowedFieldIds = null);

    string Format(
        Configuration.SelectionOutputFormatDefinition format,
        IEnumerable<SelectionOutputItem> items);
}

public sealed class SelectionOutputFormatter : ISelectionOutputFormatter
{
    private const int MaximumTemplateCharacters = 16_384;
    private const int MaximumOutputCharacters = 32 * 1024 * 1024;

    public SelectionOutputTemplateValidation ValidateTemplate(
        string template,
        IEnumerable<string>? allowedFieldIds = null)
    {
        var errors = new List<string>();
        template ??= string.Empty;
        if (template.Length > MaximumTemplateCharacters)
        {
            errors.Add("출력 템플릿은 16,384자 이하여야 합니다.");
            return new SelectionOutputTemplateValidation(errors.AsReadOnly());
        }

        var segments = Parse(template, errors);
        if (segments.Count == 0 && errors.Count == 0)
        {
            errors.Add("출력 템플릿을 입력하세요.");
        }

        if (allowedFieldIds is not null)
        {
            var allowed = new HashSet<string>(allowedFieldIds, StringComparer.OrdinalIgnoreCase);
            foreach (var fieldId in segments
                         .Where(static segment => segment.Kind == SegmentKind.Field)
                         .Select(static segment => segment.Value)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!allowed.Contains(fieldId))
                {
                    errors.Add($"선택한 프로필에 '{fieldId}' 필드가 없습니다.");
                }
            }
        }

        return new SelectionOutputTemplateValidation(errors.AsReadOnly());
    }

    public string Format(
        Configuration.SelectionOutputFormatDefinition format,
        IEnumerable<SelectionOutputItem> items)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(items);

        var template = format.Template
            ?? throw new FormatException("출력 템플릿을 입력하세요.");
        if (template.Length > MaximumTemplateCharacters)
        {
            throw new FormatException("출력 템플릿은 16,384자 이하여야 합니다.");
        }

        var errors = new List<string>();
        var segments = Parse(template, errors);
        if (segments.Count == 0 && errors.Count == 0)
        {
            errors.Add("출력 템플릿을 입력하세요.");
        }

        if (errors.Count > 0)
        {
            throw new FormatException(string.Join(Environment.NewLine, errors));
        }

        var builder = new StringBuilder();
        var itemIndex = 0;
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(item.Fields);
            if (itemIndex > 0)
            {
                AppendChecked(builder, format.ItemSeparator ?? string.Empty);
            }

            foreach (var segment in segments)
            {
                var value = segment.Kind switch
                {
                    SegmentKind.Literal => segment.Value,
                    SegmentKind.FullPath => item.FullPath,
                    SegmentKind.FolderPath => item.FolderPath,
                    SegmentKind.Name => item.Name,
                    SegmentKind.Field => FormatField(item.Fields, segment.Value),
                    _ => throw new InvalidOperationException("지원되지 않는 출력 템플릿 조각입니다."),
                };
                AppendChecked(builder, value ?? string.Empty);
            }

            itemIndex++;
        }

        return builder.ToString();
    }

    private static IReadOnlyList<TemplateSegment> Parse(
        string template,
        ICollection<string> errors)
    {
        var segments = new List<TemplateSegment>();
        var literal = new StringBuilder();
        var index = 0;
        while (index < template.Length)
        {
            var current = template[index];
            if (current == '{' && index + 1 < template.Length && template[index + 1] == '{')
            {
                literal.Append('{');
                index += 2;
                continue;
            }

            if (current == '}' && index + 1 < template.Length && template[index + 1] == '}')
            {
                literal.Append('}');
                index += 2;
                continue;
            }

            if (current == '}')
            {
                errors.Add($"템플릿의 {index + 1}번째 문자에 닫는 중괄호가 남아 있습니다.");
                index++;
                continue;
            }

            if (current != '{')
            {
                literal.Append(current);
                index++;
                continue;
            }

            FlushLiteral(segments, literal);
            var closingIndex = template.IndexOf('}', index + 1);
            if (closingIndex < 0)
            {
                errors.Add($"템플릿의 {index + 1}번째 문자에서 열린 중괄호가 닫히지 않았습니다.");
                break;
            }

            var token = template[(index + 1)..closingIndex].Trim();
            if (token.Equals("FullPath", StringComparison.OrdinalIgnoreCase))
            {
                segments.Add(new TemplateSegment(SegmentKind.FullPath, token));
            }
            else if (token.Equals("FolderPath", StringComparison.OrdinalIgnoreCase))
            {
                segments.Add(new TemplateSegment(SegmentKind.FolderPath, token));
            }
            else if (token.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                segments.Add(new TemplateSegment(SegmentKind.Name, token));
            }
            else if (token.StartsWith("Field:", StringComparison.OrdinalIgnoreCase))
            {
                var fieldId = token["Field:".Length..].Trim();
                if (fieldId.Length == 0)
                {
                    errors.Add("{Field:필드ID} 토큰에 필드 ID를 입력하세요.");
                }
                else
                {
                    segments.Add(new TemplateSegment(SegmentKind.Field, fieldId));
                }
            }
            else
            {
                errors.Add($"지원하지 않는 토큰입니다: {{{token}}}");
            }

            index = closingIndex + 1;
        }

        FlushLiteral(segments, literal);
        return segments;
    }

    private static void FlushLiteral(
        ICollection<TemplateSegment> segments,
        StringBuilder literal)
    {
        if (literal.Length == 0)
        {
            return;
        }

        segments.Add(new TemplateSegment(SegmentKind.Literal, literal.ToString()));
        literal.Clear();
    }

    private static string FormatField(
        IReadOnlyDictionary<string, object?> fields,
        string fieldId)
    {
        if (!fields.TryGetValue(fieldId, out var value))
        {
            var pair = fields.FirstOrDefault(
                pair => string.Equals(pair.Key, fieldId, StringComparison.OrdinalIgnoreCase));
            value = pair.Equals(default(KeyValuePair<string, object?>)) ? null : pair.Value;
        }

        return value switch
        {
            null => string.Empty,
            IFormattable formattable =>
                formattable.ToString(format: null, CultureInfo.CurrentCulture) ?? string.Empty,
            _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty,
        };
    }

    private static void AppendChecked(StringBuilder builder, string value)
    {
        if (value.Length > MaximumOutputCharacters - builder.Length)
        {
            throw new InvalidOperationException(
                "선택 항목의 출력 결과가 약 3,200만 자 제한을 초과했습니다. 항목 수나 템플릿을 줄여 주세요.");
        }

        builder.Append(value);
    }

    private enum SegmentKind
    {
        Literal,
        FullPath,
        FolderPath,
        Name,
        Field,
    }

    private sealed record TemplateSegment(SegmentKind Kind, string Value);
}

public interface IClipboardService
{
    bool TrySetText(string text, out string? errorMessage);
}

public sealed class WpfClipboardService : IClipboardService
{
    public bool TrySetText(string text, out string? errorMessage)
    {
        if (string.IsNullOrEmpty(text))
        {
            errorMessage = "복사할 내용이 없습니다.";
            return false;
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
            errorMessage = null;
            return true;
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException
                or InvalidOperationException
                or System.Threading.ThreadStateException)
        {
            errorMessage = "클립보드를 다른 프로그램이 사용 중입니다. 잠시 후 다시 시도해 주세요.";
            return false;
        }
    }
}
