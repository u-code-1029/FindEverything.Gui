using System.IO;
using FindEverything.Application.Options;
using FindEverything.Desktop.Services;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class WorkspaceContextTests
{
    [Fact]
    public async Task UpdateAsync_merges_against_the_snapshot_published_by_an_earlier_save()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"workspace-context-{Guid.NewGuid():N}");
        var initialRoot = Path.Combine(testRoot, "initial-root");
        var editedRoot = Path.Combine(testRoot, "edited-root");
        var initialDatabase = Path.Combine(testRoot, "initial.db");
        var externallyEditedDatabase = Path.Combine(testRoot, "external.db");
        var writer = new GatedSettingsWriter();
        using var context = new WorkspaceContext(
            new StubSettingsState<WorkspaceOptions>(new WorkspaceOptions
            {
                SelectedProfileId = "profile",
                RootPath = initialRoot,
                DatabasePath = initialDatabase,
            }),
            writer);

        var externalSave = context.SaveAsync(
            context.Current with { DatabasePath = externallyEditedDatabase });
        await writer.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        WorkspaceSnapshot? mergeInput = null;
        var localFieldUpdate = context.UpdateAsync(current =>
        {
            mergeInput = current;
            return current with { RootPath = editedRoot };
        });
        try
        {
            await Task.Yield();
            Assert.Null(mergeInput);
            Assert.False(localFieldUpdate.IsCompleted);
        }
        finally
        {
            writer.ReleaseFirstSave.TrySetResult();
        }

        await Task.WhenAll(externalSave, localFieldUpdate).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.NotNull(mergeInput);
        Assert.Equal(Path.GetFullPath(externallyEditedDatabase), mergeInput.DatabasePath);
        Assert.Equal(Path.GetFullPath(editedRoot), context.Current.RootPath);
        Assert.Equal(Path.GetFullPath(externallyEditedDatabase), context.Current.DatabasePath);
        Assert.Collection(
            writer.Updates,
            first =>
            {
                Assert.Equal(Path.GetFullPath(initialRoot), first.Workspace?.RootPath);
                Assert.Equal(
                    Path.GetFullPath(externallyEditedDatabase),
                    first.Workspace?.FileSearchDatabasePath);
                Assert.Null(first.Workspace?.DatabasePath);
            },
            second =>
            {
                Assert.Equal(Path.GetFullPath(editedRoot), second.Workspace?.RootPath);
                Assert.Equal(
                    Path.GetFullPath(externallyEditedDatabase),
                    second.Workspace?.FileSearchDatabasePath);
                Assert.Null(second.Workspace?.DatabasePath);
            });
    }

    [Fact]
    public async Task Profile_database_overrides_are_normalized_preserved_and_removable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"workspace-context-{Guid.NewGuid():N}");
        var writer = new RecordingSettingsWriter();
        using var context = new WorkspaceContext(
            new StubSettingsState<WorkspaceOptions>(new WorkspaceOptions
            {
                SelectedProfileId = "profile-a",
                FileSearchDatabasePath = Path.Combine(testRoot, "files.db"),
                ProfileDatabasePaths = new Dictionary<string, string>
                {
                    ["profile-a"] = Path.Combine(testRoot, "old.db"),
                },
            }),
            writer);

        var replacement = Path.Combine(testRoot, "nested", "..", "new.db");
        await context.SetProfileDatabasePathAsync("PROFILE-A", replacement);

        var expected = Path.GetFullPath(replacement);
        Assert.Equal(expected, context.Current.ProfileDatabasePaths!["profile-a"]);
        Assert.Equal(
            expected,
            writer.Updates.Single().Workspace?.ProfileDatabasePaths?["PROFILE-A"]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(testRoot, "files.db")),
            writer.Updates.Single().Workspace?.FileSearchDatabasePath);

        await context.SetProfileDatabasePathAsync("profile-a", null);

        Assert.Empty(context.Current.ProfileDatabasePaths!);
        Assert.Empty(writer.Updates.Last().Workspace?.ProfileDatabasePaths!);
    }

    [Fact]
    public void Constructor_migrates_the_legacy_database_to_file_search_only()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"workspace-context-{Guid.NewGuid():N}");
        var legacyDatabase = Path.Combine(testRoot, "legacy.db");
        using var context = new WorkspaceContext(
            new StubSettingsState<WorkspaceOptions>(new WorkspaceOptions
            {
                SelectedProfileId = "profile-a",
                DatabasePath = legacyDatabase,
            }),
            new RecordingSettingsWriter());

        Assert.Equal(Path.GetFullPath(legacyDatabase), context.Current.DatabasePath);
        Assert.Empty(context.Current.ProfileDatabasePaths!);
    }

    [Fact]
    public async Task Three_field_save_from_an_older_caller_preserves_profile_overrides()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"workspace-context-{Guid.NewGuid():N}");
        var profileDatabase = Path.Combine(testRoot, "profile.db");
        var writer = new RecordingSettingsWriter();
        using var context = new WorkspaceContext(
            new StubSettingsState<WorkspaceOptions>(new WorkspaceOptions
            {
                ProfileDatabasePaths = new Dictionary<string, string>
                {
                    ["profile-a"] = profileDatabase,
                },
            }),
            writer);

        await context.SaveAsync(new WorkspaceSnapshot(
            "profile-a",
            Path.Combine(testRoot, "root"),
            null));

        Assert.Equal(
            Path.GetFullPath(profileDatabase),
            context.Current.ProfileDatabasePaths!["profile-a"]);
        Assert.Equal(
            Path.GetFullPath(profileDatabase),
            writer.Updates.Single().Workspace?.ProfileDatabasePaths?["profile-a"]);
    }

    private sealed class StubSettingsState<T>(T current) : IValidatedSettingsState<T>
        where T : class
    {
        public T Current { get; } = current;
    }

    private sealed class GatedSettingsWriter : IUserSettingsWriter
    {
        private readonly object _gate = new();
        private readonly List<UserSettingsUpdate> _updates = [];
        private int _saveCount;

        public TaskCompletionSource FirstSaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstSave { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<UserSettingsUpdate> Updates
        {
            get
            {
                lock (_gate)
                {
                    return _updates.ToArray();
                }
            }
        }

        public async Task SaveAsync(
            UserSettingsUpdate update,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _updates.Add(update);
            }

            if (Interlocked.Increment(ref _saveCount) != 1)
            {
                return;
            }

            FirstSaveStarted.TrySetResult();
            await ReleaseFirstSave.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class RecordingSettingsWriter : IUserSettingsWriter
    {
        public List<UserSettingsUpdate> Updates { get; } = [];

        public Task SaveAsync(
            UserSettingsUpdate update,
            CancellationToken cancellationToken = default)
        {
            Updates.Add(update);
            return Task.CompletedTask;
        }
    }
}
