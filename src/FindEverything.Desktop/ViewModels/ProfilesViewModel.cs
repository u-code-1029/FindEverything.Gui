using CommunityToolkit.Mvvm.ComponentModel;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.ViewModels;

public partial class ProfilesViewModel : ObservableObject
{
    [ObservableProperty]
    private IReadOnlyList<ProfileSummaryViewModel> _profiles = [];

    [ObservableProperty]
    private ProfileSummaryViewModel? _selectedProfile;

    [ObservableProperty]
    private IReadOnlyList<ProfileFieldDescriptor> _fields = [];

    [ObservableProperty]
    private IReadOnlyList<ProfileRegexRuleDescriptor> _rules = [];

    [ObservableProperty]
    private IReadOnlyList<PluginReportViewModel> _reports = [];

    public ProfilesViewModel(IProfileCatalog profileCatalog)
    {
        var snapshot = profileCatalog.Current;
        Profiles = snapshot.Profiles
            .Select(static profile => new ProfileSummaryViewModel(profile.Descriptor))
            .OrderBy(static profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        Reports = snapshot.Reports
            .Select(static report => new PluginReportViewModel(report))
            .OrderBy(static report => report.Status)
            .ThenBy(static report => report.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        SelectedProfile = Profiles.FirstOrDefault();
    }

    partial void OnSelectedProfileChanged(ProfileSummaryViewModel? value)
    {
        Fields = value?.Descriptor.Fields
            .OrderBy(static field => field.Order)
            .ToArray() ?? [];
        Rules = value?.Descriptor.Rules
            .OrderBy(static rule => rule.Order)
            .ToArray() ?? [];
    }
}

public sealed class ProfileSummaryViewModel(ProfileDescriptor descriptor)
{
    public ProfileDescriptor Descriptor { get; } = descriptor;

    public string Id => Descriptor.Id;

    public string DisplayName => Descriptor.DisplayName;

    public string Version => Descriptor.Version;

    public string CandidateKind => Descriptor.CandidateKind.ToString();

    public string PathInput => Descriptor.PathInput.ToString();

    public int FieldCount => Descriptor.Fields.Count;
}

public sealed class PluginReportViewModel(ProfilePluginReport report)
{
    public string SourceDirectory { get; } = report.SourceDirectory;

    public string ProfileId { get; } = report.ProfileId ?? "—";

    public string DisplayName { get; } = report.DisplayName ?? "알 수 없는 프로필";

    public ProfilePluginStatus Status { get; } = report.Status;

    public string Diagnostics { get; } = report.Diagnostics.Count == 0
        ? "진단 없음"
        : string.Join(
            Environment.NewLine,
            report.Diagnostics.Select(static diagnostic =>
                $"[{diagnostic.Severity}] {diagnostic.Code}: {diagnostic.Message}"
                + (string.IsNullOrWhiteSpace(diagnostic.Detail)
                    ? string.Empty
                    : $" ({diagnostic.Detail})")));
}
