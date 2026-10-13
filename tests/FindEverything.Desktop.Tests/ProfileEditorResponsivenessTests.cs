using FindEverything.Application.Profiles;
using FindEverything.Desktop.ViewModels;
using FindEverything.Profile.Runtime;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class ProfileEditorResponsivenessTests
{
    [Fact]
    public void Editing_a_caption_does_not_compile_or_republish_unrelated_collections()
    {
        var compiler = new CountingCompiler();
        using var vm = ProfilesValidationTests.CreateForUi(compiler);
        var revision = vm.DraftRevision;
        var fields = vm.AvailableSelectionFieldIds;
        var errors = vm.ValidationIssues;
        var calls = compiler.Calls;
        vm.DraftFields[0].Header = "Changed caption";
        Assert.Equal(revision + 1, vm.DraftRevision);
        Assert.Equal(calls, compiler.Calls);
        Assert.Same(fields, vm.AvailableSelectionFieldIds);
        Assert.Same(errors, vm.ValidationIssues);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task Rapid_path_edits_compile_one_latest_snapshot_and_keep_numeric_validation_immediate()
    {
        var compiler = new CountingCompiler();
        using var vm = ProfilesValidationTests.CreateForUi(compiler);
        await vm.ActivateAsync();
        var calls = compiler.Calls;
        for (var i = 0; i < 30; i++) vm.DraftPathTemplate = $"C:/v{i}/{{name@name}}";
        Assert.Equal(calls, compiler.Calls);
        await vm.PendingPreview;
        Assert.Equal(calls + 1, compiler.Calls);
        Assert.Contains("v29", vm.GeneratedPatternPreview);

        var notifications = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.ValidationIssues)) notifications++; };
        vm.DraftRules[0].TimeoutMillisecondsText = "invalid";
        Assert.False(vm.SaveDraftCommand.CanExecute(null));
        Assert.True(vm.HasValidationIssues);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task An_old_worker_result_cannot_replace_a_newer_preview()
    {
        var compiler = new CountingCompiler();
        using var vm = ProfilesValidationTests.CreateForUi(compiler);
        await vm.ActivateAsync();
        compiler.BlockNext = true;
        vm.DraftPathTemplate = "C:/old/{name@name}";
        var oldWork = vm.PendingPreview;
        await compiler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.DraftPathTemplate = "C:/latest/{name@name}";
        await vm.PendingPreview;
        compiler.Release.SetResult();
        await oldWork;
        Assert.Contains("latest", vm.GeneratedPatternPreview);
        Assert.DoesNotContain("old", vm.GeneratedPatternPreview);
    }

    [Fact]
    public async Task Leaving_the_page_cancels_preview_and_reentry_computes_the_pending_draft()
    {
        var compiler = new CountingCompiler();
        using var vm = ProfilesValidationTests.CreateForUi(compiler);
        await vm.ActivateAsync();
        var calls = compiler.Calls;
        vm.DraftPathTemplate = "C:/pending/{name@name}";
        vm.Deactivate();
        await vm.PendingPreview;
        Assert.Equal(calls, compiler.Calls);
        await vm.ActivateAsync();
        await vm.PendingPreview;
        Assert.Contains("pending", vm.GeneratedPatternPreview);
    }

    private sealed class CountingCompiler : IProfilePathTemplateCompiler
    {
        private readonly ProfilePathTemplateCompiler _inner = new();
        public int Calls;
        public bool BlockNext;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProfilePathTemplateCompileResult Compile(string? template, IReadOnlyList<ProfileFieldManifest>? fields)
        {
            Interlocked.Increment(ref Calls);
            if (BlockNext)
            {
                BlockNext = false;
                Entered.TrySetResult();
                Release.Task.Wait(TimeSpan.FromSeconds(10));
            }
            return _inner.Compile(template, fields);
        }
    }
}
