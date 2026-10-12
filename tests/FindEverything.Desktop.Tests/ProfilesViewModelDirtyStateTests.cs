using System.IO;
using FindEverything.Application.Profiles;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Abstractions;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ProfilesViewModelDirtyStateTests
{
    [Fact]
    public async Task Analyzing_the_same_canonical_path_keeps_guided_assignments()
    {
        var vm = CreateFreshDraft(DraftLeaveDecision.Cancel);
        var template = vm.DraftPathTemplate;
        var assignments = vm.DraftFields[0].GuidedAssignments.ToArray();

        await vm.BuildTemplateFromSampleCommand.ExecuteAsync(null);

        Assert.Equal(template, vm.DraftPathTemplate);
        Assert.Equal(assignments, vm.DraftFields[0].GuidedAssignments.ToArray());
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Cancelled_new_profile_preserves_user_changes()
    {
        var vm = CreateFreshDraft(DraftLeaveDecision.Cancel);
        var id = vm.DraftId;
        vm.DraftDisplayName = "My unfinished profile";

        await vm.NewDraftCommand.ExecuteAsync(null);

        Assert.Equal(id, vm.DraftId);
        Assert.Equal("My unfinished profile", vm.DraftDisplayName);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Invalid_numeric_row_blocks_validation_save_and_save_on_leave()
    {
        var vm = CreateFreshDraft(DraftLeaveDecision.Save);
        vm.DraftRules[0].TimeoutMillisecondsText = "bad-input";

        Assert.True(vm.HasNumericValidationError);
        Assert.False(vm.ValidateDraftCommand.CanExecute(null));
        Assert.False(vm.SaveDraftCommand.CanExecute(null));
        Assert.False(await vm.SaveChangesAsync());
        Assert.Equal("bad-input", vm.DraftRules[0].TimeoutMillisecondsText);
        Assert.True(vm.HasUnsavedChanges);
    }

    private static ProfilesViewModel CreateFreshDraft(DraftLeaveDecision decision) => new(
        new StubProfileCatalog(ProfileCatalogSnapshot.Empty),
        new StubProfileAuthoringService(new ProfileManifest()),
        new ProfilePathTemplateCompiler(), new FixedPathCanonicalizer(@"C:\Example"),
        null!, null!, null!, null!, null!, new TestAppLocalizer(),
        NullLogger<ProfilesViewModel>.Instance,
        new DraftProtectionService(new FixedPrompt(decision), ImmediateUiScheduler.Instance));

    private sealed class FixedPrompt(DraftLeaveDecision decision) : IDraftDecisionPrompt
    {
        public Task<DraftLeaveDecision> ChooseAsync(string draftTitle) => Task.FromResult(decision);
    }

    [Fact]
    public async Task Analyze_path_marks_a_loaded_profile_dirty_once_and_disables_playground()
    {
        const string pathTemplate = "{name@name}";
        const string canonicalPath = @"C:\Profiles\Apollo";
        var field = new ProfileFieldManifest
        {
            FieldId = "name",
            GroupName = "name",
            Header = "Name",
            Order = 10,
            Required = true,
            Kind = ProfileFieldValueKind.String,
        };
        var templateCompiler = new ProfilePathTemplateCompiler();
        var compileResult = templateCompiler.Compile(pathTemplate, [field]);
        Assert.True(compileResult.IsValid);
        var manifest = new ProfileManifest
        {
            ContractVersion = ProfileContract.CurrentMajor,
            Kind = ProfileKind.Declarative,
            Id = "saved-profile",
            Version = "1.0.0",
            DisplayName = "Saved profile",
            CandidateKind = ProfileCandidateKind.Directory,
            Fields = [field],
            Rules =
            [
                new ProfileRegexRuleManifest
                {
                    Id = "default",
                    PathTemplate = pathTemplate,
                    Pattern = compileResult.Pattern,
                    MatchMode = ProfileRegexMatchMode.Full,
                    IgnoreCase = true,
                    TimeoutMilliseconds = 100,
                },
            ],
        };
        var editableProfile = new EditableProfileSummary(
            "saved-profile",
            "Saved profile",
            "saved-profile.json");
        var appliedSnapshot = new ProfileCatalogSnapshot(
            [new StubLoadedProfile("saved-profile")],
            [],
            DateTimeOffset.UtcNow);
        var catalog = new StubProfileCatalog(appliedSnapshot);
        var viewModel = new ProfilesViewModel(
            catalog,
            new StubProfileAuthoringService(manifest),
            templateCompiler,
            new FixedPathCanonicalizer(canonicalPath),
            null!,
            null!,
            null!,
            null!,
            null!,
            new TestAppLocalizer(),
            NullLogger<ProfilesViewModel>.Instance)
        {
            SelectedEditableProfile = editableProfile,
        };

        await viewModel.LoadDraftCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsDraftDirty);
        Assert.True(viewModel.CanOpenSavedProfileInPlayground);
        Assert.True(viewModel.OpenSavedProfileInPlaygroundCommand.CanExecute(null));
        Assert.Equal(pathTemplate, viewModel.DraftPathTemplate);

        catalog.Publish(ProfileCatalogSnapshot.Empty);
        Assert.False(viewModel.CanOpenSavedProfileInPlayground);
        Assert.False(viewModel.OpenSavedProfileInPlaygroundCommand.CanExecute(null));
        catalog.Publish(appliedSnapshot);
        Assert.True(viewModel.CanOpenSavedProfileInPlayground);
        Assert.True(viewModel.OpenSavedProfileInPlaygroundCommand.CanExecute(null));

        viewModel.SamplePath = "mapped-input";
        var dirtyNotifications = 0;
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(ProfilesViewModel.IsDraftDirty))
            {
                dirtyNotifications++;
            }
        };

        await viewModel.BuildTemplateFromSampleCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsDraftDirty);
        Assert.False(viewModel.CanOpenSavedProfileInPlayground);
        Assert.False(viewModel.OpenSavedProfileInPlaygroundCommand.CanExecute(null));
        Assert.Equal(1, dirtyNotifications);
        Assert.NotEqual(pathTemplate, viewModel.DraftPathTemplate);
    }

    private sealed class FixedPathCanonicalizer(string canonicalPath)
        : IProfilePathCanonicalizer
    {
        public string Canonicalize(string path) => canonicalPath;
    }

    private sealed class StubProfileCatalog(ProfileCatalogSnapshot current) : IProfileCatalog
    {
        public ProfileCatalogSnapshot Current { get; private set; } = current;

        public event EventHandler<ProfileCatalogChangedEventArgs>? Changed;

        public void Publish(ProfileCatalogSnapshot snapshot)
        {
            var previous = Current;
            Current = snapshot;
            Changed?.Invoke(this, new ProfileCatalogChangedEventArgs(previous, snapshot));
        }
    }

    private sealed class StubLoadedProfile : ILoadedProfile
    {
        public StubLoadedProfile(string id)
        {
            Descriptor = new ProfileDescriptor(
                id,
                "1.0.0",
                "Saved profile",
                ProfileCandidateKind.Directory,
                [],
                []);
        }

        public ProfileDescriptor Descriptor { get; }

        public ProfileMapResult Map(ProfilePathCandidate candidate) =>
            ProfileMapResult.NoMatch();

        public ProfileDirectoryNameExclusionResult EvaluateDirectoryName(
            string directoryName) =>
            ProfileDirectoryNameExclusionResult.NotExcluded();
    }

    private sealed class StubProfileAuthoringService(ProfileManifest manifest)
        : IProfileAuthoringService
    {
        public string UserProfilesDirectory => Path.GetTempPath();

        public Task<IReadOnlyList<EditableProfileSummary>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EditableProfileSummary>>([]);

        public Task<ProfileManifest?> LoadAsync(
            EditableProfileSummary profile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProfileManifest?>(manifest);

        public ProfileDefinitionReview Validate(ProfileManifest candidate) =>
            throw new NotSupportedException();

        public ProfileDefinitionTestResult Test(ProfileManifest candidate, string samplePath) =>
            throw new NotSupportedException();

        public Task<ProfileSaveResult> SaveAndApplyAsync(
            ProfileManifest candidate,
            string? originalProfileId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ProfileSaveResult>(new NotSupportedException());
    }
}
