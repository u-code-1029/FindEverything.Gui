using System.Text.Json;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Localization;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.Services;

public interface IProfileWorkspaceSettingsService
{
    void Validate(ProfileManifest manifest);
    Task<ProfileManifest> CreateExportSnapshotAsync(ProfileManifest manifest, CancellationToken cancellationToken = default);
    Task<ProfileWorkspaceInstallation?> InstallAsync(ProfileManifest manifest, CancellationToken cancellationToken = default);
}

/// <summary>Restores only settings still equal to this installation; never overwrites a later edit.</summary>
public sealed class ProfileWorkspaceInstallation(Func<Task<bool>> rollback)
{
    public Task<bool> RollbackAsync() => rollback();
}

/// <summary>
/// The exported JSON is a complete desktop-settings snapshot. Live preferences remain in their
/// existing stores, so resizing a column never recompiles a profile or invalidates parsed results.
/// </summary>
public sealed class ProfileWorkspaceSettingsService(AppPaths paths, ISelectionOutputFormatStore formats,
    IProfilePresentationStore presentations, IGridLayoutStore layouts, IProfileOutputPreferenceStore preferences,
    ISelectionOutputFormatter formatter, IAppLocalizer? localizer = null) : IProfileWorkspaceSettingsService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ProfileManifest> CreateExportSnapshotAsync(ProfileManifest manifest, CancellationToken cancellationToken = default)
    {
        var copy = Clone(manifest);
        // External/in-memory manifests have no local settings to overwrite their embedded snapshot.
        if (string.IsNullOrWhiteSpace(copy.Id) || !File.Exists(Path.Combine(paths.UserProfilesDirectory, copy.Id, "profile.json")))
        {
            Validate(copy);
            return copy;
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await preferences.FlushAsync(cancellationToken).ConfigureAwait(false);
            var selected = preferences.Get(copy.Id);
            var applicable = formats.GetApplicable(copy.Id);
            var owned = applicable.Where(f => !f.IsBuiltIn && string.Equals(f.ProfileId, copy.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            // A selected common format is a dependency, not permission to export every common format.
            if (applicable.FirstOrDefault(f => string.Equals(f.Id, selected, StringComparison.OrdinalIgnoreCase)) is { IsBuiltIn: false } dependency
                && !owned.Any(f => string.Equals(f.Id, dependency.Id, StringComparison.OrdinalIgnoreCase))) owned.Add(dependency);
            var preset = await presentations.LoadAsync(copy.Id, cancellationToken).ConfigureAwait(false);
            var layout = layouts.Get(copy.Id);
            var fields = FieldIds(copy);
            fields.Add("$ui:path"); fields.Add("$ui:status");
            var columns = new List<ProfileColumnPresentationManifest>();
            var layoutIndex = 0;
            foreach (var id in preset.Keys.Concat(layout.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(fields.Contains).OrderBy(id => layout.TryGetValue(id, out var value) ? value.DisplayIndex : int.MaxValue).ThenBy(id => id, StringComparer.Ordinal))
            {
                layout.TryGetValue(id, out var value);
                var manual = value?.WidthMode == GridColumnWidthMode.Manual && double.IsFinite(value.Width) && value.Width is >= 24 and <= 16_384;
                columns.Add(new()
                {
                    FieldId = id, WidthPreset = (ProfileColumnWidthPreset)preset.GetValueOrDefault(id),
                    DisplayIndex = value is null ? null : layoutIndex++, IsHidden = value?.IsHidden ?? false,
                    WidthMode = manual ? ProfileColumnWidthMode.Manual : ProfileColumnWidthMode.Auto,
                    Width = manual ? value!.Width : null,
                });
            }
            copy.Workspace = new()
            {
                OutputFormats = owned.Select(f => new ProfileOutputFormatManifest
                { Id = f.Id, DisplayName = f.DisplayName, Template = f.Template, ItemSeparator = f.ItemSeparator, Prefix = f.Prefix, Suffix = f.Suffix }).ToList(),
                SelectedOutputFormatId = owned.Any(f => string.Equals(f.Id, selected, StringComparison.OrdinalIgnoreCase))
                    ? selected : SelectionOutputFormatDefaults.FullPathLinesId,
                Columns = columns,
            };
            Validate(copy);
            return copy;
        }
        finally { _gate.Release(); }
    }

    public async Task<ProfileWorkspaceInstallation?> InstallAsync(ProfileManifest manifest, CancellationToken cancellationToken = default)
    {
        Validate(manifest);
        if (manifest.Workspace is not { } workspace) return null;
        var id = manifest.Id ?? throw new InvalidDataException("A profile identifier is required.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await preferences.FlushAsync(cancellationToken).ConfigureAwait(false);
            var oldFormats = formats.GetAll().Where(f => !f.IsBuiltIn && string.Equals(f.ProfileId, id, StringComparison.OrdinalIgnoreCase)).ToArray();
            var oldPresentation = await presentations.LoadAsync(id, cancellationToken).ConfigureAwait(false);
            var oldLayout = layouts.Get(id);
            var oldPreference = preferences.Get(id);
            var idMap = (workspace.OutputFormats ?? []).ToDictionary(f => f.Id!, _ => "portable." + Guid.NewGuid().ToString("N"), StringComparer.OrdinalIgnoreCase);
            var newFormats = (workspace.OutputFormats ?? []).Select(f => new SelectionOutputFormatDefinition(idMap[f.Id!],
                f.DisplayName!, f.Template!, f.ItemSeparator, id, f.Prefix, f.Suffix)).ToArray();
            var newPresentation = (workspace.Columns ?? []).Where(c => !c.FieldId!.StartsWith("$ui:", StringComparison.Ordinal))
                .ToDictionary(c => c.FieldId!, c => (ColumnWidthPreset)c.WidthPreset, StringComparer.OrdinalIgnoreCase);
            var newLayout = (workspace.Columns ?? []).Where(c => c.DisplayIndex is not null).Select(c => new GridColumnLayout(c.FieldId!,
                c.DisplayIndex!.Value, c.Width ?? 100, (GridColumnWidthMode)c.WidthMode, c.IsHidden)).ToArray();
            var newLayoutSnapshot = newLayout.ToDictionary(c => c.FieldId, StringComparer.OrdinalIgnoreCase);
            var newPreference = workspace.SelectedOutputFormatId is { } selected && idMap.TryGetValue(selected, out var mapped)
                ? mapped : SelectionOutputFormatDefaults.FullPathLinesId;
            var wroteFormats = false; var wrotePresentation = false; var wroteLayout = false; var wrotePreference = false;
            async Task<bool> Rollback()
            {
                var failures = new List<Exception>();
                var restored = true;
                async Task Undo(Func<Task<bool>> action)
                { try { restored &= await action().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); } }
                if (wroteLayout) await Undo(() => layouts.TryReplaceAsync(id, newLayoutSnapshot, oldLayout.Values, CancellationToken.None)).ConfigureAwait(false);
                if (wrotePresentation) await Undo(() => presentations.TryReplaceAsync(id, newPresentation, oldPresentation, CancellationToken.None, resetManualWidths: false)).ConfigureAwait(false);
                if (wrotePreference) await Undo(() => preferences.TryReplaceAsync(id, newPreference, oldPreference, CancellationToken.None)).ConfigureAwait(false);
                if (wroteFormats) await Undo(() => formats.TryReplaceProfileFormatsAsync(id, newFormats, oldFormats, CancellationToken.None)).ConfigureAwait(false);
                if (failures.Count > 0) throw new AggregateException("Some imported profile settings could not be restored.", failures);
                return restored;
            }
            try
            {
                // Publish formats last, after their selected-ID dependency has been committed.
                if (!await preferences.TryReplaceAsync(id, oldPreference, newPreference, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("The selected output format changed while importing the profile.");
                wrotePreference = true;
                if (!await layouts.TryReplaceAsync(id, oldLayout, newLayout, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Column layout changed while importing the profile.");
                wroteLayout = true;
                if (!await presentations.TryReplaceAsync(id, oldPresentation, newPresentation, cancellationToken, resetManualWidths: false).ConfigureAwait(false))
                    throw new InvalidOperationException("Column presets changed while importing the profile.");
                wrotePresentation = true;
                if (!await formats.TryReplaceProfileFormatsAsync(id, oldFormats, newFormats, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("Output formats changed while importing the profile. Retry after reviewing those changes.");
                wroteFormats = true;
                return new ProfileWorkspaceInstallation(Rollback);
            }
            catch (Exception failure)
            {
                bool restored;
                try { restored = await Rollback().ConfigureAwait(false); }
                catch (Exception rollback) { throw new AggregateException("Profile settings import failed and rollback was incomplete.", failure, rollback); }
                if (!restored) throw new InvalidOperationException("Profile settings changed during rollback. The later changes were preserved.", failure);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public void Validate(ProfileManifest manifest)
    {
        var issues = ProfileWorkspaceManifestValidator.Validate(manifest);
        if (issues.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, issues.Select(i => ProfileDiagnosticLocalizer.Translate(localizer, i))));
        var fields = FieldIds(manifest);
        foreach (var output in manifest.Workspace?.OutputFormats ?? [])
        {
            var validation = formatter.ValidateFormat(new(output.Id!, output.DisplayName!, output.Template!, output.ItemSeparator,
                manifest.Id, output.Prefix, output.Suffix), fields);
            if (!validation.IsValid) throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors));
        }
    }

    private static HashSet<string> FieldIds(ProfileManifest manifest) => (manifest.Fields ?? []).Select(f => f.FieldId!)
        .Concat((manifest.TextFileFields ?? []).Select(f => f.FieldId!)).Concat((manifest.UserInputFields ?? []).Select(f => f.FieldId!))
        .Concat((manifest.ChildDirectoryFields ?? []).Select(f => f.FieldId!)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static ProfileManifest Clone(ProfileManifest manifest) => JsonSerializer.Deserialize<ProfileManifest>(
        JsonSerializer.SerializeToUtf8Bytes(manifest, ProfileTransferService.WriteOptions), ProfileTransferService.ReadOptions)!;
}
