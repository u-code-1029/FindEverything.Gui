using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using FindEverything.Application;
using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Application.Sharing;
using FindEverything.Desktop;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Services;
using FindEverything.Desktop.ViewModels;
using FindEverything.Engine;
using FindEverything.Infrastructure.FindEverything;
using FindEverything.Infrastructure.Persistence;
using FindEverything.Profile.Abstractions;
using FindEverything.Profile.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FindEverything.Desktop.Tests;

/// <summary>Functional regressions on the smoke test's existing Application/STA dispatcher, with an isolated workspace.</summary>
internal static class WorkspaceTransferBehaviorChecks
{
    public static void Verify()
    {
        Assert.NotNull(System.Windows.Application.Current);
        var root = Path.Combine(Path.GetTempPath(), "fe-sharing-behavior-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "local", "settings.json"));
        var localRoot = Path.Combine(root, "local-root");
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Workspace:SelectedProfileId"] = "local-profile", ["Workspace:RootPath"] = localRoot,
            ["Localization:CultureName"] = "ko-KR",
        });
        var noScan = new NoScanCatalogService();
        var missing = new CountingMissingPrompt();
        var clipboard = new MemoryClipboard();
        var services = new ServiceCollection();
        services.AddLogging(); services.AddSingleton<IConfiguration>(configuration);
        services.AddFindEverythingApplication(configuration).AddFindEverythingInfrastructure().AddProfileRuntime()
            .AddDesktopPresentation(configuration, paths);
        services.AddSingleton<ICatalogService>(noScan);
        services.AddSingleton<ICatalogMissingDataPrompt>(missing);
        services.AddSingleton<IClipboardService>(clipboard);
        try
        {
            using var provider = services.BuildServiceProvider();
            var authoring = provider.GetRequiredService<IProfileAuthoringService>();
            Assert.True(Await(authoring.SaveAndApplyAsync(Manifest("local-profile", "Local label"), null)).Applied);
            var frozen = Manifest("shared-profile", "Frozen label");
            var saved = Await(authoring.SaveAndApplyAsync(frozen, null));
            Assert.True(saved.Applied);
            var producerRoot = Path.Combine(root, "producer-root-does-not-exist");
            var metadata = Path.Combine(root, "producer-metadata.db");
            var scan = Guid.NewGuid();
            CreateOfflineMetadata(metadata, producerRoot, scan);
            var catalogPath = Path.Combine(root, "producer-catalog.db");
            CatalogProjectionSnapshot snapshot;
            using (var store = new SqliteCatalogProjectionStore(catalogPath))
            {
                var scope = new CatalogProjectionScope("source", "shared-profile", producerRoot, metadata,
                    provider.GetRequiredService<ProfileTransferService>().GetProjectionFingerprint(frozen), "ko-KR", scan);
                using var writer = store.Begin(scope, saved.Review.Profile!.Descriptor.Fields);
                var values = new Dictionary<string, object?> { ["name"] = "captured producer value" };
                writer.Append(new(Path.Combine(producerRoot, "item"), "item", "rule", values, values, false));
                snapshot = writer.Publish(new(1, 1, 0, 0, 0, false, TimeSpan.Zero, DateTimeOffset.UtcNow));
            }
            var packages = provider.GetRequiredService<IIndexedDatasetTransferService>();
            var package = Path.Combine(root, "source.fepkg");
            Await(packages.ExportAsync(new(frozen, metadata, producerRoot, package)
            { CatalogDatabasePath = catalogPath, CatalogSnapshot = snapshot }));
            var receiverRoot = Path.Combine(root, "receiver-root-does-not-exist");
            var source = Await(packages.ImportAsync(package, ProfileImportConflictChoice.Replace, receiverRoot))!;
            File.WriteAllText(Path.Combine(paths.LocalDataDirectory, "workspace-source.json"), JsonSerializer.Serialize(source.Id));

            var transfer = provider.GetRequiredService<WorkspaceTransferViewModel>();
            var catalog = provider.GetRequiredService<CatalogViewModel>();
            var files = provider.GetRequiredService<FileSearchViewModel>();
            Await(files.ActivateAsync());
            catalog.Activate();
            Until(() => transfer.CurrentSource?.Id == source.Id && catalog.HasLoadedItems && !catalog.IsBusy && files.HasIndex && !files.IsSearching,
                "Persisted shared source was not restored and queried.");
            Assert.False(Directory.Exists(producerRoot)); Assert.False(Directory.Exists(receiverRoot));
            Assert.True(catalog.IsReadOnlyDataSource); Assert.True(files.IsReadOnlyDataSource);
            Assert.False(catalog.ScanCommand.CanExecute(null));
            Assert.False(files.RefreshIndexCommand.CanExecute(null));
            Assert.Equal(0, noScan.Calls); Assert.Equal(0, missing.Calls);
            Assert.Equal("local-profile", provider.GetRequiredService<IWorkspaceContext>().Current.SelectedProfileId);
            var row = Assert.Single(catalog.Items.Cast<CatalogItemViewModel>());
            Assert.Equal(Path.Combine(receiverRoot, "item"), row.FullPath);
            Assert.Equal(Path.Combine(producerRoot, "item"), row.OriginalFullPath);
            Assert.Equal("captured producer value", row.Values["name"]);
            Assert.Equal("Frozen label", Assert.Single(catalog.Fields).Header);
            Await(catalog.SelectRowAsync(row, additive: true, range: false)); Assert.Equal(1, catalog.SelectedCount);
            var format = new SelectionOutputFormatDefinition("sharing-fixture", "Stored fields",
                "{FullPath}|{OriginalFullPath}|{Field:name}", "", "shared-profile");
            catalog.SelectedOutputFormat = format;
            Await(catalog.CopySelectionCommand.ExecuteAsync(null));
            Assert.Equal(Path.Combine(receiverRoot, "item") + "|" + Path.Combine(producerRoot, "item") + "|captured producer value", clipboard.Text);
            var file = Assert.Single(files.Items);
            Assert.Equal(Path.Combine(receiverRoot, "item", "data.txt"), file.FullPath);
            Assert.Equal(Path.Combine(producerRoot, "item", "data.txt"), file.OriginalFullPath);
            Await(files.SelectRowAsync(file, additive: true, range: false)); Assert.Equal(1, files.SelectedCount);
            files.SelectedOutputFormat = format;
            Await(files.CopySelectionCommand.ExecuteAsync(null));
            Assert.Equal(Path.Combine(receiverRoot, "item", "data.txt") + "|" + Path.Combine(producerRoot, "item", "data.txt") + "|captured producer value", clipboard.Text);

            var editable = Await(authoring.ListAsync()).Single(p => p.Id == "shared-profile");
            var edited = Await(authoring.LoadAsync(editable))!;
            edited.Fields![0].Header = "Edited local label";
            Assert.True(Await(authoring.SaveAndApplyAsync(edited, edited.Id)).Applied);
            Until(() => !catalog.IsBusy && !files.IsSearching, "Local profile update did not settle.");
            Assert.True(catalog.HasLoadedItems);
            Assert.Equal("Frozen label", Assert.Single(catalog.Fields).Header);
            Assert.Equal("captured producer value", Assert.Single(catalog.Items.Cast<CatalogItemViewModel>()).Values["name"]);

            // Relink updates the registry item in-place and must not transiently select Local.
            var localTransitions = 0;
            transfer.SourceChanged += (_, _) => { if (transfer.IsLocal) localTransitions++; };
            var newRoot = Path.Combine(root, "relinked-root-does-not-exist");
            var linked = Await(packages.RelinkAsync(source, newRoot));
            Until(() => transfer.CurrentSource?.LinkRevision == 1 && catalog.RootPath == newRoot && catalog.HasLoadedItems && !catalog.IsBusy && files.RootPath == newRoot && !files.IsSearching,
                "Registry relink did not refresh the selected shared source.");
            Assert.Equal(0, localTransitions); Assert.Equal(0, noScan.Calls); Assert.Equal(0, missing.Calls);
            Assert.Equal(Path.Combine(producerRoot, "item"), Assert.Single(catalog.Items.Cast<CatalogItemViewModel>()).OriginalFullPath);

            var metadataOnly = linked with { Id = Guid.NewGuid().ToString("N"), CatalogDatabasePath = null, CatalogSnapshot = null };
            var metadataChoice = new WorkspaceSourceChoice(metadataOnly.Id, "Metadata only", metadataOnly);
            transfer.Sources.Add(metadataChoice); transfer.SelectedSource = metadataChoice;
            Until(() => catalog.IsReadOnlyDataSource && !catalog.HasLoadedItems && !catalog.IsBusy && !files.IsSearching,
                "A metadata-only shared source must remain empty in Catalog and searchable in Files.");
            Assert.Empty(catalog.Items.Cast<CatalogItemViewModel>()); Assert.True(files.HasIndex);
            Assert.Equal(0, noScan.Calls); Assert.Equal(0, missing.Calls);

            // Rapid source changes must never adopt the superseded shared query.
            var originalChoice = transfer.Sources.Single(x => x.Id == source.Id);
            transfer.SelectedSource = originalChoice; transfer.SelectedSource = metadataChoice; transfer.SelectedSource = originalChoice;
            Until(() => catalog.HasLoadedItems && !catalog.IsBusy && transfer.CurrentSource?.Id == source.Id && !files.IsSearching,
                "The latest source selection was not adopted after rapid switches.");
            Assert.Equal(newRoot, catalog.RootPath);
            Assert.Single(catalog.Items.Cast<CatalogItemViewModel>());
            catalog.Deactivate(); transfer.SelectedSource = transfer.Sources[0];
            Until(() => catalog.IsReadOnlyDataSource == false && catalog.RootPath == localRoot && catalog.SelectedProfile?.Id == "local-profile" && !catalog.IsBusy,
                "Returning to Local must restore its profile as well as its root/database.");
            Assert.Equal("Local label", Assert.Single(catalog.Fields).Header);
            Until(() => !files.IsSearching && !files.IsBusy, "The final local file query did not settle before disposal.");
            Until(() => File.Exists(Path.Combine(paths.LocalDataDirectory, "workspace-source.json"))
                && JsonSerializer.Deserialize<string>(File.ReadAllText(Path.Combine(paths.LocalDataDirectory, "workspace-source.json"))) == "local",
                "The selected data source was not persisted independently of the live workspace.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static ProfileManifest Manifest(string id, string header) => new()
    {
        ContractVersion = ProfileContract.CurrentMajor, Kind = ProfileKind.Declarative, Id = id, Version = "1.0.0", DisplayName = id,
        Fields = [new() { FieldId = "name", GroupName = "name", Header = header }],
        // The source directory does not exist; imported values must never be recomputed by this regex.
        Rules = [new() { Id = "rule", Pattern = @"(?<name>THIS_PATTERN_DOES_NOT_MATCH_IMPORTED_VALUES)", MatchMode = ProfileRegexMatchMode.Full }],
    };
    private static void CreateOfflineMetadata(string database, string root, Guid scan)
    {
        Await(Initialize());
        async Task Initialize()
        {
            await using var store = new SqliteIndexStore(database);
            await using var lease = await store.AcquireWriterAsync(); await store.InitializeAsync();
        }
        using var connection = new SqliteConnection($"Data Source={database};Pooling=false"); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO roots(root_key,root_path,last_scan_id,last_scope_path,last_status,last_published_ticks,last_entries,last_errors) VALUES($root,$path,$scan,$path,0,$ticks,1,0);"
            + "INSERT INTO entries(root_key,path_key,full_path,path_search,name,name_search,file_type,parent_path,kind,size_bytes,created_ticks,modified_ticks) VALUES($root,$key,$file,$search,'data.txt','data.txt','.txt',$parent,0,10,$ticks,$ticks);";
        var file = Path.Combine(root, "item", "data.txt");
        command.Parameters.AddWithValue("$root", PathRules.Key(root)); command.Parameters.AddWithValue("$path", root);
        command.Parameters.AddWithValue("$scan", scan.ToString("N")); command.Parameters.AddWithValue("$ticks", DateTimeOffset.UtcNow.UtcTicks);
        command.Parameters.AddWithValue("$key", PathRules.Key(file)); command.Parameters.AddWithValue("$file", file);
        command.Parameters.AddWithValue("$search", file.ToLowerInvariant()); command.Parameters.AddWithValue("$parent", Path.GetDirectoryName(file)!);
        command.ExecuteNonQuery();
    }
    private static T Await<T>(Task<T> task) { Until(() => task.IsCompleted, "An asynchronous sharing operation timed out."); return task.GetAwaiter().GetResult(); }
    private static void Await(Task task) { Until(() => task.IsCompleted, "An asynchronous sharing operation timed out."); task.GetAwaiter().GetResult(); }
    private static void Until(Func<bool> predicate, string message)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        var deadline = DateTime.UtcNow.AddSeconds(15);
        timer.Tick += (_, _) => { if (predicate() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Assert.True(predicate(), message);
    }
    private sealed class NoScanCatalogService : ICatalogService
    {
        public int Calls { get; private set; }
        private Task<CatalogResult> Unexpected() { Calls++; return Task.FromException<CatalogResult>(new InvalidOperationException("Imported data must not map or scan live paths.")); }
        public Task<CatalogResult> LoadExistingAsync(CatalogRequest request, IProgress<CatalogOperationProgress>? progress = null, CancellationToken cancellationToken = default) => Unexpected();
        public Task<CatalogResult> DiscoverAsync(CatalogRequest request, IProgress<CatalogOperationProgress>? progress = null, CancellationToken cancellationToken = default) => Unexpected();
        public Task<CatalogResult> ScanAndLoadAsync(CatalogRequest request, IProgress<CatalogOperationProgress>? progress = null, CancellationToken cancellationToken = default) => Unexpected();
    }
    private sealed class CountingMissingPrompt : ICatalogMissingDataPrompt
    {
        public int Calls { get; private set; }
        public Task<CatalogMissingDataDecision> ChooseAsync(string profileName, bool hasIndex, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(CatalogMissingDataDecision.Later); }
    }
    private sealed class MemoryClipboard : IClipboardService
    {
        public string Text { get; private set; } = string.Empty;
        public bool TrySetText(string text, out string? errorMessage) { Text = text; errorMessage = null; return true; }
    }
}
