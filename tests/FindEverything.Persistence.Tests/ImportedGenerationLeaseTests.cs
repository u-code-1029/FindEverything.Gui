using FindEverything.Infrastructure.Persistence;
using Xunit;

namespace FindEverything.Persistence.Tests;

public sealed class ImportedGenerationLeaseTests
{
    [WindowsFileSharingFact]
    public void Lazy_readers_hold_a_cross_process_compatible_shared_file_lease_until_all_are_disposed()
    {
        var root = Path.Combine(Path.GetTempPath(), "import-generation-lease-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var generation = Path.Combine(installed, "linked-0");
        Directory.CreateDirectory(generation);
        var marker = Path.Combine(generation, ImportedGenerationLease.MarkerFileName);
        File.WriteAllText(marker, ImportedGenerationLease.MarkerContent);
        try
        {
            var first = ImportedGenerationLease.Acquire(Path.Combine(generation, "catalog.db"));
            var second = ImportedGenerationLease.Acquire(Path.Combine(generation, "metadata.db"));
            try
            {
                Assert.Throws<IOException>(() => { using var blocked = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete); });
                first.Dispose();
                Assert.Throws<IOException>(() => { using var blocked = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete); });
                second.Dispose();
                using var exclusive = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
                Assert.Throws<IOException>(() => { using var blocked = ImportedGenerationLease.Acquire(Path.Combine(generation, "catalog.db")); });
            }
            finally { second.Dispose(); first.Dispose(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Each_reader_releases_its_own_lease_and_notifies_once_on_every_platform()
    {
        var root = Path.Combine(Path.GetTempPath(), "import-generation-release-" + Guid.NewGuid().ToString("N"));
        var generation = Path.Combine(root, Guid.NewGuid().ToString("N"), "linked-0");
        Directory.CreateDirectory(generation);
        var marker = Path.Combine(generation, ImportedGenerationLease.MarkerFileName);
        File.WriteAllText(marker, ImportedGenerationLease.MarkerContent);
        var releases = 0;
        void OnReleased(string directory) { if (directory == generation) releases++; }
        ImportedGenerationLease.Released += OnReleased;
        try
        {
            using var first = ImportedGenerationLease.Acquire(Path.Combine(generation, "catalog.db"));
            using var second = ImportedGenerationLease.Acquire(Path.Combine(generation, "metadata.db"));
            Assert.Equal(0, releases);
            first.Dispose(); first.Dispose();
            Assert.Equal(1, releases);
            Assert.Equal(ImportedGenerationLease.MarkerContent, File.ReadAllText(marker));
            second.Dispose(); second.Dispose();
            Assert.Equal(2, releases);
        }
        finally
        {
            ImportedGenerationLease.Released -= OnReleased;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Missing_retired_generation_is_not_silently_treated_as_an_unleased_legacy_database()
    {
        var root = Path.Combine(Path.GetTempPath(), "import-generation-lease-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(installed, "source.json"), "{}");
        try
        {
            Assert.Throws<IOException>(() => ImportedGenerationLease.Acquire(Path.Combine(installed, "linked-0", "catalog.db")));
            var legacy = Path.Combine(installed, "linked-1");
            Directory.CreateDirectory(legacy);
            using var lease = ImportedGenerationLease.Acquire(Path.Combine(legacy, "catalog.db"));
            Assert.False(File.Exists(Path.Combine(legacy, ImportedGenerationLease.MarkerFileName)));
        }
        finally { Directory.Delete(root, true); }
    }
}

public sealed class WindowsFileSharingFactAttribute : FactAttribute
{
    public WindowsFileSharingFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows FileShare.Delete exclusion is not equivalent to Unix file-sharing semantics; run this lock check on Windows.";
    }
}
