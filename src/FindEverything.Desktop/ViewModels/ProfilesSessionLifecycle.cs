using FindEverything.Application.Profiles;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace FindEverything.Desktop.ViewModels;

public partial class ProfilesViewModel
{
    private bool _isActive;
    private bool _disposed;
    private bool _profileSnapshotPending;
    private bool _previewPending;
    private bool _editableProfilesLoaded;
    private Task _previewTask = Task.CompletedTask;
    private Task? _activationTask;
    private CancellationToken _activationToken;
    private long _editableProfilesVersion;

    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _isActive = true;
        if (_profileSnapshotPending)
        {
            _profileSnapshotPending = false;
            ApplySnapshot(_profileCatalog.Current, SelectedProfile?.Id);
        }
        if (_previewPending) ScheduleTemplatePreview();
        if (_editableProfilesLoaded) return Task.CompletedTask;
        if (_activationTask is { IsCompleted: false } && !_activationToken.IsCancellationRequested)
            return _activationTask;
        _activationToken = cancellationToken;
        return _activationTask = LoadEditableProfilesOnActivationAsync(cancellationToken);
    }

    private async Task LoadEditableProfilesOnActivationAsync(CancellationToken cancellationToken)
    {
        await RefreshEditableProfilesAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
    }

    public void Deactivate()
    {
        _isActive = false;
        _editorSession.CancelPreview();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _editorSession.Dispose();
        _profileCatalog.Changed -= OnProfileCatalogChanged;
        DetachRows();
    }

    internal Task PendingPreview => _previewTask;
    internal long DraftRevision => _editorSession.Revision;

    private void ScheduleTemplatePreview()
    {
        if (_disposed || _isPopulatingDraft) return;
        _previewPending = true;
        if (!_isActive) return;
        var token = _editorSession.ReplacePreview();
        _previewTask = UpdateTemplatePreviewAsync(token);
    }

    private async Task UpdateTemplatePreviewAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token).ConfigureAwait(false);
            var snapshot = await _uiScheduler.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                var rule = DraftRules.Count == 1 ? DraftRules[0] : null;
                var eligible = rule is not null && !string.IsNullOrWhiteSpace(rule.PathTemplate)
                    && Enum.IsDefined(rule.MatchMode) && rule.IgnoreCase
                    && rule.TimeoutMilliseconds == 100
                    && DraftFields.All(static field => field.SupportsGuidedAssignments)
                    && TryResolveGuidedTerminalField(rule, out _);
                return Task.FromResult(new ProfilePreviewSnapshot(IsGuidedMode,
                    IsGuidedMode ? DraftPathTemplate : rule?.PathTemplate ?? string.Empty,
                    BuildFieldManifests(), eligible, rule?.Pattern));
            }).ConfigureAwait(false);
            var result = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return _pathTemplateCompiler.Compile(snapshot.Template, snapshot.Fields);
            }, token).ConfigureAwait(false);
            await _uiScheduler.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || _disposed) return Task.FromResult(false);
                _previewPending = false;
                if (snapshot.Guided) ApplyTemplatePreview(result);
                else CanUseGuidedMode = snapshot.GuidedEligible && result.IsValid
                    && string.Equals(result.Pattern, snapshot.ExpectedPattern, StringComparison.Ordinal);
                return Task.FromResult(true);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not update the profile preview.");
            // Keep the editable draft intact. Explicit Validate/Save produces actionable diagnostics.
        }
    }

    private void ApplyTemplatePreview(ProfilePathTemplateCompileResult result)
    {
        if (!result.IsValid || string.IsNullOrEmpty(result.Pattern))
        {
            GeneratedPatternPreview = string.Empty;
            ApplyTemplateErrors(result);
            return;
        }
        GeneratedPatternPreview = result.Pattern;
        TemplateStatusMessage = L("Loc.Profiles.Status.TemplateReady",
            "경로 규칙이 준비되었습니다. 아래에서 같은 경로를 시험해 보세요.");
        TemplateStatusSeverity = InfoBarSeverity.Success;
    }

    private sealed record ProfilePreviewSnapshot(bool Guided, string Template,
        IReadOnlyList<ProfileFieldManifest> Fields, bool GuidedEligible, string? ExpectedPattern);
}
