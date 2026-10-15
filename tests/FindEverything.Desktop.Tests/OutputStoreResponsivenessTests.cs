using System.IO;
using System.Reflection;
using FindEverything.Desktop.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class OutputStoreResponsivenessTests
{
    [Fact]
    public async Task Format_readers_use_an_immutable_committed_snapshot_while_the_writer_is_busy()
    {
        using var fixture = new Fixture();
        var store = fixture.Formats();
        await store.SaveAsync(Format("old"));
        var snapshot = store.GetAll();
        using var held = await HeldWriterGate.AcquireAsync(store);
        var write = store.SaveAsync(Format("next"));
        try
        {
            var read = await Task.Run(() => store.GetApplicable(null)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains(read, format => format.Id == "old");
            Assert.DoesNotContain(read, format => format.Id == "next");
            Assert.False(write.IsCompleted);
            Assert.Throws<NotSupportedException>(() => ((IList<SelectionOutputFormatDefinition>)snapshot).Clear());
        }
        finally { held.Release(); }
        await write;
        Assert.DoesNotContain(snapshot, format => format.Id == "next");
        Assert.Contains(store.GetAll(), format => format.Id == "next");
    }

    [Fact]
    public async Task Preference_reads_do_not_wait_for_an_outstanding_disk_write()
    {
        using var fixture = new Fixture();
        var store = fixture.Preferences();
        await store.SaveAsync("profile", "old");
        using var held = await HeldWriterGate.AcquireAsync(store);
        var write = store.SaveAsync("profile", "next");
        try
        {
            Assert.Equal("old", await Task.Run(() => store.Get("profile")).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(write.IsCompleted);
        }
        finally { held.Release(); }
        await write;
        Assert.Equal("next", store.Get("profile"));
    }

    [Fact]
    public async Task Individual_format_save_and_delete_merge_other_external_edits()
    {
        using var fixture = new Fixture();
        var stale = fixture.Formats();
        await stale.SaveAsync(Format("mine"));
        var external = fixture.Formats();
        await external.SaveAsync(Format("external"));

        await stale.SaveAsync(Format("mine") with { Template = "{Name}" });
        Assert.Contains(fixture.Formats().GetAll(), format => format.Id == "external");
        await external.SaveAsync(Format("later"));
        Assert.True(await stale.DeleteAsync("mine"));
        var saved = fixture.Formats().GetAll();
        Assert.Contains(saved, format => format.Id == "external");
        Assert.Contains(saved, format => format.Id == "later");
        Assert.DoesNotContain(saved, format => format.Id == "mine");
    }

    [Fact]
    public async Task Failed_or_cancelled_writes_never_publish_unsaved_formats_or_preferences()
    {
        using var fixture = new Fixture();
        var formats = fixture.Formats();
        await formats.SaveAsync(Format("old"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => formats.SaveAsync(Format("cancelled"), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => formats.DeleteAsync("old", cancelled.Token));
        Assert.Contains(formats.GetAll(), format => format.Id == "old");
        Assert.DoesNotContain(formats.GetAll(), format => format.Id == "cancelled");
        File.Delete(fixture.Paths.SelectionOutputFormatsFile);
        Directory.CreateDirectory(fixture.Paths.SelectionOutputFormatsFile);
        var formatFailure = await Record.ExceptionAsync(() => formats.SaveAsync(Format("failed")));
        Assert.True(formatFailure is IOException or UnauthorizedAccessException);
        Assert.Contains(formats.GetAll(), format => format.Id == "old");
        Assert.DoesNotContain(formats.GetAll(), format => format.Id == "failed");

        var preferences = fixture.Preferences();
        await preferences.SaveAsync("profile", "old");
        var preferencePath = Path.Combine(fixture.Paths.LocalDataDirectory, "profile-output-preferences.json");
        File.Delete(preferencePath);
        Directory.CreateDirectory(preferencePath);
        var preferenceFailure = await Record.ExceptionAsync(() => preferences.SaveAsync("profile", "failed"));
        Assert.True(preferenceFailure is IOException or UnauthorizedAccessException);
        Assert.Equal("old", preferences.Get("profile"));
    }

    private static SelectionOutputFormatDefinition Format(string id) => new(id, id, "{FullPath}", "\n", null);

    private sealed class HeldWriterGate : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Task _worker;
        private HeldWriterGate(object owner, TaskCompletionSource entered)
        {
            var gate = owner.GetType().GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
            _worker = Task.Run(() => { lock (gate) { entered.SetResult(); _release.Wait(); } });
        }
        public static async Task<HeldWriterGate> AcquireAsync(object owner)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var result = new HeldWriterGate(owner, entered);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return result;
        }
        public void Release() => _release.Set();
        public void Dispose() { Release(); _worker.GetAwaiter().GetResult(); _release.Dispose(); }
    }

    private sealed class Fixture : IDisposable
    {
        public AppPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "output-responsive-" + Guid.NewGuid().ToString("N")), "unused");
        public Fixture() => Directory.CreateDirectory(Paths.LocalDataDirectory);
        public SelectionOutputFormatStore Formats() => new(Paths, NullLogger<SelectionOutputFormatStore>.Instance);
        public ProfileOutputPreferenceStore Preferences() => new(Paths, NullLogger<ProfileOutputPreferenceStore>.Instance);
        public void Dispose() => Directory.Delete(Paths.LocalDataDirectory, true);
    }
}
