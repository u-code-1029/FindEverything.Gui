namespace FindEverything.Profile.Abstractions;

/// <summary>
/// Maps a named regular-expression group to a property on a profile item.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class CaptureFieldAttribute : Attribute
{
    public CaptureFieldAttribute(string fieldId, string groupName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);

        FieldId = fieldId;
        GroupName = groupName;
    }

    public string FieldId { get; }

    public string GroupName { get; }

    public string? Header { get; set; }

    public int Order { get; set; }

    public bool Required { get; set; }

    public string? ParseFormat { get; set; }

    public string? DisplayFormat { get; set; }
}
