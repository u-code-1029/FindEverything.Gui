using System.Text.Json;
using FindEverything.Profile.Runtime;

namespace FindEverything.Desktop.ViewModels;

public partial class ProfilesViewModel
{
    private ProfileWorkspaceManifest? BuildPortableWorkspace()
    {
        if (_portableWorkspace is null) return null;
        var copy = JsonSerializer.Deserialize<ProfileWorkspaceManifest>(JsonSerializer.Serialize(_portableWorkspace))!;
        // Removing a field must not leave an invalid display-only reference in an otherwise valid draft.
        var known = DraftFields.Select(f => f.FieldId.Trim()).Concat(DraftTextFileFields.Select(f => f.FieldId.Trim()))
            .Concat(DraftUserInputFields.Select(f => f.FieldId.Trim())).Concat(DraftChildDirectoryFields.Select(f => f.FieldId.Trim()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        known.Add("$ui:path"); known.Add("$ui:status");
        copy.Columns = copy.Columns?.Where(c => c.FieldId is not null && known.Contains(c.FieldId)).ToList();
        return copy;
    }
}
