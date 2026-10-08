using System.IO;
using FindEverything.Application.Catalog;
using FindEverything.Application.Indexing;
using FindEverything.Application.Options;
using FindEverything.Application.Profiles;
using FindEverything.Desktop.Hosting;
using FindEverything.Desktop.Localization;
using FindEverything.Desktop.Services;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Wpf.Ui.Controls;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class LocalizationInfrastructureTests
{
    [Theory]
    [InlineData("ko-KR", "ko-KR")]
    [InlineData("KO-kr", "ko-KR")]
    [InlineData("en-US", "en-US")]
    [InlineData("EN-us", "en-US")]
    public void Normalize_accepts_only_the_two_supported_cultures(
        string input,
        string expected) =>
        Assert.Equal(expected, LocalizationOptions.Normalize(input));

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("en")]
    [InlineData("")]
    public void Normalize_rejects_an_unsupported_culture(string input) =>
        Assert.ThrowsAny<ArgumentException>(() => LocalizationOptions.Normalize(input));

    [Fact]
    public void Bootstrapper_selects_the_packaged_dictionary_for_the_requested_culture()
    {
        var uri = LocalizationBootstrapper.CreateDictionaryUri(
            LocalizationOptions.EnglishCultureName);

        Assert.Equal(
            "/FindEverything.Gui;component/Localization/Strings.en-US.xaml",
            uri.OriginalString);
    }

    [Fact]
    public void Literal_localization_includes_infobar_messages() =>
        Assert.True(LocalizationScope.IsLocalizable(InfoBar.MessageProperty));

    [Fact]
    public void Literal_localization_ignores_a_null_tree_target() =>
        LocalizationScope.TranslateObject(null);

    [Fact]
    public void Literal_localization_ignores_a_null_dependency_property() =>
        Assert.False(LocalizationScope.IsLocalizable(null));

    [Theory]
    [InlineData("ko-KR", "ko-KR", false)]
    [InlineData("ko-KR", "en-US", true)]
    [InlineData("en-US", "ko-KR", true)]
    public void Reloaded_locale_requests_restart_only_when_it_differs_from_applied_culture(
        string applied,
        string configured,
        bool expected) =>
        Assert.Equal(
            expected,
            FindEverything.Desktop.Configuration.SettingsReloadDiagnostics.RequiresRestart(
                applied,
                configured));

    [Fact]
    public void English_profile_diagnostics_never_fall_back_to_a_Korean_message()
    {
        var translated = ProfileDiagnosticLocalizer.Translate(
            new TestAppLocalizer("en-US"),
            new ProfilePathTemplateDiagnostic(
                "custom_template_problem",
                "경로 템플릿을 확인하세요."));

        Assert.DoesNotContain("경로", translated, StringComparison.Ordinal);
        Assert.Contains("custom_template_problem", translated, StringComparison.Ordinal);
    }

    [Fact]
    public void English_profile_mapping_issues_never_expose_the_Korean_runtime_message()
    {
        var translated = ProfileDiagnosticLocalizer.Translate(
            new TestAppLocalizer("en-US"),
            new ProfileMappingIssue(
                "required_capture_missing",
                "client",
                "필수 값 'Client'을 찾을 수 없습니다."));

        Assert.DoesNotContain("필수 값", translated, StringComparison.Ordinal);
        Assert.Contains("required_capture_missing", translated, StringComparison.Ordinal);
    }

    [Fact]
    public void English_user_facing_errors_translate_app_owned_exception_types()
    {
        var localizer = new TestAppLocalizer(
            "en-US",
            new Dictionary<string, string>
            {
                ["Loc.Common.TryAfterOperation"] =
                    "Try again after the current operation finishes.",
                ["Loc.Path.Error.MappedDriveResolutionFailed"] =
                    "Could not convert mapped network path '{0}' to a UNC path.",
                ["Loc.Language.ChangeFailed.Message"] =
                    "The language setting could not be saved. Check the user settings file and try again.",
                ["Loc.Catalog.Error.IndexDatabaseMissing"] =
                    "Index database '{0}' does not exist. Run 'Index and load' or choose the correct database.",
                ["Loc.Catalog.Error.RootNotIndexed"] =
                    "Database '{1}' has no index registered for the exact search root '{0}'. Run 'Index and load' with the same search location.",
            });
        var mappedDriveException = new MappedDrivePathResolutionException(
            @"Z:\Projects",
            2250,
            "공급자 원시 상세");

        var mappedDriveMessage = UserFacingExceptionLocalizer.TranslateOperationFailure(
            localizer,
            mappedDriveException);
        var busyMessage = UserFacingExceptionLocalizer.TranslateOperationFailure(
            localizer,
            new ApplicationOperationBusyException(ApplicationOperationKind.ProfileWrite));
        var languageMessage = UserFacingExceptionLocalizer.TranslateLanguageChangeFailure(localizer);
        var missingIndexMessage = UserFacingExceptionLocalizer.TranslateOperationFailure(
            localizer,
            new CatalogIndexUnavailableException(
                IndexRootAvailability.DatabaseMissing,
                @"C:\Index\metadata.db",
                @"D:\Archive"));
        var wrongRootMessage = UserFacingExceptionLocalizer.TranslateOperationFailure(
            localizer,
            new CatalogIndexUnavailableException(
                IndexRootAvailability.RootNotIndexed,
                @"C:\Index\metadata.db",
                @"D:\Archive"));

        Assert.Equal(
            "Could not convert mapped network path 'Z:\\Projects' to a UNC path.",
            mappedDriveMessage);
        Assert.Equal("Try again after the current operation finishes.", busyMessage);
        Assert.Equal(
            "The language setting could not be saved. Check the user settings file and try again.",
            languageMessage);
        Assert.Contains("C:\\Index\\metadata.db", missingIndexMessage, StringComparison.Ordinal);
        Assert.Contains("D:\\Archive", wrongRootMessage, StringComparison.Ordinal);
        Assert.Contains("exact search root", wrongRootMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("공급자", mappedDriveMessage, StringComparison.Ordinal);
        Assert.Contains("공급자 원시 상세", mappedDriveException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Korean_user_facing_errors_remain_localized()
    {
        var localizer = new TestAppLocalizer(
            "ko-KR",
            new Dictionary<string, string>
            {
                ["Loc.Common.TryAfterOperation"] =
                    "현재 작업이 끝난 뒤 다시 시도하세요.",
                ["Loc.Path.Error.MappedDriveResolutionFailed"] =
                    "매핑된 네트워크 경로 '{0}'를 UNC 경로로 변환하지 못했습니다.",
                ["Loc.Language.ChangeFailed.Message"] =
                    "언어 설정을 저장하지 못했습니다. 사용자 설정 파일을 확인한 뒤 다시 시도하세요.",
            });

        Assert.Contains(
            "매핑된 네트워크 경로",
            UserFacingExceptionLocalizer.TranslateOperationFailure(
                localizer,
                new MappedDrivePathResolutionException(@"Z:\Projects")),
            StringComparison.Ordinal);
        Assert.Equal(
            "현재 작업이 끝난 뒤 다시 시도하세요.",
            UserFacingExceptionLocalizer.TranslateOperationFailure(
                localizer,
                new ApplicationOperationBusyException(ApplicationOperationKind.IndexWrite)));
        Assert.Contains(
            "언어 설정",
            UserFacingExceptionLocalizer.TranslateLanguageChangeFailure(localizer),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Bundled_sample_project_text_does_not_ship_Korean_copy()
    {
        var sampleRoot = Path.Combine(
            AppContext.BaseDirectory,
            "SampleData",
            "sample-projects");
        Assert.True(Directory.Exists(sampleRoot), $"Missing sample data: {sampleRoot}");

        var textFiles = Directory
            .EnumerateFiles(sampleRoot, "*", SearchOption.AllDirectories)
            .Where(static path =>
                Path.GetExtension(path) is ".md" or ".txt")
            .ToArray();
        Assert.NotEmpty(textFiles);
        foreach (var path in textFiles)
        {
            Assert.DoesNotMatch("[가-힣]", File.ReadAllText(path));
        }
    }

    [Fact]
    public void Restart_is_launched_once_and_only_after_shutdown_has_completed()
    {
        var lifetime = new RecordingLifetimeController();
        var launcher = new RecordingProcessLauncher();
        var coordinator = new ApplicationRestartCoordinator(
            lifetime,
            launcher,
            NullLogger<ApplicationRestartCoordinator>.Instance);

        coordinator.RestartAfterShutdownIfRequested();
        Assert.Equal(0, launcher.LaunchCount);

        coordinator.RequestRestart();
        coordinator.RequestRestart();
        Assert.True(coordinator.IsRestartRequested);
        Assert.Equal(1, lifetime.ShutdownCount);
        Assert.Equal(0, launcher.LaunchCount);

        coordinator.RestartAfterShutdownIfRequested();
        coordinator.RestartAfterShutdownIfRequested();
        Assert.Equal(1, launcher.LaunchCount);
    }

    [Fact]
    public void Failed_shutdown_does_not_leave_restart_stuck_and_can_be_retried()
    {
        var lifetime = new FailOnceLifetimeController();
        var launcher = new RecordingProcessLauncher();
        var coordinator = new ApplicationRestartCoordinator(
            lifetime,
            launcher,
            NullLogger<ApplicationRestartCoordinator>.Instance);

        Assert.Throws<InvalidOperationException>(coordinator.RequestRestart);
        Assert.False(coordinator.IsRestartRequested);
        Assert.Equal(0, launcher.LaunchCount);

        coordinator.RequestRestart();
        Assert.True(coordinator.IsRestartRequested);
        Assert.Equal(2, lifetime.ShutdownCount);

        coordinator.RestartAfterShutdownIfRequested();
        Assert.Equal(1, launcher.LaunchCount);
    }

    [Fact]
    public async Task Language_change_saves_only_locale_before_requesting_restart()
    {
        var events = new List<string>();
        var writer = new RecordingSettingsWriter(events);
        var restart = new RecordingRestartCoordinator(events);
        var service = new LanguageSelectionService(
            new TestAppLocalizer(LocalizationOptions.KoreanCultureName),
            writer,
            restart);

        Assert.True(await service.ChangeAsync("EN-us"));

        Assert.Equal(["save", "restart"], events);
        var update = Assert.IsType<UserSettingsUpdate>(writer.Update);
        Assert.Null(update.Workspace);
        Assert.Null(update.Indexing);
        Assert.Null(update.Appearance);
        var localization = Assert.IsType<LocalizationOptions>(update.Localization);
        Assert.Equal(
            LocalizationOptions.EnglishCultureName,
            localization.CultureName);
        Assert.True(restart.IsRestartRequested);
    }

    [Fact]
    public async Task Selecting_the_current_language_does_not_write_or_restart()
    {
        var events = new List<string>();
        var writer = new RecordingSettingsWriter(events);
        var restart = new RecordingRestartCoordinator(events);
        var service = new LanguageSelectionService(
            new TestAppLocalizer(LocalizationOptions.EnglishCultureName),
            writer,
            restart);

        Assert.False(await service.ChangeAsync("en-US"));
        Assert.Empty(events);
        Assert.Null(writer.Update);
        Assert.False(restart.IsRestartRequested);
    }

    [Fact]
    public void Replacement_process_preserves_arguments_and_appends_selected_locale_last()
    {
        var context = new ApplicationLaunchContext(
            @"C:\Apps\FindEverything.Gui.exe",
            [
                "--Workspace:RootPath",
                @".\Data With Spaces",
                "--Localization:CultureName=ko-KR",
                "--flag=value",
            ],
            @"C:\Original Working Directory");

        var startInfo = CurrentApplicationProcessLauncher.CreateStartInfo(
            context.EntryCommandPath,
            context,
            LocalizationOptions.EnglishCultureName);

        Assert.Equal(context.EntryCommandPath, startInfo.FileName);
        Assert.Equal(context.WorkingDirectory, startInfo.WorkingDirectory);
        Assert.Equal(
            [
                .. context.Arguments,
                ApplicationLaunchContext.ManagedLocaleMarkerArgument,
                "--Localization:CultureName=en-US",
            ],
            startInfo.ArgumentList.ToArray());
        Assert.Equal(
            "--Localization:CultureName=en-US",
            startInfo.ArgumentList[^1]);
        var replacementConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Localization:CultureName"] = LocalizationOptions.KoreanCultureName,
            })
            .AddCommandLine(startInfo.ArgumentList.ToArray())
            .Build();
        Assert.Equal(
            LocalizationOptions.EnglishCultureName,
            replacementConfiguration["Localization:CultureName"]);
    }

    [Fact]
    public void Framework_dependent_restart_preserves_relative_dll_arguments_and_working_directory()
    {
        var context = new ApplicationLaunchContext(
            @".\publish\FindEverything.Gui.dll",
            ["--Workspace:RootPath=.\\Data", "--Workspace:DatabasePath=.\\Data\\index.db"],
            @"C:\Original Checkout");

        var startInfo = CurrentApplicationProcessLauncher.CreateStartInfo(
            @"C:\Program Files\dotnet\dotnet.exe",
            context,
            LocalizationOptions.KoreanCultureName);

        Assert.Equal(@"C:\Original Checkout", startInfo.WorkingDirectory);
        Assert.Equal(
            [
                context.EntryCommandPath,
                .. context.Arguments,
                ApplicationLaunchContext.ManagedLocaleMarkerArgument,
                "--Localization:CultureName=ko-KR",
            ],
            startInfo.ArgumentList.ToArray());
    }

    [Fact]
    public void Launch_context_removes_only_the_previous_restart_managed_locale_pair()
    {
        var context = ApplicationLaunchContext.Capture(
            @".\FindEverything.Gui.dll",
            [
                "--Localization:CultureName=en-US",
                "--flag=value",
                ApplicationLaunchContext.ManagedLocaleMarkerArgument,
                "--Localization:CultureName=ko-KR",
            ],
            @"D:\Workspace");

        Assert.Equal(
            ["--Localization:CultureName=en-US", "--flag=value"],
            context.Arguments);
        Assert.Equal(@"D:\Workspace", context.WorkingDirectory);
    }

    private sealed class RecordingSettingsWriter(List<string> events) : IUserSettingsWriter
    {
        public UserSettingsUpdate? Update { get; private set; }

        public Task SaveAsync(
            UserSettingsUpdate update,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("save");
            Update = update;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRestartCoordinator(List<string> events) :
        IApplicationRestartCoordinator
    {
        public bool IsRestartRequested { get; private set; }

        public void RequestRestart()
        {
            events.Add("restart");
            IsRestartRequested = true;
        }

        public void RestartAfterShutdownIfRequested()
        {
        }
    }

    private sealed class RecordingLifetimeController : IApplicationLifetimeController
    {
        public int ShutdownCount { get; private set; }

        public void Shutdown() => ShutdownCount++;
    }

    private sealed class FailOnceLifetimeController : IApplicationLifetimeController
    {
        public int ShutdownCount { get; private set; }

        public void Shutdown()
        {
            ShutdownCount++;
            if (ShutdownCount == 1)
            {
                throw new InvalidOperationException("shutdown failed");
            }
        }
    }

    private sealed class RecordingProcessLauncher : IApplicationProcessLauncher
    {
        public int LaunchCount { get; private set; }

        public void LaunchReplacement() => LaunchCount++;
    }
}
