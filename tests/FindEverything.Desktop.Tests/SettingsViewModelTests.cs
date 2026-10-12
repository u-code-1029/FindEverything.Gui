using FindEverything.Application.Options;
using FindEverything.Desktop.Appearance;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Wpf.Ui;
using Wpf.Ui.Controls;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class SettingsViewModelTests
{
    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("1.5")]
    public async Task Invalid_numeric_text_cannot_save_a_previous_numeric_value(string text)
    {
        var writer = new RecordingWriter();
        var vm = Create(writer);
        vm.SearchPageSizeText = text;

        Assert.True(vm.HasNumericValidationError);
        Assert.False(vm.SaveCommand.CanExecute(null));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Null(writer.LastUpdate);
        Assert.NotEmpty(vm.NumericValidationMessage);
    }

    [Fact]
    public async Task Valid_numeric_drafts_are_saved_and_inputs_are_disabled_until_completion()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new RecordingWriter { Completion = completion.Task };
        var vm = Create(writer);
        vm.SearchPageSizeText = "250";
        vm.MaxEntriesPerSecondText = "4500";
        vm.DirectoryDelayMillisecondsText = "0";
        vm.UiUpdateBatchSizeText = "25";
        vm.NotificationAutoDismissSecondsText = "8";

        var save = vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsSaving);
        Assert.False(vm.IsEditable);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Equal(250, writer.LastUpdate!.Indexing!.SearchPageSize);
        Assert.Equal(4500, writer.LastUpdate.Indexing.MaxEntriesPerSecond);
        Assert.Equal(0, writer.LastUpdate.Indexing.DirectoryDelayMilliseconds);
        Assert.Equal(25, writer.LastUpdate.Indexing.UiUpdateBatchSize);
        Assert.Equal(8, writer.LastUpdate.Notifications!.AutoDismissSeconds);

        completion.SetResult();
        await save;
        Assert.False(vm.IsSaving);
        Assert.True(vm.IsEditable);
        Assert.Equal(InfoBarSeverity.Success, vm.StatusSeverity);
    }

    [Fact]
    public void Scan_and_notification_drafts_start_with_the_requested_defaults()
    {
        var vm = Create(new RecordingWriter());
        Assert.Equal("10", vm.UiUpdateBatchSizeText);
        Assert.Equal("3", vm.NotificationAutoDismissSecondsText);
        Assert.True(vm.IsSaveAvailable);
    }

    [Theory]
    [InlineData("", "3")]
    [InlineData("0", "3")]
    [InlineData("1001", "3")]
    [InlineData("abc", "3")]
    [InlineData("10", "")]
    [InlineData("10", "0")]
    [InlineData("10", "61")]
    [InlineData("10", "1.5")]
    public async Task Invalid_scan_batch_or_notification_duration_cannot_save_a_previous_value(string batch, string duration)
    {
        var writer = new RecordingWriter();
        var vm = Create(writer);
        vm.UiUpdateBatchSizeText = batch;
        vm.NotificationAutoDismissSecondsText = duration;
        Assert.True(vm.HasNumericValidationError);
        Assert.False(vm.SaveCommand.CanExecute(null));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Null(writer.LastUpdate);
    }

    [Fact]
    public void External_reload_updates_unchanged_fields_and_preserves_local_edits()
    {
        var state = new MutableState<IndexingOptions>(new());
        var diagnostics = new ReloadDiagnostics();
        var vm = Create(new RecordingWriter(), state, diagnostics);
        vm.SearchPageSizeText = "125";
        vm.UiUpdateBatchSizeText = "30";
        state.Current = new IndexingOptions
        {
            SearchPageSize = 300,
            MaxEntriesPerSecond = 6000,
            DirectoryDelayMilliseconds = 12,
            UiUpdateBatchSize = 15,
        };

        diagnostics.Publish();

        Assert.Equal("125", vm.SearchPageSizeText);
        Assert.Equal("6000", vm.MaxEntriesPerSecondText);
        Assert.Equal("12", vm.DirectoryDelayMillisecondsText);
        Assert.Equal("30", vm.UiUpdateBatchSizeText);
    }

    private static SettingsViewModel Create(
        RecordingWriter writer,
        MutableState<IndexingOptions>? indexing = null,
        ReloadDiagnostics? diagnostics = null) => new(
            indexing ?? new MutableState<IndexingOptions>(new()),
            new MutableState<AppearanceOptions>(new()),
            writer, new NoopAppearance(), new NoopSnackbar(), null!,
            diagnostics ?? new ReloadDiagnostics(), new TestAppLocalizer(),
            new AppPaths("unused", "unused-settings.json"),
            NullLogger<SettingsViewModel>.Instance, ImmediateUiScheduler.Instance);

    private sealed class MutableState<T>(T current) : IValidatedSettingsState<T> where T : class
    {
        public T Current { get; set; } = current;
    }

    private sealed class RecordingWriter : IUserSettingsWriter
    {
        public UserSettingsUpdate? LastUpdate { get; private set; }
        public Task Completion { get; init; } = Task.CompletedTask;
        public Task SaveAsync(UserSettingsUpdate update, CancellationToken cancellationToken = default)
        {
            LastUpdate = update;
            return Completion;
        }
    }

    private sealed class ReloadDiagnostics : ISettingsReloadDiagnostics
    {
        public string? LastError => null;
        public event EventHandler? Changed;
        public void Publish() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class NoopAppearance : IAppearanceService
    {
        public void Attach(FluentWindow window) { }
        public void Apply(FluentWindow window, AppearanceOptions options) { }
        public void ApplyToAttachedWindow(AppearanceOptions options) { }
    }

    internal sealed class NoopSnackbar : ISnackbarService
    {
        public TimeSpan DefaultTimeOut { get; set; }
        public void SetSnackbarPresenter(SnackbarPresenter contentPresenter) { }
        public SnackbarPresenter? GetSnackbarPresenter() => null;
        public void Show(string title, string message, ControlAppearance appearance, IconElement? icon, TimeSpan timeout) { }
    }
}
