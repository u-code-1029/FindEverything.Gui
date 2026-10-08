using System.IO;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Wpf.Ui.Controls;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ProfilePlaygroundEvaluatorTests
{
    [Fact]
    public void Evaluate_uses_the_loaded_profile_and_returns_display_and_debug_results()
    {
        var canonicalPath = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "FindEverything.Playground.Tests",
            "A"));
        var canonicalizer = new RecordingCanonicalizer(canonicalPath);
        var profile = new RecordingLoadedProfile(canonicalPath);
        var evaluator = new ProfilePlaygroundEvaluator(canonicalizer, new TestAppLocalizer());

        var result = evaluator.Evaluate(profile, "  mapped-drive-input  ");

        Assert.Equal("mapped-drive-input", canonicalizer.Input);
        Assert.Equal(canonicalPath, profile.MappedPath);
        Assert.Equal(canonicalPath, result.CanonicalPath);
        Assert.Equal(InfoBarSeverity.Success, result.Severity);
        Assert.Contains("default", result.Summary, StringComparison.Ordinal);

        var row = Assert.Single(result.Rows);
        Assert.Equal("담당자", row.FieldName);
        Assert.Equal("홍길동", row.Value);
        Assert.Equal("A", row.RawValue);
        Assert.True(row.HasMappedValue);

        var pathDebug = Assert.Single(result.RegexDebugRules);
        Assert.Equal("default", pathDebug.RuleId);
        Assert.Contains(pathDebug.Lanes, static lane => lane.Label == "alias");
        Assert.Equal("A", result.DirectoryExclusionResult.LeafName);
        Assert.Contains("skip-a", result.DirectoryExclusionResult.Title, StringComparison.Ordinal);
        Assert.Single(result.DirectoryExclusionRegexDebugRules);
    }

    [Fact]
    public void Evaluate_reports_no_match_without_fabricating_rows()
    {
        var canonicalPath = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "FindEverything.Playground.Tests",
            "none"));
        var evaluator = new ProfilePlaygroundEvaluator(
            new RecordingCanonicalizer(canonicalPath),
            new TestAppLocalizer());
        var profile = new RecordingLoadedProfile(canonicalPath)
        {
            Result = ProfileMapResult.NoMatch(),
        };

        var result = evaluator.Evaluate(profile, canonicalPath);

        Assert.Empty(result.Rows);
        Assert.Equal(InfoBarSeverity.Warning, result.Severity);
        Assert.Contains("일치하지", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Directory_leaf_helper_treats_a_root_as_having_no_leaf()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));

        Assert.NotNull(root);
        Assert.Equal(
            string.Empty,
            ProfilePlaygroundEvaluator.GetDirectoryLeafName(root!));
    }

    [Fact]
    public void Missing_profile_selection_does_not_fallback_or_navigate()
    {
        var canonicalPath = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "FindEverything.Playground.Tests",
            "A"));
        var profile = new RecordingLoadedProfile(canonicalPath);
        var viewModel = new ProfilePlaygroundViewModel(
            new StubProfileCatalog(new ProfileCatalogSnapshot(
                [profile],
                [],
                DateTimeOffset.UtcNow)),
            new ProfilePlaygroundEvaluator(
                new RecordingCanonicalizer(canonicalPath),
                new TestAppLocalizer()),
            new StubDesktopPickerService(),
            new TestAppLocalizer(),
            NullLogger<ProfilePlaygroundViewModel>.Instance);
        var selectedProfile = Assert.IsType<ProfilePlaygroundProfileViewModel>(
            viewModel.SelectedProfile);

        Assert.False(viewModel.SelectProfile("missing-profile"));
        Assert.Same(selectedProfile, viewModel.SelectedProfile);

        var navigator = new ProfilePlaygroundNavigator(null!, viewModel);
        Assert.False(navigator.Navigate("missing-profile"));
        Assert.Same(selectedProfile, viewModel.SelectedProfile);
    }

    [Fact]
    public async Task View_model_discards_a_completed_evaluation_after_the_input_changes()
    {
        var canonicalPath = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "FindEverything.Playground.Tests",
            "A"));
        var profile = new RecordingLoadedProfile(canonicalPath);
        using var evaluator = new BlockingPlaygroundEvaluator(canonicalPath);
        var viewModel = new ProfilePlaygroundViewModel(
            new StubProfileCatalog(new ProfileCatalogSnapshot(
                [profile],
                [],
                DateTimeOffset.UtcNow)),
            evaluator,
            new StubDesktopPickerService(),
            new TestAppLocalizer(),
            NullLogger<ProfilePlaygroundViewModel>.Instance)
        {
            InputPath = "first path",
        };

        var evaluationTask = viewModel.EvaluateCommand.ExecuteAsync(null);
        await evaluator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.IsReady);
        try
        {
            viewModel.InputPath = "second path";
        }
        finally
        {
            evaluator.Release();
        }

        await evaluationTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.IsReady);
        Assert.Empty(viewModel.Rows);
        Assert.Contains("변경", viewModel.Summary, StringComparison.Ordinal);
    }

    private sealed class RecordingCanonicalizer(string canonicalPath)
        : IProfilePathCanonicalizer
    {
        public string? Input { get; private set; }

        public string Canonicalize(string path)
        {
            Input = path;
            return canonicalPath;
        }
    }

    private sealed class RecordingLoadedProfile : ILoadedProfile
    {
        private readonly string _expectedPath;

        public RecordingLoadedProfile(string expectedPath)
        {
            _expectedPath = expectedPath;
            var field = new ProfileFieldDescriptor(
                "owner",
                "alias",
                "담당자",
                10,
                true,
                ProfileFieldValueKind.String,
                false,
                null,
                null)
            {
                ValueMappings = new Dictionary<string, string>
                {
                    ["A"] = "홍길동",
                },
            };
            Descriptor = new ProfileDescriptor(
                "profile.playground",
                "1.0.0",
                "Playground profile",
                ProfileCandidateKind.Directory,
                [field],
                [new ProfileRegexRuleDescriptor(
                    1,
                    "default",
                    @"(?<alias>[^\\/]+)$",
                    ProfileRegexMatchMode.Partial,
                    true,
                    100)])
            {
                ExcludedDirectoryNameRules =
                [
                    new ProfileDirectoryNameExclusionRuleDescriptor(
                        1,
                        "skip-a",
                        @"(?<leaf>A)",
                        ProfileRegexMatchMode.Full,
                        true,
                        100),
                ],
            };

            var raw = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["owner"] = "A",
            };
            var display = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["owner"] = "홍길동",
            };
            Result = ProfileMapResult.Success(new MappedProfileItem(
                Descriptor.Id,
                expectedPath,
                "default",
                raw,
                raw)
            {
                DisplayValues = display,
            });
        }

        public ProfileDescriptor Descriptor { get; }

        public ProfileMapResult Result { get; set; }

        public string? MappedPath { get; private set; }

        public ProfileMapResult Map(ProfilePathCandidate candidate)
        {
            MappedPath = candidate.AbsolutePath;
            Assert.Equal(_expectedPath, candidate.AbsolutePath);
            return Result;
        }

        public ProfileDirectoryNameExclusionResult EvaluateDirectoryName(
            string directoryName) =>
            string.Equals(directoryName, "A", StringComparison.OrdinalIgnoreCase)
                ? ProfileDirectoryNameExclusionResult.Excluded("skip-a")
                : ProfileDirectoryNameExclusionResult.NotExcluded();
    }

    private sealed class StubProfileCatalog(ProfileCatalogSnapshot current) : IProfileCatalog
    {
        public ProfileCatalogSnapshot Current { get; } = current;

        public event EventHandler<ProfileCatalogChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class BlockingPlaygroundEvaluator(string canonicalPath)
        : IProfilePlaygroundEvaluator, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ProfilePlaygroundEvaluation Evaluate(ILoadedProfile profile, string inputPath)
        {
            Started.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test did not release the playground evaluation.");
            }

            return new ProfilePlaygroundEvaluation(
                canonicalPath,
                "old result",
                InfoBarSeverity.Success,
                [new ProfileTestResultViewModel("field", "old value", "success")],
                [],
                new ProfileDirectoryExclusionTestResultViewModel(
                    "A",
                    "not excluded",
                    "test",
                    InfoBarSeverity.Informational),
                []);
        }

        public void Release() => _release.Set();

        public void Dispose() => _release.Dispose();
    }

    private sealed class StubDesktopPickerService : IDesktopPickerService
    {
        public string? PickRootDirectory(string? initialDirectory) => null;

        public string? PickDatabasePath(string? currentPath) => null;

        public string? PickScanLogPath(string suggestedFileName) => null;

        public CatalogSelectionExportDestination? PickCatalogExportPath(
            string suggestedFileName) => null;
    }
}
