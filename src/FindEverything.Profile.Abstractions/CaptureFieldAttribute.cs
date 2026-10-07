namespace FindEverything.Profile.Abstractions;

/// <summary>
/// Maps a named regular-expression group to a property on a profile item.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class CaptureFieldAttribute : Attribute
{
    public CaptureFieldAttribute(string fieldId, string groupName)
        : this(fieldId, groupName, [])
    {
    }

    public CaptureFieldAttribute(
        string fieldId,
        string groupName,
        params string[] additionalGroupNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        ArgumentNullException.ThrowIfNull(additionalGroupNames);

        foreach (var additionalGroupName in additionalGroupNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(additionalGroupName);
        }

        FieldId = fieldId;
        GroupName = groupName;
        GroupNames = [groupName, .. additionalGroupNames];
    }

    public string FieldId { get; }

    public string GroupName { get; }

    /// <summary>
    /// Gets the regular-expression groups in the order in which their captured
    /// values are concatenated before conversion to the property type.
    /// </summary>
    public IReadOnlyList<string> GroupNames { get; }

    public string? Header { get; set; }

    public int Order { get; set; }

    public bool Required { get; set; }

    public string? ParseFormat { get; set; }

    public string? DisplayFormat { get; set; }
}
