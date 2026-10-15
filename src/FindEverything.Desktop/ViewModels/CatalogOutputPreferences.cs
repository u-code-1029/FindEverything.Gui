using FindEverything.Desktop.Configuration;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public partial class CatalogViewModel
{
    private readonly IProfileOutputPreferenceStore? _outputPreferences;
    private readonly Dictionary<string, PendingOutputPreference> _pendingOutputPreferences = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshingOutputFormats;
    private long _outputPreferenceRevision;
    private sealed record PendingOutputPreference(string FormatId, long Revision);

    partial void OnSelectedOutputFormatChanged(SelectionOutputFormatDefinition? value)
    {
        if (_refreshingOutputFormats || _disposed || _outputPreferences is null
            || IsReadOnlyDataSource || SelectedProfile?.Id is not { } profileId || value is null) return;
        var pending = new PendingOutputPreference(value.Id, ++_outputPreferenceRevision);
        _pendingOutputPreferences[profileId] = pending;
        _ = SaveOutputPreferenceAsync(profileId, pending);
    }

    private async Task SaveOutputPreferenceAsync(string profileId, PendingOutputPreference pending)
    {
        try { await _outputPreferences!.SaveAsync(profileId, pending.FormatId); }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not save the copy format for profile {ProfileId}.", profileId);
            if (!_disposed) SetStatus(L("Loc.Ux.Output.Preference.SaveFailed", "복사 포맷 선택을 저장하지 못했습니다."),
                exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (_pendingOutputPreferences.GetValueOrDefault(profileId)?.Revision == pending.Revision)
                _pendingOutputPreferences.Remove(profileId);
        }
    }
}
