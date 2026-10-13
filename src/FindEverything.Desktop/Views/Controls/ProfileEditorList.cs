using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Threading;

namespace FindEverything.Desktop.Views.Controls;

/// <summary>One viewport for section headers and field cards, with real WPF container recycling.</summary>
[ContentProperty(nameof(Sections))]
public sealed class ProfileEditorList : ListBox
{
    public static readonly DependencyProperty SectionsProperty = DependencyProperty.Register(
        nameof(Sections), typeof(FreezableCollection<ProfileEditorSection>), typeof(ProfileEditorList));
    private readonly ObservableCollection<ProfileEditorEntry> _entries = [];
    private readonly HashSet<INotifyCollectionChanged> _sources = [];
    private static readonly object NullValue = new();
    private DispatcherOperation? _refresh;

    public ProfileEditorList()
    {
        SetValue(SectionsProperty, new FreezableCollection<ProfileEditorSection>());
        Sections.Changed += (_, _) => ScheduleRefresh();
        ItemsSource = _entries;
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) =>
        {
            _refresh?.Abort(); _refresh = null;
            DetachSources();
        };
        DataContextChanged += (_, _) => ScheduleRefresh();
    }

    public FreezableCollection<ProfileEditorSection> Sections => (FreezableCollection<ProfileEditorSection>)GetValue(SectionsProperty);
    public ScrollViewer? ScrollViewer => Template?.FindName("PART_ScrollViewer", this) as ScrollViewer;

    public void ScrollToSection(string id)
    {
        if (_entries.FirstOrDefault(entry => entry.Section.Id == id) is { } entry) ScrollIntoView(entry);
    }

    private void ScheduleRefresh()
    {
        if (!IsLoaded || _refresh?.Status == DispatcherOperationStatus.Pending) return;
        _refresh = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() => { _refresh = null; Refresh(); }));
    }

    private void DetachSources()
    {
        foreach (var source in _sources) CollectionChangedEventManager.RemoveHandler(source, OnSourceChanged);
        _sources.Clear();
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs args) => ScheduleRefresh();

    private void Refresh()
    {
        if (!IsLoaded) return;
        DetachSources();
        var reusable = new Dictionary<ProfileEditorSection, Dictionary<object, Queue<ProfileEditorEntry>>>(ReferenceEqualityComparer.Instance);
        foreach (var entry in _entries)
        {
            if (!reusable.TryGetValue(entry.Section, out var values))
                reusable.Add(entry.Section, values = new(ReferenceEqualityComparer.Instance));
            var key = entry.Value ?? NullValue;
            if (!values.TryGetValue(key, out var matches)) values.Add(key, matches = new());
            matches.Enqueue(entry);
        }
        var desired = new List<ProfileEditorEntry>();
        foreach (var section in Sections)
        {
            if (section.Template is null) continue;
            if (!section.IsCollection) { Add(section, DataContext); continue; }
            if (section.ItemsSource is INotifyCollectionChanged source && _sources.Add(source))
                CollectionChangedEventManager.AddHandler(source, OnSourceChanged);
            if (section.ItemsSource is not { } items) continue;
            foreach (var item in items) Add(section, item);
        }
        // Keep existing entry identities and issue small moves/inserts instead
        // of Reset, preserving the focused field editor when a neighbour moves.
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < _entries.Count && ReferenceEquals(_entries[index], desired[index])) continue;
            var previous = _entries.IndexOf(desired[index]);
            if (previous >= 0) _entries.Move(previous, index);
            else _entries.Insert(index, desired[index]);
        }
        while (_entries.Count > desired.Count) _entries.RemoveAt(_entries.Count - 1);

        void Add(ProfileEditorSection section, object? value)
        {
            desired.Add(reusable.TryGetValue(section, out var values)
                && values.TryGetValue(value ?? NullValue, out var matches)
                && matches.TryDequeue(out var entry)
                    ? entry : new ProfileEditorEntry(section, value));
        }
    }
}

public sealed record ProfileEditorEntry(ProfileEditorSection Section, object? Value)
{
    public DataTemplate? Template => Section.Template;
}

/// <summary>Freezable inheritance supplies the owning list's binding context without constructing a view.</summary>
public sealed class ProfileEditorSection : Freezable
{
    public static readonly DependencyProperty IdProperty = DependencyProperty.Register(nameof(Id), typeof(string), typeof(ProfileEditorSection));
    public static readonly DependencyProperty TemplateProperty = DependencyProperty.Register(nameof(Template), typeof(DataTemplate), typeof(ProfileEditorSection));
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(ProfileEditorSection));
    public static readonly DependencyProperty IsCollectionProperty = DependencyProperty.Register(nameof(IsCollection), typeof(bool), typeof(ProfileEditorSection));
    public string? Id { get => (string?)GetValue(IdProperty); set => SetValue(IdProperty, value); }
    public DataTemplate? Template { get => (DataTemplate?)GetValue(TemplateProperty); set => SetValue(TemplateProperty, value); }
    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public bool IsCollection { get => (bool)GetValue(IsCollectionProperty); set => SetValue(IsCollectionProperty, value); }
    protected override Freezable CreateInstanceCore() => new ProfileEditorSection();
}
