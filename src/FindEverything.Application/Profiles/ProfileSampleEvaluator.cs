using FindEverything.Profile.Runtime;

namespace FindEverything.Application.Profiles;

public sealed record ProfileSampleResult(
    string CanonicalPath,
    string LeafName,
    ProfileMapResult Mapping,
    ProfileDirectoryNameExclusionResult? DirectoryExclusion);

public interface IProfileSampleEvaluator
{
    ProfileSampleResult Evaluate(ILoadedProfile profile, string inputPath);

    ProfileSampleResult Evaluate(ILoadedProfile profile, string inputPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = Evaluate(profile, inputPath);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}

/// <summary>Evaluates the published profile instance using the same absolute-path contract as scanning.</summary>
public sealed class ProfileSampleEvaluator(IProfilePathCanonicalizer pathCanonicalizer) : IProfileSampleEvaluator
{
    public ProfileSampleResult Evaluate(ILoadedProfile profile, string inputPath) => Evaluate(profile, inputPath, CancellationToken.None);

    public ProfileSampleResult Evaluate(ILoadedProfile profile, string inputPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalPath = pathCanonicalizer.Canonicalize(inputPath.Trim());
        cancellationToken.ThrowIfCancellationRequested();
        var leaf = GetDirectoryLeafName(canonicalPath);
        var exclusion = leaf.Length == 0 || profile.Descriptor.ExcludedDirectoryNameRules.Count == 0
            ? null : profile.EvaluateDirectoryName(leaf);
        cancellationToken.ThrowIfCancellationRequested();
        return new(canonicalPath, leaf, profile.Map(new ProfilePathCandidate(canonicalPath), cancellationToken), exclusion);
    }

    public static string GetDirectoryLeafName(string canonicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        var root = Path.GetPathRoot(canonicalPath);
        return !string.IsNullOrEmpty(root) && string.Equals(
            Path.TrimEndingDirectorySeparator(canonicalPath), Path.TrimEndingDirectorySeparator(root),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? string.Empty : Path.GetFileName(Path.TrimEndingDirectorySeparator(canonicalPath));
    }
}
