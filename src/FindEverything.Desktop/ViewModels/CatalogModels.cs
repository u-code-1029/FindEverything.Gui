using FindEverything.Application.Catalog;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.ViewModels;

public sealed class ProfileChoiceViewModel(ILoadedProfile profile)
{
    public ILoadedProfile Profile { get; } = profile;

    public string Id => Profile.Descriptor.Id;

    public string DisplayName => Profile.Descriptor.DisplayName;

    public string Version => Profile.Descriptor.Version;

    public override string ToString() => DisplayName;
}

public sealed class CatalogItemViewModel(CatalogItem item)
{
    public string FullPath { get; } = item.FullPath;

    public string RelativePath { get; } = item.RelativePath;

    public string MatchedRuleId { get; } = item.MatchedRuleId;

    public bool CoveragePending { get; } = item.CoveragePending;

    public string CoverageText => CoveragePending ? "대기 범위" : "완료";

    public IReadOnlyDictionary<string, object?> Values { get; } = item.Values;

    public object? this[string fieldId] =>
        Values.TryGetValue(fieldId, out var value) ? value : null;
}
