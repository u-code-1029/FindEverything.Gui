using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FindEverything.Desktop.Configuration;
using FindEverything.Desktop.ViewModels;
using FindEverything.Desktop.Views.Controls;
using FindEverything.Desktop.Views.Pages;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Ui = Wpf.Ui.Controls;

namespace FindEverything.Desktop.Tests;

/// <summary>Runs on the composition smoke test's existing application/STA; never opens an OS picker.</summary>
internal static class MultiRootEntryPathsUiChecks
{
    public static void Verify(IServiceProvider services, Action<FrameworkElement, string>? capture = null)
    {
        VerifyCatalog(services, capture);
        VerifyProfile(capture);
    }

    private static void VerifyCatalog(IServiceProvider services, Action<FrameworkElement, string>? capture)
    {
        var fixture = new EntryListFixture();
        var page = new CatalogPage(services.GetRequiredService<CatalogViewModel>(),
            services.GetRequiredService<ScanConsoleViewModel>(), services.GetRequiredService<IGridLayoutStore>())
        { DataContext = fixture };
        var host = new Window { Width = 760, Height = 680, Content = page, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            host.Show(); Pump(host);
            var details = Assert.IsAssignableFrom<Ui.CardControl>(page.FindName("CatalogDetailsPanel"));
            Assert.True(details.IsVisible);
            var entryCard = Assert.IsAssignableFrom<Ui.CardControl>(page.FindName("CatalogEntryPathsCard"));
            var databaseCard = Assert.IsAssignableFrom<Ui.CardControl>(page.FindName("CatalogDatabaseCard"));
            Assert.NotSame(entryCard, databaseCard);
            var list = Assert.Single(Descendants(details).OfType<ItemsControl>(), candidate =>
                BindingOperations.GetBinding(candidate, ItemsControl.ItemsSourceProperty)?.Path.Path == "EntryPaths");
            Assert.Equal(fixture.EntryPaths.ToArray(), list.Items.Cast<string>().ToArray());
            foreach (var path in fixture.EntryPaths)
            {
                var row = Assert.IsAssignableFrom<DependencyObject>(list.ItemContainerGenerator.ContainerFromItem(path));
                var remove = Assert.Single(Descendants(row).OfType<Ui.Button>());
                Assert.Same(fixture.RemoveEntryPathCommand, remove.Command);
                Assert.Equal(path, remove.CommandParameter);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(remove)));
                Assert.NotNull(remove.ToolTip);
                Assert.Contains(Descendants(row).OfType<Ui.TextBlock>(), text => text.Text == path && Equals(text.ToolTip, path));
            }

