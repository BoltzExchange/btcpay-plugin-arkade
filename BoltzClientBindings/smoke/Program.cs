using System.Runtime.InteropServices;

namespace Boltz.Client;

internal static class Program
{
    private static void Main()
    {
        var contractVersion = _UniFFILib.ffi_boltz_client_bindings_uniffi_contract_version();
        Console.WriteLine(
            $"Validated generated Boltz client bindings on {RuntimeInformation.OSArchitecture} " +
            $"with UniFFI contract version {contractVersion}.");
    }
}
