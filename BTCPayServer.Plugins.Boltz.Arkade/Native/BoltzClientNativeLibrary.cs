using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BTCPayServer.Plugins.Boltz.Arkade.Native;

internal static class BoltzClientNativeLibrary
{
    private const string LibraryName = "boltz_client_bindings";
    private const string LibraryFileName = "libboltz_client_bindings.so";

    // The resolver must be installed before UniFFI's generated type initializer
    // performs its native contract and checksum calls.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        NativeLibrary.SetDllImportResolver(typeof(BoltzClientNativeLibrary).Assembly, Resolve);
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName || !OperatingSystem.IsLinux())
            return nint.Zero;

        var runtimeIdentifier = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "linux-x64",
            Architecture.Arm64 => "linux-arm64",
            _ => null
        };
        var assemblyDirectory = Path.GetDirectoryName(assembly.Location);
        if (runtimeIdentifier is null || assemblyDirectory is null)
            return nint.Zero;

        var libraryPath = Path.Combine(
            assemblyDirectory,
            "runtimes",
            runtimeIdentifier,
            "native",
            LibraryFileName);
        return File.Exists(libraryPath)
            ? NativeLibrary.Load(libraryPath)
            : nint.Zero;
    }
}
