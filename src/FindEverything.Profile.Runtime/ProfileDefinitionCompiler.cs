using Microsoft.Extensions.Options;

namespace FindEverything.Profile.Runtime;

public sealed record ProfileDefinitionReview(
    ILoadedProfile? Profile,
    IReadOnlyList<ProfileDiagnostic> Diagnostics)
{
    public bool IsValid => Profile is not null
        && Diagnostics.All(static diagnostic =>
            diagnostic.Severity != ProfileDiagnosticSeverity.Error);
}

public sealed record ProfileDefinitionTestResult(
    ProfileDefinitionReview Review,
    ProfileMapResult? Mapping);

public interface IProfileDefinitionCompiler
{
    ProfileDefinitionReview Validate(ProfileManifest manifest);

    ProfileDefinitionTestResult Test(ProfileManifest manifest, string samplePath);
}

internal sealed class ProfileDefinitionCompiler(
    IOptions<PluginDiscoveryOptions> options,
    ProfileModelCompiler modelCompiler) : IProfileDefinitionCompiler
{
    private readonly ProfileManifestValidator _manifestValidator = new();

    public ProfileDefinitionReview Validate(ProfileManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var readResult = _manifestValidator.Validate(
            AppContext.BaseDirectory,
            manifest,
            options.Value.ContractMajor);
        if (readResult.Manifest is null)
        {
            return new ProfileDefinitionReview(null, readResult.Diagnostics);
        }

        if (readResult.Manifest.Kind != ProfileKind.Declarative)
        {
            return new ProfileDefinitionReview(
                null,
                readResult.Diagnostics.Append(new ProfileDiagnostic(
                    ProfileDiagnosticSeverity.Error,
                    "profile_kind_not_editable",
                    "GUI 편집기에서는 Declarative 프로필만 만들 수 있습니다.")).ToArray());
        }

        var compilation = modelCompiler.Compile(readResult.Manifest);
        return new ProfileDefinitionReview(
            compilation.Profile,
            readResult.Diagnostics.Concat(compilation.Diagnostics).ToArray());
    }

    public ProfileDefinitionTestResult Test(ProfileManifest manifest, string samplePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(samplePath);

        var review = Validate(manifest);
        if (!review.IsValid || review.Profile is null)
        {
            return new ProfileDefinitionTestResult(review, null);
        }

        var mapping = review.Profile.Map(new ProfilePathCandidate(samplePath, samplePath));
        return new ProfileDefinitionTestResult(review, mapping);
    }
}
