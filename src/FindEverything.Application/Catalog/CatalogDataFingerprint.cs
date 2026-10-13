using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FindEverything.Profile.Runtime;

namespace FindEverything.Application.Catalog;

public static class CatalogDataFingerprint
{
    /// <summary>Interactions and new-scan settings do not invalidate already parsed values.</summary>
    public static string Create(ProfileDescriptor descriptor, string runtimeIdentity)
    {
        var node = JsonSerializer.SerializeToNode(descriptor)!.AsObject();
        node.Remove("SelectionMatchRules");
        node.Remove("MaxScanDepth");
        if (node["TextFileFields"] is JsonArray textFields)
            foreach (var field in textFields.OfType<JsonObject>()) field.Remove("EditOptions");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToJsonString() + "|" + runtimeIdentity)));
    }
}
