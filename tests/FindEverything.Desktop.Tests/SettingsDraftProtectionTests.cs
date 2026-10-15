using System.IO;
using FindEverything.Application.Options;
using FindEverything.Desktop.Appearance;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Hosting;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Wpf.Ui.Controls;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class SettingsDraftProtectionTests
{
    [Theory]
    [InlineData(nameof(SettingsViewModel.SearchPageSizeText), "250")]
    [InlineData(nameof(SettingsViewModel.MaxEntriesPerSecondText), "4000")]
    [InlineData(nameof(SettingsViewModel.DirectoryDelayMillisecondsText), "17")]
    [InlineData(nameof(SettingsViewModel.UiUpdateBatchSizeText), "25")]
    [InlineData(nameof(SettingsViewModel.NotificationAutoDismissSecondsText), "12")]
    public void Every_numeric_editor_tracks_changes_and_semantic_reverts(string propertyName, string changed)
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        var notifications = 0;
        vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(vm.HasUnsavedChanges)) notifications++; };
        var property = typeof(SettingsViewModel).GetProperty(propertyName)!;
        var saved = (string)property.GetValue(vm)!;

        Assert.False(vm.HasUnsavedChanges);
        property.SetValue(vm, changed);
        Assert.True(vm.HasUnsavedChanges);
        property.SetValue(vm, "invalid");
        Assert.True(vm.HasUnsavedChanges);
        property.SetValue(vm, " 00" + saved + " ");
        Assert.False(vm.HasUnsavedChanges);
        Assert.True(notifications >= 3);
    }

    [Fact]
    public void Every_appearance_choice_tracks_changes_and_reverts()
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        vm.SelectedTheme = vm.ThemeChoices.Single(choice => choice.Value == ThemePreference.Dark);
        Assert.True(vm.HasUnsavedChanges);
        vm.SelectedTheme = vm.ThemeChoices.Single(choice => choice.Value == ThemePreference.System);
        Assert.False(vm.HasUnsavedChanges);
        vm.SelectedBackdrop = vm.BackdropChoices.Single(choice => choice.Value == BackdropPreference.None);
        Assert.True(vm.HasUnsavedChanges);
        vm.SelectedBackdrop = vm.BackdropChoices.Single(choice => choice.Value == BackdropPreference.Auto);
        Assert.False(vm.HasUnsavedChanges);
        vm.SelectedCatalogRowDensity = vm.CatalogRowDensityChoices.Single(choice => choice.Value == CatalogRowDensity.Wide);
        Assert.True(vm.HasUnsavedChanges);
        vm.SelectedCatalogRowDensity = vm.CatalogRowDensityChoices.Single(choice => choice.Value == CatalogRowDensity.Normal);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Registered_settings_cancel_app_exit_without_changing_the_draft()
    {
        using var fixture = new Fixture(DraftLeaveDecision.Cancel);
        fixture.ViewModel.UiUpdateBatchSizeText = "25";

        Assert.False(await fixture.Protection.ConfirmAllAsync());
        Assert.Equal("설정", Assert.Single(fixture.Prompt.Titles));
        Assert.Equal("25", fixture.ViewModel.UiUpdateBatchSizeText);
        Assert.True(fixture.ViewModel.HasUnsavedChanges);
        Assert.Empty(fixture.Writer.Updates);
    }

    [Fact]
    public async Task Shared_save_confirmation_normalizes_saved_values_without_writing_workspace_or_locale()
    {
        using var fixture = new Fixture(DraftLeaveDecision.Save);
        var vm = fixture.ViewModel;
        vm.UiUpdateBatchSizeText = "0025";
        vm.SelectedTheme = vm.ThemeChoices.Single(choice => choice.Value == ThemePreference.Dark);

        Assert.True(await fixture.Protection.ConfirmAllAsync());

        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal("25", vm.UiUpdateBatchSizeText);
        Assert.Equal(25, vm.UiUpdateBatchSize);
        var update = Assert.Single(fixture.Writer.Updates);
        Assert.Null(update.Workspace);
        Assert.Null(update.Localization);
        Assert.Equal(25, update.Indexing!.UiUpdateBatchSize);
        Assert.Equal(ThemePreference.Dark, update.Appearance!.Theme);
    }

    [Fact]
    public async Task Invalid_or_failed_save_keeps_the_draft_and_blocks_leave()
    {
        using var fixture = new Fixture(DraftLeaveDecision.Save);
        var vm = fixture.ViewModel;
        vm.SearchPageSizeText = "not a number";
        Assert.False(await vm.SaveChangesAsync());
        Assert.False(await fixture.Protection.ConfirmAllAsync());
        Assert.True(vm.HasUnsavedChanges);
        Assert.Empty(fixture.Writer.Updates);

        vm.SearchPageSizeText = "250";
        fixture.Writer.Completion = Task.FromException(new IOException("Read-only settings"));
        Assert.False(await fixture.Protection.ConfirmAllAsync());
        Assert.Equal("250", vm.SearchPageSizeText);
        Assert.True(vm.HasUnsavedChanges);
        Assert.False(vm.IsSaving);
        Assert.Equal(InfoBarSeverity.Error, vm.StatusSeverity);
    }

    [Fact]
    public void Reload_preserves_edits_then_discard_restores_latest_valid_settings_without_a_write()
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        vm.SearchPageSizeText = "unfinished";
        vm.SelectedTheme = vm.ThemeChoices.Single(choice => choice.Value == ThemePreference.Dark);
        fixture.Indexing.Current = new() { SearchPageSize = 300, MaxEntriesPerSecond = 6000, DirectoryDelayMilliseconds = 11, UiUpdateBatchSize = 20 };
        fixture.Appearance.Current = new() { Theme = ThemePreference.Light, Backdrop = BackdropPreference.None, CatalogRowDensity = CatalogRowDensity.Wide };
        fixture.Notifications.Current = new() { AutoDismissSeconds = 8 };
        fixture.Diagnostics.Publish();

        Assert.Equal("unfinished", vm.SearchPageSizeText);
        Assert.Equal(ThemePreference.Dark, vm.SelectedTheme.Value);
        Assert.Equal("6000", vm.MaxEntriesPerSecondText);
        Assert.Equal("8", vm.NotificationAutoDismissSecondsText);
        Assert.True(vm.HasUnsavedChanges);
        vm.DiscardChanges();

        Assert.Equal("300", vm.SearchPageSizeText);
        Assert.Equal(ThemePreference.Light, vm.SelectedTheme.Value);
        Assert.Equal(BackdropPreference.None, vm.SelectedBackdrop.Value);
        Assert.Equal(CatalogRowDensity.Wide, vm.SelectedCatalogRowDensity.Value);
        Assert.False(vm.HasNumericValidationError);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Empty(fixture.Writer.Updates);
    }

    [Fact]
    public async Task An_in_flight_save_blocks_leave_without_prompting_and_does_not_swallow_newer_edits()
    {
        using var fixture = new Fixture(DraftLeaveDecision.Discard);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Writer.Completion = completion.Task;
        var vm = fixture.ViewModel;
        vm.UiUpdateBatchSizeText = "20";
        var save = vm.SaveChangesAsync();
        Assert.True(vm.IsSaving);
        Assert.False(vm.CanLeaveDraft);
        Assert.False(await vm.SaveChangesAsync());
        Assert.False(await fixture.Protection.ConfirmAllAsync());
        Assert.Empty(fixture.Prompt.Titles);

        vm.UiUpdateBatchSizeText = "30";
        fixture.Diagnostics.Publish(); // The writer has not published its saved snapshot yet.
        completion.SetResult();
        Assert.False(await save);
        Assert.Equal("30", vm.UiUpdateBatchSizeText);
        Assert.Equal(20, fixture.Indexing.Current.UiUpdateBatchSize);
        Assert.True(vm.HasUnsavedChanges);
        Assert.True(vm.CanLeaveDraft);
        Assert.True(await fixture.Protection.ConfirmAllAsync());
        Assert.Equal("20", vm.UiUpdateBatchSizeText);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Theory]
    [InlineData(DraftLeaveDecision.Cancel, false)]
    [InlineData(DraftLeaveDecision.Save, false)]
    [InlineData(DraftLeaveDecision.Discard, true)]
    public async Task Language_restart_uses_the_same_settings_draft_protection(DraftLeaveDecision decision, bool expectedRestart)
    {
        using var fixture = new Fixture(decision);
        fixture.ViewModel.UiUpdateBatchSizeText = "invalid";
        var restart = new RecordingRestart();
        var languages = new LanguageSelectionService(new TestAppLocalizer(), fixture.Writer, restart,
            fixture.Protection, new RestartNowPrompt(), scheduler: ImmediateUiScheduler.Instance);

        Assert.Equal(expectedRestart, await languages.ChangeAsync("en-US"));
        Assert.Equal(expectedRestart, restart.IsRestartRequested);
        if (expectedRestart)
        {
            Assert.False(fixture.ViewModel.HasUnsavedChanges);
            var update = Assert.Single(fixture.Writer.Updates);
            Assert.Equal("en-US", update.Localization?.CultureName);
            Assert.Null(update.Workspace);
            Assert.Null(update.Indexing);
        }
        else
        {
            Assert.True(fixture.ViewModel.HasUnsavedChanges);
            Assert.Empty(fixture.Writer.Updates);
        }
    }

    [Fact]
    public async Task Disposed_editor_unsubscribes_from_reload_and_cannot_save()
    {
        using var fixture = new Fixture();
        var previous = fixture.ViewModel.UiUpdateBatchSizeText;
        fixture.ViewModel.Dispose();
        fixture.Indexing.Current = new() { UiUpdateBatchSize = 80 };
        fixture.Diagnostics.Publish();
        Assert.Equal(previous, fixture.ViewModel.UiUpdateBatchSizeText);
        Assert.False(await fixture.ViewModel.SaveChangesAsync());
        Assert.Empty(fixture.Writer.Updates);
    }

    private sealed class Fixture : IDisposable
    {
        public MutableState<IndexingOptions> Indexing { get; } = new(new());
        public MutableState<AppearanceOptions> Appearance { get; } = new(new());
        public MutableState<NotificationOptions> Notifications { get; } = new(new());
        public Diagnostics Diagnostics { get; } = new();
        public RecordingPrompt Prompt { get; }
        public DraftProtectionService Protection { get; }
        public PublishingWriter Writer { get; }
        public SettingsViewModel ViewModel { get; }
        public Fixture(DraftLeaveDecision decision = DraftLeaveDecision.Cancel)
        {
            Prompt = new(decision);
            Protection = new(Prompt, ImmediateUiScheduler.Instance);
            Writer = new(Indexing, Appearance, Notifications);
            ViewModel = new(Indexing, Appearance, Writer, new NoopAppearance(), new SettingsViewModelTests.NoopSnackbar(), null!,
                Diagnostics, new TestAppLocalizer(), new AppPaths("unused", "unused-settings.json"),
                NullLogger<SettingsViewModel>.Instance, ImmediateUiScheduler.Instance, Notifications, Protection);
        }
        public void Dispose() => ViewModel.Dispose();
    }
    private sealed class MutableState<T>(T current) : IValidatedSettingsState<T> where T : class
    {
        public T Current { get; set; } = current;
    }
    private sealed class PublishingWriter(MutableState<IndexingOptions> indexing,
        MutableState<AppearanceOptions> appearance, MutableState<NotificationOptions> notifications) : IUserSettingsWriter
    {
        public List<UserSettingsUpdate> Updates { get; } = [];
        public Task Completion { get; set; } = Task.CompletedTask;
        public async Task SaveAsync(UserSettingsUpdate update, CancellationToken cancellationToken = default)
        {
            Updates.Add(update);
            await Completion.ConfigureAwait(false);
            if (update.Indexing is { } savedIndexing) indexing.Current = savedIndexing;
            if (update.Appearance is { } savedAppearance) appearance.Current = savedAppearance;
            if (update.Notifications is { } savedNotifications) notifications.Current = savedNotifications;
        }
    }
    private sealed class Diagnostics : ISettingsReloadDiagnostics
    {
        public string? LastError => null;
        public event EventHandler? Changed;
        public void Publish() => Changed?.Invoke(this, EventArgs.Empty);
    }
    private sealed class RecordingPrompt(DraftLeaveDecision decision) : IDraftDecisionPrompt
    {
        public List<string> Titles { get; } = [];
        public Task<DraftLeaveDecision> ChooseAsync(string draftTitle) { Titles.Add(draftTitle); return Task.FromResult(decision); }
    }
    private sealed class NoopAppearance : IAppearanceService
    {
        public void Attach(FluentWindow window) { }
        public void Apply(FluentWindow window, AppearanceOptions options) { }
        public void ApplyToAttachedWindow(AppearanceOptions options) { }
    }
    private sealed class RestartNowPrompt : ILanguageRestartPrompt
    {
        public Task<LanguageRestartDecision> ChooseAsync(string cultureName, bool operationRunning, CancellationToken cancellationToken) =>
            Task.FromResult(LanguageRestartDecision.RestartNow);
    }
    private sealed class RecordingRestart : IApplicationRestartCoordinator
    {
        public bool IsRestartRequested { get; private set; }
        public void RequestRestart() => IsRestartRequested = true;
        public void RestartAfterShutdownIfRequested() { }
    }
}
