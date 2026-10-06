using System.Reflection;
using System.Runtime.Loader;
using FindEverything.Profile.Abstractions;

namespace FindEverything.Profile.Runtime;

internal sealed class ProfileLoadContext : AssemblyLoadContext
{
    private static readonly Assembly ContractAssembly = typeof(CaptureFieldAttribute).Assembly;
    private static readonly string ContractAssemblyName = ContractAssembly.GetName().Name
        ?? throw new InvalidOperationException("프로필 계약 어셈블리 이름을 확인할 수 없습니다.");

    private readonly AssemblyDependencyResolver _resolver;

    public ProfileLoadContext(string entryAssemblyPath)
        : base(
            $"profile:{Path.GetFileNameWithoutExtension(entryAssemblyPath)}:{Guid.NewGuid():N}",
            isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(
                assemblyName.Name,
                ContractAssemblyName,
                StringComparison.OrdinalIgnoreCase))
        {
            return ContractAssembly;
        }

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is null ? nint.Zero : LoadUnmanagedDllFromPath(libraryPath);
    }
}
