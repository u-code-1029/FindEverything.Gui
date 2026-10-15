using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Globalization;
using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Application.Options;
using FindEverything.Application.Results;
using FindEverything.Application.Sharing;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Services;
using FindEverything.Infrastructure.FindEverything;
using FindEverything.Infrastructure.Persistence;
using FindEverything.Profile.Abstractions;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed partial class WorkspaceTransferServiceTests
{
    [Fact]
    public async Task PortableJsonExportsLatestSettingsAndImportsIndependentFormatOwnership()
    {
        using var fixture = new Fixture();
        var authoring = fixture.Provider.GetRequiredService<IProfileAuthoringService>();
        var profile = Manifest();
        Assert.True((await authoring.SaveAndApplyAsync(profile, null)).Applied);
        var formats = fixture.Provider.GetRequiredService<ISelectionOutputFormatStore>();
        formats.Save(new("profile-format", "Name lines", "{name}", ", ", profile.Id, "[", "]"));
        formats.Save(new("other-format", "Other", "{FullPath}", "\n", "another-profile"));
        formats.Save(new("unselected-common", "Common", "{FullPath}", "\n", null));
        await fixture.Provider.GetRequiredService<IProfileOutputPreferenceStore>().SaveAsync(profile.Id!, "profile-format");
        await fixture.Provider.GetRequiredService<IProfilePresentationStore>().SaveAsync(profile.Id!, new Dictionary<string, ColumnWidthPreset> { ["name"] = ColumnWidthPreset.Wide });
        await fixture.Provider.GetRequiredService<IGridLayoutStore>().SaveAsync(profile.Id!, [new("name", 0, 240, GridColumnWidthMode.Manual, true)]);
        var transfer = fixture.Provider.GetRequiredService<ProfileTransferService>();
        var beforeFingerprint = transfer.GetProjectionFingerprint(profile);
        var path = Path.Combine(fixture.Root, "portable.json");
        await transfer.ExportAsync(profile, path);
        Assert.Null(profile.Workspace); // Export does not mutate the caller's definition.
        var exported = (await transfer.InspectAsync(path)).Manifest;
        var included = Assert.Single(exported.Workspace!.OutputFormats!);
        Assert.Equal("profile-format", included.Id);
        Assert.Equal("[", included.Prefix);
        Assert.Equal(beforeFingerprint, transfer.GetProjectionFingerprint(exported));
        var imported = (await transfer.ImportAsync(path))!;
        Assert.NotEqual(profile.Id, imported.ProfileId);
        var installedFormat = Assert.Single(formats.GetAll(), f => f.ProfileId == imported.ProfileId);
        Assert.NotEqual(included.Id, installedFormat.Id);
        Assert.Equal("[", installedFormat.Prefix);
        Assert.Equal(installedFormat.Id, fixture.Provider.GetRequiredService<IProfileOutputPreferenceStore>().Get(imported.ProfileId));
        var column = fixture.Provider.GetRequiredService<IGridLayoutStore>().Get(imported.ProfileId)["name"];
        Assert.True(column.IsHidden); Assert.Equal(240, column.Width); Assert.Equal(GridColumnWidthMode.Manual, column.WidthMode);
        Assert.Equal(ColumnWidthPreset.Wide, (await fixture.Provider.GetRequiredService<IProfilePresentationStore>().LoadAsync(imported.ProfileId))["name"]);
        Assert.Contains(formats.GetAll(), f => f.Id == "profile-format" && f.ProfileId == profile.Id);
        Assert.Contains(formats.GetAll(), f => f.Id == "other-format");
        Assert.Contains(formats.GetAll(), f => f.Id == "unselected-common");
        var roundTrip = Path.Combine(fixture.Root, "portable-again.json");
        var saved = await authoring.LoadAsync((await authoring.ListAsync()).Single(p => p.Id == imported.ProfileId));
        await transfer.ExportAsync(saved!, roundTrip);
        var second = (await transfer.InspectAsync(roundTrip)).Manifest.Workspace!;
        Assert.Equal(installedFormat.Id, second.SelectedOutputFormatId);
        Assert.Equal(240, Assert.Single(second.Columns!).Width);
    }

    [Fact]
    public async Task PortableSettingsCallbackFailureRestoresEveryStoreAndProfile()
    {
        using var fixture = new Fixture();
        var authoring = fixture.Provider.GetRequiredService<IProfileAuthoringService>();
        var original = Manifest(); original.DisplayName = "Original";
        Assert.True((await authoring.SaveAndApplyAsync(original, null)).Applied);
        var formats = fixture.Provider.GetRequiredService<ISelectionOutputFormatStore>();
        var preference = fixture.Provider.GetRequiredService<IProfileOutputPreferenceStore>();
        var presentation = fixture.Provider.GetRequiredService<IProfilePresentationStore>();
        var layout = fixture.Provider.GetRequiredService<IGridLayoutStore>();
        var originalFormat = new SelectionOutputFormatDefinition("old-format", "Old", "{FullPath}", "\n", original.Id);
        formats.Save(originalFormat);
        await preference.SaveAsync(original.Id!, originalFormat.Id);
        await presentation.SaveAsync(original.Id!, new Dictionary<string, ColumnWidthPreset> { ["name"] = ColumnWidthPreset.Narrow });
        var oldColumn = new GridColumnLayout("name", 0, 190, GridColumnWidthMode.Manual);
        await layout.SaveAsync(original.Id!, [oldColumn]);
        var incoming = Manifest(); incoming.DisplayName = "Incoming"; incoming.Workspace = PortableSettings();
        var transfer = fixture.Provider.GetRequiredService<ProfileTransferService>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer.InstallAtomicallyAsync<bool>(incoming,
            ProfileImportConflictChoice.Replace, _ => Task.FromException<bool>(new OperationCanceledException()), CancellationToken.None));
        Assert.Equal(originalFormat, Assert.Single(formats.GetAll(), f => f.ProfileId == original.Id));
        Assert.Equal(originalFormat.Id, preference.Get(original.Id!));
        Assert.Equal(ColumnWidthPreset.Narrow, (await presentation.LoadAsync(original.Id!))["name"]);
        Assert.Equal(oldColumn, layout.Get(original.Id!)["name"]);
        Assert.Equal("Original", Assert.Single(fixture.Provider.GetRequiredService<IProfileCatalog>().Current.Profiles).Descriptor.DisplayName);
    }

    [Fact]
    public async Task PortableRollbackPreservesLaterSettingsEditsButStillRestoresTheProfile()
    {
        using var fixture = new Fixture();
        var original = Manifest(); original.DisplayName = "Original";
        Assert.True((await fixture.Provider.GetRequiredService<IProfileAuthoringService>().SaveAndApplyAsync(original, null)).Applied);
        var incoming = Manifest(); incoming.DisplayName = "Incoming"; incoming.Workspace = PortableSettings();
        var preferences = fixture.Provider.GetRequiredService<IProfileOutputPreferenceStore>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.GetRequiredService<ProfileTransferService>()
            .InstallAtomicallyAsync<bool>(incoming, ProfileImportConflictChoice.Replace, async result =>
            {
                await preferences.SaveAsync(result.ProfileId, "later-user-edit");
                throw new OperationCanceledException();
            }, CancellationToken.None));
        Assert.Equal("later-user-edit", preferences.Get(original.Id!));
        Assert.Equal("Original", Assert.Single(fixture.Provider.GetRequiredService<IProfileCatalog>().Current.Profiles).Descriptor.DisplayName);
    }

    [Fact]
    public async Task PortableJsonRejectsInvalidFormatReferencesBeforeInstallingAnything()
    {
        using var fixture = new Fixture();
        var manifest = Manifest(); manifest.Workspace = PortableSettings();
        manifest.Workspace.OutputFormats![0].Template = "{not-a-column}";
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Provider.GetRequiredService<ProfileTransferService>()
            .ImportManifestAsync(manifest, ProfileImportConflictChoice.NewProfile));
        Assert.Empty(await fixture.Provider.GetRequiredService<IProfileAuthoringService>().ListAsync());
        Assert.Single(fixture.Provider.GetRequiredService<ISelectionOutputFormatStore>().GetAll());
    }

    private static ProfileWorkspaceManifest PortableSettings() => new()
    {
        OutputFormats = [new() { Id = "names", DisplayName = "Names", Template = "{name}" }],
        SelectedOutputFormatId = "names", Columns = [new() { FieldId = "name", WidthPreset = ProfileColumnWidthPreset.Wide,
            DisplayIndex = 0, WidthMode = ProfileColumnWidthMode.Manual, Width = 240, IsHidden = true }],
    };

    [Fact]
    public async Task PortableJsonIncludesOnlyTheSelectedCommonFormatDependency()
    {
        using var fixture = new Fixture();
        var manifest = Manifest();
        Assert.True((await fixture.Provider.GetRequiredService<IProfileAuthoringService>().SaveAndApplyAsync(manifest, null)).Applied);
        var formats = fixture.Provider.GetRequiredService<ISelectionOutputFormatStore>();
        formats.Save(new("selected-common", "Selected common", "{FullPath}", "\n", null));
        formats.Save(new("other-common", "Other common", "{FullPath}", "\n", null));
        await fixture.Provider.GetRequiredService<IProfileOutputPreferenceStore>().SaveAsync(manifest.Id!, "selected-common");
        var snapshot = await fixture.Provider.GetRequiredService<IProfileWorkspaceSettingsService>().CreateExportSnapshotAsync(manifest);
        Assert.Equal("selected-common", Assert.Single(snapshot.Workspace!.OutputFormats!).Id);
        var result = (await fixture.Provider.GetRequiredService<ProfileTransferService>().ImportManifestAsync(snapshot, ProfileImportConflictChoice.NewProfile))!;
        var scoped = Assert.Single(formats.GetAll(), f => f.ProfileId == result.ProfileId);
        Assert.NotEqual("selected-common", scoped.Id);
        Assert.Contains(formats.GetAll(), f => f.Id == "selected-common" && f.ProfileId is null);
        Assert.Contains(formats.GetAll(), f => f.Id == "other-common" && f.ProfileId is null);
    }

    [Fact]
    public async Task SettingsWriteFailureRollsBackEarlierSettingsAndProfileCommit()
    {
        using var fixture = new Fixture();
        var original = Manifest(); original.DisplayName = "Original";
        Assert.True((await fixture.Provider.GetRequiredService<IProfileAuthoringService>().SaveAndApplyAsync(original, null)).Applied);
        var preference = fixture.Provider.GetRequiredService<IProfileOutputPreferenceStore>();
        var layout = fixture.Provider.GetRequiredService<IGridLayoutStore>();
        await preference.SaveAsync(original.Id!, SelectionOutputFormatDefaults.FullPathLinesId);
        var oldColumn = new GridColumnLayout("name", 0, 180, GridColumnWidthMode.Manual);
        await layout.SaveAsync(original.Id!, [oldColumn]);
        // A destination directory forces the presentation's atomic file move to fail.
        Directory.CreateDirectory(Path.Combine(fixture.Paths.UserProfilesDirectory, original.Id!, "presentation.json"));
        var incoming = Manifest(); incoming.DisplayName = "Incoming"; incoming.Workspace = PortableSettings();
        var failure = await Record.ExceptionAsync(() => fixture.Provider.GetRequiredService<ProfileTransferService>()
            .ImportManifestAsync(incoming, ProfileImportConflictChoice.Replace));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Equal(SelectionOutputFormatDefaults.FullPathLinesId, preference.Get(original.Id!));
        Assert.Equal(oldColumn, layout.Get(original.Id!)["name"]);
        Assert.Single(fixture.Provider.GetRequiredService<ISelectionOutputFormatStore>().GetAll());
        Assert.Equal("Original", Assert.Single(fixture.Provider.GetRequiredService<IProfileCatalog>().Current.Profiles).Descriptor.DisplayName);
    }

    [Fact]
    public async Task JsonOpenInstallsValidatedLocalProfileWithoutChangingExternalFile()
    {
        using var fixture = new Fixture();
        var transfer = fixture.Provider.GetRequiredService<IProfileTransferService>();
        var file = Path.Combine(fixture.Root, "external.json");
        await transfer.ExportAsync(Manifest(), file);
        var before = await File.ReadAllBytesAsync(file);
        Assert.False((await transfer.InspectAsync(file)).HasIdConflict);
        var first = (await transfer.ImportAsync(file))!;
        Assert.True(File.Exists(first.ManifestPath));
        Assert.StartsWith(fixture.Paths.UserProfilesDirectory, first.ManifestPath, StringComparison.OrdinalIgnoreCase);
        Assert.True((await transfer.InspectAsync(file)).HasIdConflict);
        var second = (await transfer.ImportAsync(file))!;
        Assert.NotEqual(first.ProfileId, second.ProfileId);
        Assert.Null(await transfer.ImportAsync(file, ProfileImportConflictChoice.Cancel));
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task JsonImportRejectsAssembliesAndOversizedFilesBeforeInstallation()
    {
        using var fixture = new Fixture();
        var transfer = fixture.Provider.GetRequiredService<IProfileTransferService>();
        var assembly = Path.Combine(fixture.Root, "assembly.json");
        await File.WriteAllTextAsync(assembly, "{\"kind\":\"Assembly\",\"id\":\"unsafe\",\"entryAssembly\":\"anything.dll\"}");
        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.ImportAsync(assembly));
        var huge = Path.Combine(fixture.Root, "huge.json");
        await File.WriteAllTextAsync(huge, new string(' ', (int)ProfileManifestLimits.MaximumLengthBytes + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => transfer.InspectAsync(huge));
        Assert.Empty(await fixture.Provider.GetRequiredService<IProfileAuthoringService>().ListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JsonImportRollsBackFileAndCatalogIfPublisherFailsAfterSaving(bool replacing)
    {
        using var fixture = new Fixture();
        var catalog = fixture.Provider.GetRequiredService<IProfileCatalog>();
        var publisher = fixture.Provider.GetRequiredService<IProfileCatalogPublisher>();
        var original = Manifest(); original.DisplayName = "Original";
        if (replacing) Assert.True((await fixture.Provider.GetRequiredService<IProfileAuthoringService>().SaveAndApplyAsync(original, null)).Applied);
        var failingAuthoring = new ProfileAuthoringService(fixture.Paths,
            fixture.Provider.GetRequiredService<IProfileDefinitionCompiler>(), catalog, new ThrowingPublisher(publisher),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProfileAuthoringService>.Instance);
        var transfer = new ProfileTransferService(failingAuthoring, catalog, publisher);
        var incoming = Manifest(); incoming.DisplayName = "Incoming";
        await Assert.ThrowsAsync<InvalidOperationException>(() => transfer.ImportManifestAsync(incoming,
            replacing ? ProfileImportConflictChoice.Replace : ProfileImportConflictChoice.NewProfile));
        var file = Path.Combine(fixture.Paths.UserProfilesDirectory, incoming.Id!, "profile.json");
        if (replacing)
        {
            Assert.Equal("Original", Assert.Single(catalog.Current.Profiles).Descriptor.DisplayName);
            Assert.Contains("Original", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
        }
        else { Assert.False(File.Exists(file)); Assert.Empty(catalog.Current.Profiles); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackageRoundTripIsOfflineReadOnlyRelinkableAndNotesAreExplicit(bool notes)
    {
        using var fixture = new Fixture();
        var export = await fixture.CreateExportAsync(notes);
        var service = fixture.Provider.GetRequiredService<IIndexedDatasetTransferService>();
        await service.ExportAsync(export);
        var inspection = await service.InspectAsync(export.DestinationPath);
        Assert.True(inspection.HasCatalog);
        Assert.Equal(notes, inspection.IncludesUserInputs);
        var targetRoot = Path.Combine(fixture.Root, "receiver-does-not-exist");
        var source = (await service.ImportAsync(export.DestinationPath, ProfileImportConflictChoice.NewProfile, targetRoot))!;
        Assert.False(Directory.Exists(targetRoot));
        Assert.True(source.IsReadOnly);
        Assert.True(source.HasCatalog);
        Assert.NotEqual(export.Profile.Id, source.ProfileId);
        using (var projection = (IImportedCatalogProjectionStore)service.OpenCatalogStore(source))
        {
            await using var query = projection.OpenImportedQuery(new());
            var row = Assert.Single((await query.ReadRangeAsync(0, 10)).Items);
            Assert.Equal(Path.Combine(targetRoot, "item"), row.FullPath);
            Assert.Equal(Path.Combine(export.RootPath, "item"), row.OriginalFullPath);
            Assert.Equal(row.OriginalFullPath, row.StableKey);
            Assert.Equal(notes ? "PRIVATE NOTE" : "Default", row.Values["memo"]);
            Assert.EndsWith("FULL TEXT TAIL", Assert.IsType<string>(row.Values["content"]));
        }
        var archivedCatalog = Path.Combine(source.DirectoryPath, "archive", "catalog.db");
        var archiveBytes = await File.ReadAllBytesAsync(archivedCatalog);
        var newRoot = Path.Combine(fixture.Root, "different-offline-root");
        var relinked = await service.RelinkAsync(source, newRoot);
        Assert.Equal(1, relinked.LinkRevision);
        using (var projection = (IImportedCatalogProjectionStore)service.OpenCatalogStore(relinked))
        {
            await using var query = projection.OpenImportedQuery(new());
            Assert.Equal(Path.Combine(newRoot, "item"), Assert.Single((await query.ReadRangeAsync(0, 10)).Items).FullPath);
        }
        Assert.Equal(archiveBytes, await File.ReadAllBytesAsync(archivedCatalog));
        var listed = Assert.Single(await fixture.Provider.GetRequiredService<IImportedWorkspaceRegistry>().ListAsync());
        Assert.Equal(relinked.EffectiveRoot, listed.EffectiveRoot);
        await using var fileSession = new FindEverythingIndexSessionFactory().Create(relinked.MetadataDatabasePath);
        var files = await fileSession.SearchEntriesAsync(new(newRoot) { Keyword = "metadata", Limit = 20 });
        Assert.Equal(Path.Combine(newRoot, "item", "metadata.txt"), Assert.Single(files.Entries).FullPath);
    }

    [Fact]
    public async Task MetadataOnlyPackageImportsWithoutInventingStructuredData()
    {
        using var fixture = new Fixture();
        var export = (await fixture.CreateExportAsync(false)) with { CatalogSnapshot = null, CatalogDatabasePath = null };
        var service = fixture.Provider.GetRequiredService<IIndexedDatasetTransferService>();
        await service.ExportAsync(export);
        Assert.False((await service.InspectAsync(export.DestinationPath)).HasCatalog);
        var source = (await service.ImportAsync(export.DestinationPath, ProfileImportConflictChoice.NewProfile))!;
        Assert.False(source.HasCatalog);
        Assert.Null(source.CatalogSnapshot);
        Assert.Throws<InvalidOperationException>(() => service.OpenCatalogStore(source));
        Assert.True(File.Exists(source.MetadataDatabasePath));
    }

    [Theory]
    [InlineData("../profile.json")]
    [InlineData("nested/profile.json")]
    [InlineData("profile.json")]
    [InlineData("anything.dll")]
    public async Task ArchiveRejectsTraversalDuplicatesAndUnknownEntries(string unsafeName)
    {
        using var fixture = new Fixture();
        var export = await fixture.CreateExportAsync(false);
        var service = fixture.Provider.GetRequiredService<IIndexedDatasetTransferService>();
        await service.ExportAsync(export);
        using (var zip = ZipFile.Open(export.DestinationPath, ZipArchiveMode.Update))
        {
            var entry = zip.CreateEntry(unsafeName);
            using var writer = new StreamWriter(entry.Open()); writer.Write("bad");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InspectAsync(export.DestinationPath));
        Assert.Empty(await fixture.Provider.GetRequiredService<IImportedWorkspaceRegistry>().ListAsync());
    }

    [Fact]
    public async Task DamagedEntryIsRejectedByChecksum()
    {
        using var fixture = new Fixture();
        var export = await fixture.CreateExportAsync(false);
        var service = fixture.Provider.GetRequiredService<IIndexedDatasetTransferService>();
        await service.ExportAsync(export);
        using (var zip = ZipFile.Open(export.DestinationPath, ZipArchiveMode.Update))
        {
            zip.GetEntry("profile.json")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("profile.json").Open()); writer.Write("{}");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InspectAsync(export.DestinationPath));
    }

    [Fact]
    public async Task CancellationAfterProfileInstallRestoresExistingManifestAndDoesNotRegisterData()
    {
        using var fixture = new Fixture();
        var export = await fixture.CreateExportAsync(false);
        var authoring = fixture.Provider.GetRequiredService<IProfileAuthoringService>();
        var saved = await authoring.LoadAsync(Assert.Single(await authoring.ListAsync()));
        saved!.DisplayName = "ORIGINAL LOCAL NAME";
        Assert.True((await authoring.SaveAndApplyAsync(saved, saved.Id)).Applied);
        var service = fixture.Provider.GetRequiredService<IIndexedDatasetTransferService>();
        await service.ExportAsync(export);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(_ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportAsync(export.DestinationPath,
            ProfileImportConflictChoice.Replace, progress: progress, cancellationToken: cancellation.Token));
        Assert.Equal("ORIGINAL LOCAL NAME", (await authoring.LoadAsync(Assert.Single(await authoring.ListAsync())))!.DisplayName);
        Assert.Empty(await fixture.Provider.GetRequiredService<IImportedWorkspaceRegistry>().ListAsync());
    }

    [Fact]
    public async Task ExportRejectsMismatchedScanIdsAndLeavesExistingDestinationUnchanged()
    {
        using var fixture = new Fixture();
        var export = await fixture.CreateExportAsync(false);
        await File.WriteAllTextAsync(export.DestinationPath, "existing file");
        var snapshot = export.CatalogSnapshot! with { Scope = export.CatalogSnapshot!.Scope with { IndexScanId = Guid.NewGuid() } };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Provider.GetRequiredService<IIndexedDatasetTransferService>().ExportAsync(export with { CatalogSnapshot = snapshot }));
        Assert.Equal("existing file", await File.ReadAllTextAsync(export.DestinationPath));
    }

    [Fact]
    public async Task LocaleRestartRebuildsSearchTextFromStoredDatesWithoutChangingArchiveOrReadingSourceFiles()
    {
        using var fixture = new Fixture();
        var definition = Manifest();
        definition.Fields!.Add(new() { FieldId = "when", GroupName = "when", Header = "When",
            Kind = ProfileFieldValueKind.DateTime, ParseFormat = "yyyyMMdd", DisplayFormat = "MMMM" });
        definition.Rules![0].Pattern = @"(?<name>.+)(?<when>\d{8})";
        var date = new DateTime(2026, 5, 21);
        var export = await fixture.CreateExportAsync(false, definition, new Dictionary<string, object?> { ["when"] = date }, "ko-KR");
        var korean = fixture.Provider.GetRequiredService<IIndexedDatasetTransferService>();
        await korean.ExportAsync(export);
        var receiverRoot = Path.Combine(fixture.Root, "missing-receiver-root");
        var source = (await korean.ImportAsync(export.DestinationPath, ProfileImportConflictChoice.NewProfile, receiverRoot))!;
        Assert.Equal("ko-KR", source.CatalogSnapshot!.Scope.CultureName);
        var archivedCatalog = Path.Combine(source.DirectoryPath, "archive", "catalog.db");
        var originalArchive = await File.ReadAllBytesAsync(archivedCatalog);
        using (var store = (IImportedCatalogProjectionStore)korean.OpenCatalogStore(source))
        {
            await using var query = store.OpenImportedQuery(new(date.ToString("MMMM", CultureInfo.GetCultureInfo("ko-KR"))));
            Assert.Equal(1, await query.CountAsync());
        }
        Directory.Delete(export.RootPath, recursive: true);
        Assert.False(Directory.Exists(receiverRoot));
        using var english = new IndexedDatasetTransferService(fixture.Paths,
            fixture.Provider.GetRequiredService<ProfileTransferService>(), fixture.Provider.GetRequiredService<IProfilePresentationStore>(),
            Options.Create(new LocalizationOptions { CultureName = "en-US" }));
        var updated = await english.EnsureCultureAsync(source);
        Assert.Equal(1, updated.LinkRevision);
        Assert.Equal("en-US", updated.CatalogSnapshot!.Scope.CultureName);
        Assert.Equal(receiverRoot, updated.EffectiveRoot);
        using (var store = (IImportedCatalogProjectionStore)english.OpenCatalogStore(updated))
        {
            await using var query = store.OpenImportedQuery(new(date.ToString("MMMM", CultureInfo.GetCultureInfo("en-US"))));
            Assert.Equal(1, await query.CountAsync());
            Assert.Equal(date, Assert.Single((await query.ReadRangeAsync(0, 10)).Items).Values["when"]);
            await using var previousLocale = store.OpenImportedQuery(new(date.ToString("MMMM", CultureInfo.GetCultureInfo("ko-KR"))));
            Assert.Equal(0, await previousLocale.CountAsync());
        }
        Assert.Same(updated, await english.EnsureCultureAsync(updated));
        Assert.Equal(originalArchive, await File.ReadAllBytesAsync(archivedCatalog));
    }

    [Theory]
    [InlineData("export")]
    [InlineData("import")]
    [InlineData("relink")]
    public async Task StagingSetupFailureAlwaysReleasesTransferGate(string operation)
    {
        using var fixture = new Fixture();
        var request = await fixture.CreateExportAsync(false);
        var service = fixture.Provider.GetRequiredService<IIndexedDatasetTransferService>();
        await service.ExportAsync(request);
        var source = operation == "relink" ? (await service.ImportAsync(request.DestinationPath, ProfileImportConflictChoice.NewProfile))! : null;
        var imports = Path.Combine(fixture.Paths.LocalDataDirectory, "Imports");
        var backup = Path.Combine(fixture.Paths.LocalDataDirectory, "Imports-test-backup");
        Directory.Move(imports, backup);
        await File.WriteAllTextAsync(imports, "A test-owned file prevents creating the staging directory.");
        async Task RunOperation()
        {
            if (operation == "export") await service.ExportAsync(request);
            else if (operation == "import") await service.ImportAsync(request.DestinationPath, ProfileImportConflictChoice.NewProfile);
            else await service.RelinkAsync(source!, Path.Combine(fixture.Root, "receiver"));
        }
        try { await Assert.ThrowsAnyAsync<IOException>(RunOperation); }
        finally { File.Delete(imports); Directory.Move(backup, imports); }
        await RunOperation().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task InvalidExportDestinationDoesNotAcquireOrStrandTheTransferGate()
    {
        using var fixture = new Fixture();
        var request = await fixture.CreateExportAsync(false);
        var service = fixture.Provider.GetRequiredService<IIndexedDatasetTransferService>();
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExportAsync(request with { DestinationPath = "\0" }));
        await service.ExportAsync(request).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WindowsStagingCleanupFailureDoesNotFailCommittedExportOrStrandGate()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows open-file delete semantics are the deterministic failure injection.
        using var fixture = new Fixture();
        var request = await fixture.CreateExportAsync(false);
        var service = fixture.Provider.GetRequiredService<IIndexedDatasetTransferService>();
        FileStream? heldStagingFile = null;
        var imports = Path.Combine(fixture.Paths.LocalDataDirectory, "Imports");
        try
        {
            var progress = new InlineProgress(_ =>
            {
                if (heldStagingFile is not null) return;
                var stage = Directory.EnumerateDirectories(imports, ".stage-*").Single();
                heldStagingFile = new FileStream(Path.Combine(stage, "profile.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            });
            await service.ExportAsync(request, progress);
            Assert.NotNull(heldStagingFile);
            Assert.True(File.Exists(request.DestinationPath));
            heldStagingFile.Dispose(); heldStagingFile = null;
            await service.ExportAsync(request).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { heldStagingFile?.Dispose(); }
    }

    [Fact]
    public async Task PackageCallbackFailureRestoresExactOriginalJsonIncludingCommentsAndUnknownProperties()
    {
        using var fixture = new Fixture();
        var authoring = fixture.Provider.GetRequiredService<IProfileAuthoringService>();
        var original = Manifest(); original.DisplayName = "Original";
        Assert.True((await authoring.SaveAndApplyAsync(original, null)).Applied);
        var editable = Assert.Single(await authoring.ListAsync());
        var text = await File.ReadAllTextAsync(editable.ManifestPath);
        var commented = "/* preserve this user note */\n" + text[..text.LastIndexOf('}')] + ",\n\"userNote\":\"preserve this unknown property\"\n}";
        await File.WriteAllTextAsync(editable.ManifestPath, commented);
        var before = await File.ReadAllBytesAsync(editable.ManifestPath);
        var incoming = Manifest(); incoming.DisplayName = "Incoming";
        var transfer = fixture.Provider.GetRequiredService<ProfileTransferService>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer.InstallAtomicallyAsync<bool>(incoming,
            ProfileImportConflictChoice.Replace, _ => Task.FromException<bool>(new OperationCanceledException()), CancellationToken.None));
        Assert.Equal(before, await File.ReadAllBytesAsync(editable.ManifestPath));
        Assert.Equal("Original", Assert.Single(fixture.Provider.GetRequiredService<IProfileCatalog>().Current.Profiles).Descriptor.DisplayName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackageRollbackNeverOverwritesOrDeletesExternalProfileEdits(bool replacing)
    {
        using var fixture = new Fixture();
        var authoring = fixture.Provider.GetRequiredService<IProfileAuthoringService>();
        if (replacing) Assert.True((await authoring.SaveAndApplyAsync(Manifest(), null)).Applied);
        var transfer = fixture.Provider.GetRequiredService<ProfileTransferService>();
        string? changedPath = null;
        byte[]? externalBytes = null;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => transfer.InstallAtomicallyAsync<bool>(Manifest(),
            replacing ? ProfileImportConflictChoice.Replace : ProfileImportConflictChoice.NewProfile,
            async result =>
            {
                var external = Manifest(); external.Id = result.ProfileId; external.DisplayName = "External editor owns this change";
                externalBytes = JsonSerializer.SerializeToUtf8Bytes(external, ProfileTransferService.WriteOptions);
                changedPath = result.ManifestPath;
                await File.WriteAllBytesAsync(changedPath, externalBytes);
                throw new OperationCanceledException();
            }, CancellationToken.None));
        Assert.Contains("externally", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(changedPath);
        Assert.Equal(externalBytes, await File.ReadAllBytesAsync(changedPath));
        Assert.Equal("External editor owns this change", Assert.Single(fixture.Provider.GetRequiredService<IProfileCatalog>().Current.Profiles).Descriptor.DisplayName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackageRejectsInconsistentProfileColumnTypeOrMappingEvenWhenChecksumsAreValid(bool changeMapping)
    {
        using var fixture = new Fixture();
        var request = await fixture.CreateExportAsync(false);
        var service = fixture.Provider.GetRequiredService<IIndexedDatasetTransferService>();
        await service.ExportAsync(request);
        using (var zip = ZipFile.Open(request.DestinationPath, ZipArchiveMode.Update))
        {
            var profileEntry = zip.GetEntry("profile.json")!;
            ProfileManifest changed;
            await using (var stream = profileEntry.Open())
                changed = (await JsonSerializer.DeserializeAsync<ProfileManifest>(stream, ProfileTransferService.ReadOptions))!;
            if (changeMapping) changed.Fields![0].ValueMappings = [new() { Source = "item", Display = "Forged display" }];
            else changed.Fields![0].Kind = ProfileFieldValueKind.Int32;
            var replacement = JsonSerializer.SerializeToUtf8Bytes(changed, ProfileTransferService.WriteOptions);
            profileEntry.Delete();
            await using (var stream = zip.CreateEntry("profile.json").Open()) await stream.WriteAsync(replacement);
            var manifestEntry = zip.GetEntry("manifest.json")!;
            JsonNode manifest;
            await using (var stream = manifestEntry.Open()) manifest = (await JsonNode.ParseAsync(stream))!;
            var file = manifest["files"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "profile.json")!;
            file["length"] = replacement.LongLength;
            file["sha256"] = Convert.ToHexString(SHA256.HashData(replacement));
            manifestEntry.Delete();
            await using (var stream = zip.CreateEntry("manifest.json").Open())
                await JsonSerializer.SerializeAsync(stream, manifest);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InspectAsync(request.DestinationPath));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(request.DestinationPath, ProfileImportConflictChoice.Replace));
        Assert.Empty(await fixture.Provider.GetRequiredService<IImportedWorkspaceRegistry>().ListAsync());
        var authoring = fixture.Provider.GetRequiredService<IProfileAuthoringService>();
        var editable = Assert.Single(await authoring.ListAsync());
        Assert.Equal(ProfileFieldValueKind.String, (await authoring.LoadAsync(editable))!.Fields![0].Kind);
    }

    private sealed class InlineProgress(Action<WorkspaceTransferProgress> report) : IProgress<WorkspaceTransferProgress>
    { public void Report(WorkspaceTransferProgress value) => report(value); }
    private sealed class ThrowingPublisher(IProfileCatalogPublisher inner) : IProfileCatalogPublisher
    {
        public void Publish(ProfileCatalogSnapshot snapshot) { inner.Publish(snapshot); throw new InvalidOperationException("Injected publication failure."); }
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public AppPaths Paths { get; }
        public ServiceProvider Provider { get; }
        public Fixture()
        {
            var temp = Path.GetFullPath(Path.GetTempPath());
            if (OperatingSystem.IsMacOS() && temp.StartsWith("/var/", StringComparison.Ordinal)) temp = "/private" + temp;
            Root = Path.Combine(temp, "fe-workspace-transfer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Paths = new(Path.Combine(Root, "local"), Path.Combine(Root, "local", "settings.json"));
            var services = new ServiceCollection();
            services.AddLogging(); services.AddProfileRuntime();
            services.Configure<PluginDiscoveryOptions>(o => { o.ProfilesDirectory = Path.Combine(Root, "bundled"); o.UserProfilesDirectory = Paths.UserProfilesDirectory; o.ContractMajor = ProfileContract.CurrentMajor; });
            services.Configure<LocalizationOptions>(o => o.CultureName = "ko-KR");
            services.AddSingleton(Paths);
            services.AddSingleton<IProfileAuthoringService, ProfileAuthoringService>();
            services.AddSingleton<ProfileTransferService>();
            services.AddSingleton<IProfileTransferService>(p => p.GetRequiredService<ProfileTransferService>());
            services.AddSingleton<GridLayoutStore>();
            services.AddSingleton<IGridLayoutStore>(p => p.GetRequiredService<GridLayoutStore>());
            services.AddSingleton<IProfilePresentationStore, ProfilePresentationStore>();
            services.AddSingleton<ISelectionOutputFormatStore, SelectionOutputFormatStore>();
            services.AddSingleton<IProfileOutputPreferenceStore, ProfileOutputPreferenceStore>();
            services.AddSingleton<ISelectionOutputFormatter, SelectionOutputFormatter>();
            services.AddSingleton<IProfileWorkspaceSettingsService, ProfileWorkspaceSettingsService>();
            services.AddSingleton<IndexedDatasetTransferService>();
            services.AddSingleton<IIndexedDatasetTransferService>(p => p.GetRequiredService<IndexedDatasetTransferService>());
            services.AddSingleton<IImportedWorkspaceRegistry>(p => p.GetRequiredService<IndexedDatasetTransferService>());
            Provider = services.BuildServiceProvider();
        }
        public async Task<WorkspacePackageExportRequest> CreateExportAsync(bool notes, ProfileManifest? definition = null,
            IReadOnlyDictionary<string, object?>? extraValues = null, string cultureName = "en-US")
        {
            var profile = definition ?? Manifest();
            var saved = await Provider.GetRequiredService<IProfileAuthoringService>().SaveAndApplyAsync(profile, null);
            Assert.True(saved.Applied);
            var source = Path.Combine(Root, "source");
            Directory.CreateDirectory(Path.Combine(source, "item"));
            await File.WriteAllTextAsync(Path.Combine(source, "item", "metadata.txt"), "source content");
            var metadata = Path.Combine(Root, "metadata.db");
            IndexScanReport scan;
            await using (var session = new FindEverythingIndexSessionFactory().Create(metadata))
                scan = await session.ScanAsync(new(source) { Options = new IndexScanOptions { DirectoryDelay = TimeSpan.Zero, MaxEntriesPerSecond = 100000 } });
            var catalog = Path.Combine(Root, "results.db");
            using var store = new SqliteCatalogProjectionStore(catalog);
            var scope = new CatalogProjectionScope("scope", profile.Id!, source, metadata,
                Provider.GetRequiredService<ProfileTransferService>().GetProjectionFingerprint(profile), cultureName, scan.ScanId);
            using var writer = store.Begin(scope, saved.Review.Profile!.Descriptor.Fields);
            var values = new Dictionary<string, object?> { ["name"] = "item", ["content"] = new string('x', 5000) + "FULL TEXT TAIL" };
            if (extraValues is not null) foreach (var pair in extraValues) values[pair.Key] = pair.Value;
            writer.Append(new CatalogItem(Path.Combine(source, "item"), "item", "rule", values, values, false));
            var snapshot = writer.Publish(new(1, 1, 0, 0, 0, false, TimeSpan.Zero, DateTimeOffset.UtcNow));
            await store.SetValueAsync(profile.Id!, Path.Combine(source, "item"), "memo", "PRIVATE NOTE");
            return new(profile, metadata, source, Path.Combine(Root, "shared.fepkg"))
            { CatalogDatabasePath = catalog, CatalogSnapshot = snapshot, IncludeUserInputs = notes };
        }
        public void Dispose()
        {
            Provider.GetRequiredService<GridLayoutStore>().StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            Provider.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    private sealed class MemoryPresentation : IProfilePresentationStore
    {
        public event EventHandler<ProfilePresentationChangedEventArgs>? Changed { add { } remove { } }
        public Task<IReadOnlyDictionary<string, ColumnWidthPreset>> LoadAsync(string profileId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, ColumnWidthPreset>>(new Dictionary<string, ColumnWidthPreset>());
        public Task SaveAsync(string profileId, IReadOnlyDictionary<string, ColumnWidthPreset> fields, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private static ProfileManifest Manifest() => new()
    {
        ContractVersion = ProfileContract.CurrentMajor, Kind = ProfileKind.Declarative, Id = "shared-profile", Version = "1.0.0", DisplayName = "Shared profile",
        Fields = [new() { FieldId = "name", GroupName = "name", Header = "Name", Kind = ProfileFieldValueKind.String }],
        TextFileFields = [new() { FieldId = "content", Header = "Content", FileNamePattern = "metadata\\.txt" }],
        UserInputFields = [new() { FieldId = "memo", Header = "Memo", DefaultValue = "Default" }],
        Rules = [new() { Id = "rule", Pattern = "(?<name>.+)", MatchMode = ProfileRegexMatchMode.Full }],
    };
}