            var input = Assert.Single(Descendants(details).OfType<Ui.TextBox>(), candidate =>
                BindingOperations.GetBinding(candidate, TextBox.TextProperty)?.Path.Path == "NewEntryPath");
            Assert.Equal(UpdateSourceTrigger.PropertyChanged,
                BindingOperations.GetBinding(input, TextBox.TextProperty)!.UpdateSourceTrigger);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(input)));
            Assert.Contains(input, Descendants(entryCard));
            Assert.DoesNotContain(input, Descendants(databaseCard));
            Assert.Same(fixture.AddEntryPathCommand, Assert.Single(input.InputBindings.OfType<KeyBinding>(), key => key.Key == Key.Enter).Command);
            Assert.Single(Descendants(details).OfType<Ui.Button>(), button => ReferenceEquals(button.Command, fixture.AddEntryPathCommand));
            Assert.Single(Descendants(details).OfType<Ui.Button>(), button => ReferenceEquals(button.Command, fixture.BrowseEntryPathCommand));

            var location = Assert.IsType<Ui.Button>(page.FindName("CatalogLocationButton"));
            Assert.Same(fixture.OpenEntryPathsCommand, location.Command);
            Assert.Equal(Ui.SymbolRegular.FolderAdd20, Assert.IsType<Ui.SymbolIcon>(location.Icon).Symbol);
            Assert.Equal(page.FindResource("Loc.Catalog.Entries.Manage"), AutomationProperties.GetName(location));
            Assert.Equal(page.FindResource("Loc.Catalog.Entries.Help"), AutomationProperties.GetHelpText(location));
            var actionLabel = Assert.IsType<Ui.TextBlock>(page.FindName("CatalogLocationActionLabel"));
            Assert.Equal(page.FindResource("Loc.Catalog.Entries.ManageShort"), actionLabel.Text);
            Assert.True(actionLabel.IsVisible && actionLabel.ActualWidth > 0,
                "The compact location action must visibly advertise adding paths, not only display a folder name.");
            var summary = Assert.IsType<Ui.TextBlock>(page.FindName("CatalogLocationSummary"));
            Assert.Equal(fixture.EntryPathsSummary, summary.Text);
            Assert.Equal("EntryPathsSummary", BindingOperations.GetBinding(summary, TextBlock.TextProperty)?.Path.Path);
            Assert.NotNull(location.ToolTip);
            Assert.Contains(Descendants(Assert.IsAssignableFrom<DependencyObject>(location.ToolTip)).OfType<Ui.TextBlock>(),
                text => BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path.Path == "EntryPathsDescription");
            fixture.CanEditWorkspace = false; fixture.Notify(); Pump(host);
            Assert.False(input.IsEnabled);
            Assert.False(location.IsEnabled);
            Assert.All(Descendants(list).OfType<Ui.Button>(), button => Assert.False(button.IsEnabled));
            fixture.CanEditWorkspace = true; fixture.Notify(); Pump(host);
            Assert.True(input.IsEnabled);
            Assert.True(input.ActualWidth > 120);
            capture?.Invoke(page, "Catalog-multiple-entry-paths.png");
        }
        finally { host.Content = null; host.Close(); }
    }

    private static void VerifyProfile(Action<FrameworkElement, string>? capture)
    {
        using var vm = ProfilesValidationTests.CreateForUi();
        vm.CurrentEditorStep = ProfileEditorStep.Profile;
        vm.AddEntryPathCommand.Execute(null);
        var first = Assert.Single(vm.DraftEntryPaths);
        first.Path = @"C:\Projects";
        vm.AddEntryPathCommand.Execute(null);
        var second = vm.DraftEntryPaths[1];
        second.Path = @"\\server\Archive";
        var page = new ProfilesPage(vm);
        var host = new Ui.FluentWindow { Width = 760, Height = 720, Content = page, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            host.Show(); Pump(host);
            var list = Assert.IsType<ProfileEditorList>(page.ActiveEditorList);
            var entries = list.Items.Cast<ProfileEditorEntry>().Where(entry => entry.Section.Id == "EntryPaths").ToArray();
            Assert.Equal(2, entries.Length);
            Assert.Same(first, entries[0].Value);
            Assert.Same(second, entries[1].Value);
            list.ScrollIntoView(entries[1]); Pump(host);
            var row = Assert.IsAssignableFrom<DependencyObject>(list.ItemContainerGenerator.ContainerFromItem(entries[1]));
            var input = Assert.Single(Descendants(row).OfType<Ui.TextBox>(), control => control.Name == "ProfileEntryPathInput");
            Assert.Same(second, input.DataContext);
            Assert.Equal(second.Path, input.Text);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(input)));
            Assert.NotNull(input.ToolTip);
            Assert.True(input.ActualWidth >= 120, "Path input must retain usable width beside the browse/delete actions.");
            var binding = BindingOperations.GetBinding(input, TextBox.TextProperty);
            Assert.Equal("Path", binding?.Path.Path);
            Assert.Equal(UpdateSourceTrigger.PropertyChanged, binding?.UpdateSourceTrigger);
            var browse = Assert.Single(Descendants(row).OfType<Ui.Button>(), button => ReferenceEquals(button.Command, vm.BrowseEntryPathCommand));
            var remove = Assert.Single(Descendants(row).OfType<Ui.Button>(), button => ReferenceEquals(button.Command, vm.RemoveEntryPathCommand));
            Assert.Same(second, browse.CommandParameter);
            Assert.Same(second, remove.CommandParameter);
            Assert.False(browse.IsEnabled); // Fixture has no OS picker; binding must still resolve.
            Assert.True(remove.IsEnabled);
            Assert.All(new[] { browse, remove }, button => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))));

            input.SetCurrentValue(TextBox.TextProperty, @"D:\Updated");
            input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); Pump(host);
            Assert.Equal(@"D:\Updated", second.Path);
            Assert.Equal(@"C:\Projects", first.Path);
            capture?.Invoke(page, "Profiles-multiple-entry-paths.png");
            remove.Command!.Execute(remove.CommandParameter); Pump(host);
            Assert.Same(first, Assert.Single(vm.DraftEntryPaths));
            Assert.Single(list.Items.Cast<ProfileEditorEntry>(), entry => entry.Section.Id == "EntryPaths");
            Assert.DoesNotContain(list.Items.Cast<ProfileEditorEntry>(), entry => ReferenceEquals(entry.Value, second));
        }
        finally { host.Content = null; host.Close(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Pump(Window host)
    {
        host.UpdateLayout();
        var frame = new DispatcherFrame();
        host.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        host.UpdateLayout();
    }

    public sealed class EntryListFixture : ObservableObject
    {
        public ObservableCollection<string> EntryPaths { get; } = [@"C:\Projects", @"\\server\Archive"];
        public string NewEntryPath { get; set; } = string.Empty;
        public string EntryPathsSummary => "2 starting locations";
        public string EntryPathsDescription => string.Join(Environment.NewLine, EntryPaths);
        public bool IsDetailsOpen => true;
        public bool CanEditWorkspace { get; set; } = true;
        public bool IsResultInteractionEnabled => true;
        public bool CanTypeSearch => true;
        public bool IsCatalogEmptyVisible => false;
        public bool HasLoadedItems => false;
        public object[] Profiles => [];
        public IRelayCommand AddEntryPathCommand { get; } = new RelayCommand(static () => { });
        public IRelayCommand BrowseEntryPathCommand { get; } = new RelayCommand(static () => { });
        public IRelayCommand<string> RemoveEntryPathCommand { get; } = new RelayCommand<string>(static _ => { });
        public IRelayCommand OpenEntryPathsCommand { get; } = new RelayCommand(static () => { });
        public void Notify() => OnPropertyChanged(string.Empty);
    }
}
