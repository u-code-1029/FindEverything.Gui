using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using FindEverything.Application.Options;

namespace FindEverything.Desktop.Localization;

public interface IAppLocalizer
{
    CultureInfo Culture { get; }

    string Get(string key, string koreanFallback);

    string Format(string key, string koreanFallback, params object?[] arguments);
}

public sealed class AppLocalizer : IAppLocalizer
{
    private readonly CultureInfo _culture;
    private readonly IReadOnlyDictionary<string, string> _messages;
    public AppLocalizer(IValidatedSettingsState<LocalizationOptions> localizationSettings)
    {
        _culture = CultureInfo.GetCultureInfo(LocalizationOptions.Normalize(localizationSettings.Current.CultureName));
        var messages = new Dictionary<string, string>(StringComparer.Ordinal);
        if (System.Windows.Application.Current is { } application)
        {
            void Collect(ResourceDictionary dictionary)
            {
                foreach (var merged in dictionary.MergedDictionaries) Collect(merged);
                foreach (var key in dictionary.Keys.OfType<string>())
                    if (dictionary[key] is string value) messages[key] = value;
            }
            Collect(application.Resources);
        }
        // Localization changes restart the app. A frozen snapshot also allows
        // page formatting and exports without accessing WPF resources on workers.
        _messages = messages;
    }
    public CultureInfo Culture => _culture;
    public string Get(string key, string koreanFallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(koreanFallback);
        return _messages.TryGetValue(key, out var value) ? value : koreanFallback;
    }
    public string Format(string key, string koreanFallback, params object?[] arguments) =>
        string.Format(Culture, Get(key, koreanFallback), arguments);
}

public static class LocalizationBootstrapper
{
    private const string DictionaryPrefix = "/FindEverything.Gui;component/Localization/Strings.";

    public static CultureInfo Apply(
        System.Windows.Application application,
        LocalizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(options);

        var cultureName = LocalizationOptions.Normalize(options.CultureName);
        var culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        var dictionaries = application.Resources.MergedDictionaries;
        for (var index = dictionaries.Count - 1; index >= 0; index--)
        {
            var source = dictionaries[index].Source?.OriginalString;
            if (source?.StartsWith(DictionaryPrefix, StringComparison.OrdinalIgnoreCase) == true
                || source?.StartsWith("Localization/Strings.", StringComparison.OrdinalIgnoreCase) == true)
            {
                dictionaries.RemoveAt(index);
            }
        }

        dictionaries.Add(new ResourceDictionary
        {
            Source = CreateDictionaryUri(cultureName),
        });
        application.Resources["Loc.Culture.XmlLanguage"] =
            XmlLanguage.GetLanguage(culture.IetfLanguageTag);
        return culture;
    }

    internal static Uri CreateDictionaryUri(string cultureName) =>
        new(
            $"{DictionaryPrefix}{LocalizationOptions.Normalize(cultureName)}.xaml",
            UriKind.Relative);
}
