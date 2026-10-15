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
    public async Task Whole_result_affixes_update_the_preview_persist_and_are_protected_as_drafts()
    {
        var store = new MemoryFormatStore();
        var vm = Create(DraftLeaveDecision.Cancel, store: store);
        await vm.NewFormatCommand.ExecuteAsync(null);
        vm.DraftTemplate = "{Name}";
        vm.DraftItemSeparator = ", ";
        vm.DraftPrefix = "[\\n";
        vm.DraftSuffix = "\\n]";

        Assert.Equal("[\nProject-1, Project-2\n]", vm.PreviewText);
        await vm.NewFormatCommand.ExecuteAsync(null);
        Assert.Equal("[\\n", vm.DraftPrefix);
        Assert.Equal("\\n]", vm.DraftSuffix);
        Assert.True(vm.HasUnsavedChanges);

        Assert.True(await vm.SaveChangesAsync());
        var saved = Assert.Single(store.GetAll(), format => !format.IsBuiltIn);
        Assert.Equal("[\n", saved.Prefix);
        Assert.Equal("\n]", saved.Suffix);
        Assert.Equal("[\\n", vm.DraftPrefix);
        Assert.Equal("\\n]", vm.DraftSuffix);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task New_structured_format_shows_the_selected_profiles_column_buttons()
    {
        var vm = Create(DraftLeaveDecision.Discard, new StructuredProfileCatalog());
        await vm.NewFormatCommand.ExecuteAsync(null);

        Assert.Equal("projects", vm.SelectedProfileChoice?.ProfileId);
        Assert.True(vm.HasProfileTokens);
        Assert.Equal(4, vm.CommonTokenChoices.Count);
        var token = Assert.Single(vm.ProfileTokenChoices);
        Assert.Equal("Client name", token.DisplayName);
        Assert.Equal("{Field:Client}", token.Token);

        vm.DraftTemplate = "before replace after";
        var caret = vm.InsertTokenAtSelection(token, 7, 7);
        Assert.Equal("before {Field:Client} after", vm.DraftTemplate);
        Assert.Equal(7 + token.Token.Length, caret);
        Assert.True(vm.IsPreviewValid);

        vm.SelectedProfileChoice = vm.ProfileChoices.First(static choice => choice.IsCommon);
        Assert.Empty(vm.ProfileTokenChoices);
        Assert.False(vm.IsPreviewValid);
    }

    [Fact]
    public void Built_in_format_is_not_changed_by_token_insertion()
    {
        var vm = Create(DraftLeaveDecision.Discard);

        vm.InsertTokenAtSelection(vm.CommonTokenChoices.First(), 0, vm.DraftTemplate.Length);

        Assert.Equal("{FullPath}", vm.DraftTemplate);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Oversized_affix_blocks_preview_and_save()
    {
        var store = new MemoryFormatStore();
        var vm = Create(DraftLeaveDecision.Discard, store: store);
        await vm.NewFormatCommand.ExecuteAsync(null);
        vm.DraftSuffix = new string('x', SelectionOutputFormatDefaults.MaximumAffixCharacters + 1);

        Assert.False(vm.IsPreviewValid);
        Assert.NotEmpty(vm.PreviewValidationMessage);
        Assert.False(await vm.SaveChangesAsync());
        Assert.True(vm.HasUnsavedChanges);
        Assert.Single(store.GetAll());
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

    [Fact]
    public async Task Duplicating_a_built_in_format_preserves_every_output_option_under_a_new_id()
    {
        var original = SelectionOutputFormatDefaults.FullPathLines with
        {
            Template = "{Name}: {Field:Client} ({FullPath})",
            ItemSeparator = "\t|\r\n",
            Prefix = "[\r\n",
            Suffix = "\n]\\end",
            ProfileId = "projects"
        };
        var store = new MemoryFormatStore();
        store.Save(original);
        using var vm = Create(DraftLeaveDecision.Discard, new StructuredProfileCatalog(), store);

        Assert.True(vm.IsBuiltInPreview);
        Assert.True(vm.DuplicateFormatCommand.CanExecute(null));
        vm.DuplicateFormatCommand.Execute(null);

        Assert.Null(vm.SelectedFormat);
        Assert.True(vm.CanEdit);
        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(original.Template, vm.DraftTemplate);
        Assert.Equal("\\t|\\r\\n", vm.DraftItemSeparator);
        Assert.Equal("[\\r\\n", vm.DraftPrefix);
        Assert.Equal("\\n]\\\\end", vm.DraftSuffix);
        Assert.Equal(original.ProfileId, vm.SelectedProfileChoice?.ProfileId);
        Assert.False(vm.DuplicateFormatCommand.CanExecute(null));

        Assert.True(await vm.SaveChangesAsync());
        var saved = Assert.Single(store.GetAll(), format => !format.IsBuiltIn);
        Assert.NotEqual(original.Id, saved.Id);
        Assert.StartsWith("format.", saved.Id);
        Assert.NotEqual(original.DisplayName, saved.DisplayName);
        Assert.Equal(original.Template, saved.Template);
        Assert.Equal(original.ItemSeparator, saved.ItemSeparator);
        Assert.Equal(original.Prefix, saved.Prefix);
        Assert.Equal(original.Suffix, saved.Suffix);
        Assert.Equal(original.ProfileId, saved.ProfileId);
        Assert.Equal(original, Assert.Single(store.GetAll(), format => format.IsBuiltIn));
        Assert.False(vm.HasUnsavedChanges);
        Assert.True(vm.DuplicateFormatCommand.CanExecute(null));
    }

    [Fact]
    public async Task Preview_uses_a_snapshot_of_the_recent_test_and_can_switch_back_to_examples()
    {
        var samples = new OutputPreviewSampleStore();
        var fields = new Dictionary<string, object?> { ["Client"] = "Contoso" };
        samples.Set("projects", SelectionOutputItem.FromPath(@"D:\Actual\Release-42", fields: fields));
        fields["Client"] = "Changed after test";
        using var vm = Create(DraftLeaveDecision.Discard, new StructuredProfileCatalog(), samples: samples);
        await vm.NewFormatCommand.ExecuteAsync(null);
        vm.DraftTemplate = "{Name}|{Field:Client}";
        vm.DraftItemSeparator = ";";

        Assert.True(vm.HasTestSample);
        Assert.True(vm.IsPreviewValid);
        Assert.Equal("Release-42|Contoso", vm.PreviewText);
        Assert.Contains("최근 프로필 테스트", vm.PreviewSourceDescription);

        vm.UseTestSample = false;
        Assert.Equal("Project-1|Client name 1;Project-2|Client name 2", vm.PreviewText);
        Assert.Contains("예제 2개", vm.PreviewSourceDescription);
        vm.UseTestSample = true;
        Assert.Equal("Release-42|Contoso", vm.PreviewText);
    }

    [Fact]
    public async Task A_recent_test_from_another_profile_is_not_used_for_structured_tokens()
    {
        var samples = new OutputPreviewSampleStore();
        samples.Set("projects", SelectionOutputItem.FromPath(@"D:\Actual\Release-42",
            fields: new Dictionary<string, object?> { ["Client"] = "Contoso" }));
        using var vm = Create(DraftLeaveDecision.Discard,
            new StructuredProfileCatalog("projects", "other"), samples: samples);
        await vm.NewFormatCommand.ExecuteAsync(null);
        vm.DraftTemplate = "{Name}|{Field:Client}";
        vm.DraftItemSeparator = ";";
        vm.SelectedProfileChoice = vm.ProfileChoices.Single(choice => choice.ProfileId == "other");

        Assert.False(vm.HasTestSample);
        Assert.True(vm.UseTestSample);
        Assert.Equal("Project-1|Client name 1;Project-2|Client name 2", vm.PreviewText);
        Assert.Contains("예제 2개", vm.PreviewSourceDescription);

        vm.SelectedProfileChoice = vm.ProfileChoices.Single(choice => choice.ProfileId == "projects");
        Assert.True(vm.HasTestSample);
        Assert.Equal("Release-42|Contoso", vm.PreviewText);
    }

    [Fact]
    public async Task Common_formats_can_preview_a_recent_test_path_without_selecting_its_profile()
    {
        var samples = new OutputPreviewSampleStore();
        samples.Set("projects", SelectionOutputItem.FromPath(@"D:\Actual\Release-42"));
        using var vm = Create(DraftLeaveDecision.Discard, samples: samples);
        await vm.ActivateAsync();

        Assert.True(vm.SelectedProfileChoice?.IsCommon);
        Assert.True(vm.HasTestSample);
        Assert.Equal(@"D:\Actual\Release-42", vm.PreviewText);
    }

    [Fact]
    public async Task Hidden_page_defers_recent_sample_rendering_until_activation()
    {
        var samples = new OutputPreviewSampleStore();
        using var vm = Create(DraftLeaveDecision.Discard, samples: samples);
        var examples = vm.PreviewText;

        samples.Set("projects", SelectionOutputItem.FromPath(@"D:\Actual\first"));
        Assert.Equal(examples, vm.PreviewText);
        await vm.ActivateAsync();
        Assert.Equal(@"D:\Actual\first", vm.PreviewText);
        samples.Set("projects", SelectionOutputItem.FromPath(@"D:\Actual\second"));
        Assert.Equal(@"D:\Actual\second", vm.PreviewText);

        vm.Deactivate();
        samples.Set("projects", SelectionOutputItem.FromPath(@"D:\Actual\latest"));
        Assert.Equal(@"D:\Actual\second", vm.PreviewText);
        await vm.ActivateAsync();
        Assert.Equal(@"D:\Actual\latest", vm.PreviewText);
    }

    [Fact]
    public async Task Huge_repeated_test_values_are_bounded_without_invalidating_the_preview()
    {
        var samples = new OutputPreviewSampleStore();
        samples.Set("projects", SelectionOutputItem.FromPath(@"D:\Actual\large",
            fields: new Dictionary<string, object?> { ["Client"] = new string('x', 100_000) }));
        using var vm = Create(DraftLeaveDecision.Discard, new StructuredProfileCatalog(), samples: samples);
        await vm.NewFormatCommand.ExecuteAsync(null);
        // Full output would exceed even the 32 Mi-character clipboard limit.
        vm.DraftTemplate = string.Concat(Enumerable.Repeat("{Field:Client}", 500));

        Assert.True(vm.IsPreviewValid);
        Assert.Equal(new string('x', SelectionOutputPreview.MaximumCharacters) + Environment.NewLine + "…", vm.PreviewText);
        Assert.Contains("16,384", vm.PreviewValidationMessage);
    }

    private static OutputFormatsViewModel Create(DraftLeaveDecision decision,
        IProfileCatalog? profiles = null, MemoryFormatStore? store = null,
        OutputPreviewSampleStore? samples = null) => new(
        store ?? new MemoryFormatStore(), new SelectionOutputFormatter(), profiles ?? new EmptyProfileCatalog(),
        new SettingsViewModelTests.NoopSnackbar(), null!, new TestAppLocalizer(),
        NullLogger<OutputFormatsViewModel>.Instance,
        new DraftProtectionService(new FixedPrompt(decision), ImmediateUiScheduler.Instance), previewSamples: samples);

    private sealed class FixedPrompt(DraftLeaveDecision decision) : IDraftDecisionPrompt
    {
        public Task<DraftLeaveDecision> ChooseAsync(string draftTitle) => Task.FromResult(decision);
    }

    private sealed class EmptyProfileCatalog : IProfileCatalog
    {
        public ProfileCatalogSnapshot Current => ProfileCatalogSnapshot.Empty;
        public event EventHandler<ProfileCatalogChangedEventArgs>? Changed { add { } remove { } }
    }

    private sealed class StructuredProfileCatalog(params string[] profileIds) : IProfileCatalog
    {
        public ProfileCatalogSnapshot Current { get; } = new(
            (profileIds.Length == 0 ? new[] { "projects" } : profileIds)
                .Select(id => (ILoadedProfile)new StructuredProfile(id)).ToArray(), [], DateTimeOffset.UtcNow);
        public event EventHandler<ProfileCatalogChangedEventArgs>? Changed { add { } remove { } }
    }

    private sealed class StructuredProfile(string id) : ILoadedProfile
    {
        public ProfileDescriptor Descriptor { get; } = new(id, "1", "Projects", ProfileCandidateKind.Directory,
            [new ProfileFieldDescriptor("Client", "client", "Client name", 0, true,
                ProfileFieldValueKind.String, false, null, null)], []);
        public ProfileMapResult Map(ProfilePathCandidate candidate) => ProfileMapResult.NoMatch();
        public ProfileDirectoryNameExclusionResult EvaluateDirectoryName(string directoryName) =>
            ProfileDirectoryNameExclusionResult.NotExcluded();
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
