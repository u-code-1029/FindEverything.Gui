namespace FindEverything.Application.Sharing;

/// <summary>Lexical path linking only. Never probes or reparses an imported source drive.</summary>
public static class WorkspacePathMapping
{
    public static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (root.StartsWith(@"\\?\", StringComparison.Ordinal) || root.StartsWith(@"\\.\", StringComparison.Ordinal)
            || root.StartsWith(@"\??\", StringComparison.Ordinal))
            throw new InvalidDataException("Use an ordinary absolute drive or UNC path, not a device namespace.");
        if (!Path.IsPathFullyQualified(root)) throw new InvalidDataException("An absolute root is required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public static string GetRelative(string root, string fullPath)
    {
        root = NormalizeRoot(root);
        fullPath = NormalizeRoot(fullPath);
        if (string.Equals(root, fullPath, StringComparison.OrdinalIgnoreCase)) return string.Empty;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An indexed path lies outside the package root.");
        return ValidateRelative(fullPath[prefix.Length..]);
    }

    public static string ValidateRelative(string relative)
    {
        if (relative.Length == 0 || relative == ".") return string.Empty;
        if (Path.IsPathRooted(relative) || relative.IndexOfAny([':', '\0']) >= 0)
            throw new InvalidDataException("An imported relative path is invalid.");
        var parts = relative.Split(['/', '\\'], StringSplitOptions.None);
        if (parts.Any(p => p.Length == 0 || p is "." or ".."))
            throw new InvalidDataException("An imported relative path escapes its root.");
        if (OperatingSystem.IsWindows() && parts.Any(p => p.EndsWith(' ') || p.EndsWith('.')))
            throw new InvalidDataException("An imported path contains ambiguous Windows folder names.");
        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    public static string Join(string root, string relative)
    {
        root = NormalizeRoot(root);
        relative = ValidateRelative(relative);
        return relative.Length == 0 ? root : Path.GetFullPath(Path.Combine(root, relative));
    }

    public static string ToEffective(ImportedWorkspaceSource source, string originalPath) =>
        Join(source.EffectiveRoot, GetRelative(source.OriginalRoot, originalPath));

    public static string ToOriginal(ImportedWorkspaceSource source, string effectivePath) =>
        Join(source.OriginalRoot, GetRelative(source.EffectiveRoot, effectivePath));
}
