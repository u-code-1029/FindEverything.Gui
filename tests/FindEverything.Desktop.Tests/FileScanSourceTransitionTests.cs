using System.IO;
using System.Reflection;
using FindEverything.Application.Sharing;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class CatalogMissingDataAndCancellationTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public Task File_source_changes_during_scan_cleanup_load_only_the_latest_source(
        bool partialScan, bool deactivate, bool cleanupFails) => OnStaAsync(async () =>
    {
        var directory = Directory.CreateTempSubdirectory("findeverything-file-source-transition-").FullName;
        var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? scanning = null;
        WorkspaceTransferViewModel? transfer = null;
        try
        {
            var service = new FileService();
            service.Query.ReleaseCount.TrySetResult(1);
            service.ScanOverride = async (_, token) =>
            {
                scanStarted.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally
                {
                    cleanupStarted.TrySetResult();
                    await releaseCleanup.Task;
                    if (cleanupFails) throw new IOException("Simulated scan cleanup failure.");
                }
                throw new InvalidOperationException("The scan must be cancelled by source selection.");
            };
            using var owner = CreateFilesOwner(service);
            transfer = new WorkspaceTransferViewModel(null!, null!, new EmptySourceRegistry(), null!, null!, null!,
                new Snackbar(), null!, null!, null!, new TestAppLocalizer(), ImmediateUiScheduler.Instance,
                new AppPaths(directory, Path.Combine(directory, "settings.json")));
            await transfer.InitializationTask;
            owner.AttachWorkspaceTransfer(transfer);
            await owner.ActivateAsync();
            Assert.Single(owner.Items);
            Assert.Equal(1, service.OpenCalls);

            owner.RefreshScopePath = directory; // A real child of the fixture's temporary root.
            scanning = partialScan ? owner.RefreshScopeCommand.ExecuteAsync(null) : owner.RefreshIndexCommand.ExecuteAsync(null);
            await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var first = SharedSource("first", directory);
            var latest = SharedSource("latest", directory);
            transfer.Sources.Add(first);
            transfer.Sources.Add(latest);
            transfer.SelectedSource = first;
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(owner.IsBusy);
            Assert.Empty(owner.Items);
            Assert.Equal(1, service.OpenCalls); // No status/query against the cancelled scan's old scope.

            transfer.SelectedSource = latest;
            if (deactivate) owner.Deactivate();
            service.Query = new FileQuery { RootPath = latest.Source!.EffectiveRoot };
            service.Query.ReleaseCount.TrySetResult(1);
            releaseCleanup.TrySetResult();
            await scanning.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(owner.IsBusy);
            if (deactivate)
            {
                Assert.Empty(owner.Items);
                Assert.Equal(1, service.OpenCalls);
                await owner.ActivateAsync();
            }
            Assert.Single(owner.Items);
            Assert.True(owner.HasIndex);
            Assert.True(owner.CanSearch);
            Assert.Equal(2, service.OpenCalls);
            Assert.Equal(latest.Source!.MetadataDatabasePath, service.LastScope!.DatabasePath);
            Assert.Equal(latest.Source.EffectiveRoot, service.LastScope.RootPath);
            Assert.Equal(1, service.ScanCalls);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            if (scanning is not null) await scanning.WaitAsync(TimeSpan.FromSeconds(5));
            if (transfer is not null)
            {
                // Source selection saves asynchronously; finish that fixture I/O
                // before deleting its dedicated, temporary directory.
                var gate = (SemaphoreSlim)typeof(WorkspaceTransferViewModel)
                    .GetField("_selectionSaveGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transfer)!;
                await gate.WaitAsync();
                gate.Release();
                transfer.Dispose();
            }
            Directory.Delete(directory, recursive: true);
        }
    });

    private static WorkspaceSourceChoice SharedSource(string id, string directory)
    {
        var root = Path.Combine(directory, id);
        var source = new ImportedWorkspaceSource(id, "sample", id, root, root, directory,
            Path.Combine(directory, id + ".db"), null, null, 1, false, false, DateTimeOffset.UtcNow, 0, null!);
        return new(id, id, source);
    }

    private sealed class EmptySourceRegistry : IImportedWorkspaceRegistry
    {
        public event EventHandler? Changed { add { } remove { } }
        public Task<IReadOnlyList<ImportedWorkspaceSource>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ImportedWorkspaceSource>>([]);
    }
}
