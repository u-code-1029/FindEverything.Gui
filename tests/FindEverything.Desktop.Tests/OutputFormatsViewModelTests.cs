using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class OutputFormatsViewModelTests
{
    [Fact]
    public async Task Cancelled_new_format_keeps_the_unsaved_template()
    {
        var vm = Create(DraftLeaveDecision.Cancel);
        await vm.NewFormatCommand.ExecuteAsync(null);
        vm.DraftTemplate = "unsaved {Name}";
        var id = vm.SelectedFormat;

        await vm.NewFormatCommand.ExecuteAsync(null);

        Assert.Equal("unsaved {Name}", vm.DraftTemplate);
        Assert.Same(id, vm.SelectedFormat);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Cancelled_format_switch_restores_selection_and_unsaved_template()
    {
        var vm = Create(DraftLeaveDecision.Cancel);
        await vm.NewFormatCommand.ExecuteAsync(null);
        vm.DraftTemplate = "unsaved {Name}";

        vm.SelectedFormat = vm.Formats.First();

        Assert.Null(vm.SelectedFormat);
        Assert.Equal("unsaved {Name}", vm.DraftTemplate);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Preview_uses_two_examples_and_the_actual_formatter_and_separator()
    {
        var vm = Create(DraftLeaveDecision.Discard);
        await vm.NewFormatCommand.ExecuteAsync(null);
        vm.DraftTemplate = "{Name}";
        vm.DraftItemSeparator = ", ";

        Assert.True(vm.IsPreviewValid);
        Assert.Equal("Project-1, Project-2", vm.PreviewText);

        vm.DraftTemplate = "{Field:missing}";
        Assert.False(vm.IsPreviewValid);
        Assert.Empty(vm.PreviewText);
        Assert.NotEmpty(vm.PreviewValidationMessage);
    }

    [Fact]
    public async Task Invalid_template_cannot_be_saved_by_a_leave_confirmation()
    {
        var vm = Create(DraftLeaveDecision.Save);
        await vm.NewFormatCommand.ExecuteAsync(null);
        vm.DraftTemplate = "{unknown}";

        await vm.NewFormatCommand.ExecuteAsync(null);

        Assert.Equal("{unknown}", vm.DraftTemplate);
        Assert.True(vm.HasUnsavedChanges);
    }

    private static OutputFormatsViewModel Create(DraftLeaveDecision decision) => new(
        new MemoryFormatStore(), new SelectionOutputFormatter(), new EmptyProfileCatalog(),
        new SettingsViewModelTests.NoopSnackbar(), null!, new TestAppLocalizer(),
        NullLogger<OutputFormatsViewModel>.Instance,
        new DraftProtectionService(new FixedPrompt(decision), ImmediateUiScheduler.Instance));

    private sealed class FixedPrompt(DraftLeaveDecision decision) : IDraftDecisionPrompt
    {
        public Task<DraftLeaveDecision> ChooseAsync(string draftTitle) => Task.FromResult(decision);
    }

    private sealed class EmptyProfileCatalog : IProfileCatalog
    {
        public ProfileCatalogSnapshot Current => ProfileCatalogSnapshot.Empty;
        public event EventHandler<ProfileCatalogChangedEventArgs>? Changed { add { } remove { } }
    }

    private sealed class MemoryFormatStore : ISelectionOutputFormatStore
    {
        private readonly List<SelectionOutputFormatDefinition> _formats = [SelectionOutputFormatDefaults.FullPathLines];
        public event EventHandler? Changed;
        public IReadOnlyList<SelectionOutputFormatDefinition> GetAll() => _formats.ToArray();
        public IReadOnlyList<SelectionOutputFormatDefinition> GetApplicable(string? profileId) => GetAll();
        public void Save(SelectionOutputFormatDefinition definition)
        {
            _formats.RemoveAll(format => format.Id == definition.Id);
            _formats.Add(definition);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        public bool Delete(string id) => _formats.RemoveAll(format => format.Id == id) > 0;
    }
}
