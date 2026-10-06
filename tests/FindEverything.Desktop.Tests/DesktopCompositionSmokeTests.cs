using System.IO;
using System.Windows;
using FindEverything.Application;
using FindEverything.Desktop;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.Views;
using FindEverything.Desktop.Views.Pages;
using FindEverything.Infrastructure.FindEverything;
using FindEverything.Profile.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class DesktopCompositionSmokeTests
{
    [Fact]
    public async Task Shell_pages_and_navigation_resolve_on_an_sta_thread()
    {
        var completion = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunSmokeTest(completion))
        {
            IsBackground = true,
            Name = "FindEverything WPF smoke test",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var exception = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Null(exception);
    }

    private static void RunSmokeTest(TaskCompletionSource<Exception?> completion)
    {
        App? application = null;
        MainWindow? window = null;
        Exception? failure = null;
        try
        {
            application = new App();
            application.InitializeComponent();

            var configuration = new ConfigurationManager();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services
                .AddFindEverythingApplication(configuration)
                .AddFindEverythingInfrastructure()
                .AddProfileRuntime()
                .AddDesktopPresentation(
                    configuration,
                    new AppPaths(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "findeverything-smoke.json")));

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            window = provider.GetRequiredService<MainWindow>();
            Assert.NotNull(provider.GetRequiredService<CatalogPage>());
            Assert.NotNull(provider.GetRequiredService<ProfilesPage>());
            Assert.NotNull(provider.GetRequiredService<SettingsPage>());

            window.Show();
            var navigation = provider.GetRequiredService<INavigationService>();
            Assert.True(navigation.Navigate(typeof(CatalogPage)));
            Assert.True(navigation.Navigate(typeof(ProfilesPage)));
            Assert.True(navigation.Navigate(typeof(SettingsPage)));
            window.Close();
            window = null;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            try
            {
                window?.Close();
                application?.Shutdown();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            completion.TrySetResult(failure);
        }
    }
}
