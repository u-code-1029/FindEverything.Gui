namespace FindEverything.Desktop.Services;

/// <summary>One explicitly tested example, held in memory only; never reads the disk while typing a format.</summary>
public sealed class OutputPreviewSampleStore
{
    private readonly object _gate = new();
    private string? _profileId;
    private SelectionOutputItem? _item;
    public event EventHandler? Changed;

    public void Set(string profileId, SelectionOutputItem item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            _profileId = profileId;
            _item = item with { Fields = new Dictionary<string, object?>(item.Fields, StringComparer.OrdinalIgnoreCase) };
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public SelectionOutputItem? Get(string? profileId)
    {
        lock (_gate)
            return string.IsNullOrWhiteSpace(profileId) || string.Equals(profileId, _profileId, StringComparison.OrdinalIgnoreCase)
                ? _item : null;
    }
}
