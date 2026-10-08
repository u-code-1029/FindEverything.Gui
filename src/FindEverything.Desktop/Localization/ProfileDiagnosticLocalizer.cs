using System.Text.RegularExpressions;
using FindEverything.Application.Profiles;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.Localization;

/// <summary>
/// Keeps compiler/runtime diagnostic codes useful in English even when an
/// underlying profile component still emits a Korean diagnostic message.
/// Known authoring diagnostics have dedicated resources; unknown plug-in
/// diagnostics retain their stable code and receive a clear English fallback.
/// </summary>
public static partial class ProfileDiagnosticLocalizer
{
    public static string Translate(
        IAppLocalizer? localizer,
        ProfileDiagnostic diagnostic) =>
        Translate(localizer, diagnostic.Code, diagnostic.Message, isTemplate: false);

    public static string Translate(
        IAppLocalizer? localizer,
        ProfileMappingIssue issue) =>
        Translate(localizer, issue.Code, issue.Message, isTemplate: false);

    public static string Translate(
        IAppLocalizer? localizer,
        ProfilePathTemplateDiagnostic diagnostic) =>
        Translate(localizer, diagnostic.Code, diagnostic.Message, isTemplate: true);

    private static string Translate(
        IAppLocalizer? localizer,
        string code,
        string sourceMessage,
        bool isTemplate)
    {
        if (localizer is null
            || !string.Equals(
                localizer.Culture.Name,
                "en-US",
                StringComparison.OrdinalIgnoreCase))
        {
            return sourceMessage;
        }

        var translated = localizer.Get(
            $"Loc.ProfileDiagnostic.{code}",
            string.Empty);
        if (string.IsNullOrWhiteSpace(translated))
        {
            translated = localizer.Format(
                isTemplate
                    ? "Loc.ProfileDiagnostic.TemplateFallback"
                    : "Loc.ProfileDiagnostic.Fallback",
                isTemplate
                    ? "Path-template issue ({0}). Review field IDs, placeholders, and optional segments."
                    : "Profile issue ({0}). Review the related field or rule.",
                code);
        }

        var references = QuotedReferencePattern()
            .Matches(sourceMessage)
            .Select(static match => match.Groups[1].Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        return references.Length == 0
            ? translated
            : $"{translated} [{string.Join(", ", references)}]";
    }

    [GeneratedRegex("['‘]([^'’]+)['’]", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedReferencePattern();
}
