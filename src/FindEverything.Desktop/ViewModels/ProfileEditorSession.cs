using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection;

namespace FindEverything.Desktop.ViewModels;

/// <summary>Owns draft data independently of the currently realized editor step.</summary>
internal sealed class ProfileEditorSession : IDisposable
{
    private CancellationTokenSource? _previewCancellation;
    public long Revision { get; private set; }
    public bool IsPopulating { get; set; }
    public bool HasUserChanges { get; set; }
    public ObservableCollection<ProfileFieldDraftViewModel> Fields { get; } = [];
    public ObservableCollection<ProfileRuleDraftViewModel> Rules { get; } = [];
    public ObservableCollection<ProfileDirectoryNameExclusionRuleDraftViewModel> Exclusions { get; } = [];
    public ObservableCollection<ProfileTextFileFieldDraftViewModel> TextFiles { get; } = [];
    public ObservableCollection<ProfileUserInputFieldDraftViewModel> UserInputs { get; } = [];
    public ObservableCollection<ProfileChildDirectoryFieldDraftViewModel> ChildDirectories { get; } = [];
    public ObservableCollection<ProfileSelectionRuleDraftViewModel> SelectionRules { get; } = [];

    public void Changed() { Revision++; HasUserChanges = true; }

    public CancellationToken ReplacePreview()
    {
        CancelPreview();
        _previewCancellation = new CancellationTokenSource();
        return _previewCancellation.Token;
    }

    public void CancelPreview()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
    }

    public void Dispose() => CancelPreview();
}

/// <summary>Derived display notifications must never be interpreted as a new edit.</summary>
internal static class ProfileDraftChanges
{
    private static readonly ConcurrentDictionary<Type, HashSet<string>> InputProperties = new();

    public static bool IsInput(object? row, string? property)
    {
        if (row is null || property is null) return false;
        if (property is nameof(ProfileFieldDraftViewModel.ValueMappings)
            or nameof(ProfileSelectionRuleDraftViewModel.Columns)) return true;
        // These typed values mirror the editable text properties, which emit their own notification.
        if (property is "TimeoutMilliseconds" or "MaxBytes" or "Order") return false;
        return InputProperties.GetOrAdd(row.GetType(), static type => type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(static item => item.SetMethod is not null)
                .Select(static item => item.Name).ToHashSet(StringComparer.Ordinal))
            .Contains(property);
    }

    public static bool AffectsNumericValidation(string? property) => property is
        "TimeoutMillisecondsText" or "MaxBytesText" or "OrderText" or "Columns";

    public static bool AffectsPath(string? property) => property is
        "FieldId" or "GroupName" or "GroupNamesText" or "Kind" or "Required"
        or "ParseFormat" or "DateSourcePreset" or "IsTerminalField"
        or "Pattern" or "PathTemplate" or "MatchMode" or "IgnoreCase"
        or "TimeoutMillisecondsText" or "StopTraversalGroupsText";
}
