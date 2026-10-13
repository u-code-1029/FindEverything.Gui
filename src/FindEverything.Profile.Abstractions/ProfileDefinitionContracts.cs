// Retain the namespace while keeping authoring consumers independent of the
// compiler implementation assembly, as with the other profile runtime contracts.
namespace FindEverything.Profile.Runtime;

public sealed record ProfileDefinitionReview(
    ILoadedProfile? Profile,
    IReadOnlyList<ProfileDiagnostic> Diagnostics)
{
    public bool IsValid => Profile is not null
        && Diagnostics.All(static diagnostic => diagnostic.Severity != ProfileDiagnosticSeverity.Error);
}

public sealed record ProfileDefinitionTestResult(
    ProfileDefinitionReview Review,
    ProfileMapResult? Mapping);

public interface IProfileDefinitionCompiler
{
    ProfileDefinitionReview Validate(ProfileManifest manifest);

    ProfileDefinitionTestResult Test(ProfileManifest manifest, string samplePath);
}
