namespace LogReader.Infrastructure.Services;

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal static class LiveLogPipeClientValidator
{
    private const int MaximumComputerNameCharacters = 256;
    private const int ErrorPipeLocal = 229;

    public static bool IsLocalClient(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!OperatingSystem.IsWindows() || !pipe.IsConnected)
            return false;
        return IsLocalWindowsClient(pipe.SafePipeHandle);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsLocalWindowsClient(SafePipeHandle pipeHandle)
    {
        var succeeded = TryGetClientComputerName(pipeHandle, out var name, out var errorCode);
        return succeeded
            ? IsLocalComputerName(name, Environment.MachineName)
            : errorCode == ErrorPipeLocal;
    }

    [SupportedOSPlatform("windows")]
    internal static bool TryGetClientComputerName(
        SafePipeHandle pipeHandle,
        out string clientComputerName,
        out int errorCode)
    {
        var name = new StringBuilder(MaximumComputerNameCharacters);
        var succeeded = GetNamedPipeClientComputerName(
            pipeHandle,
            name,
            (uint)name.Capacity);
        clientComputerName = succeeded ? name.ToString() : string.Empty;
        errorCode = succeeded ? 0 : Marshal.GetLastPInvokeError();
        return succeeded;
    }

    internal static bool IsLocalComputerName(string? clientName, string? localName)
    {
        var normalizedClient = NormalizeComputerName(clientName);
        var normalizedLocal = NormalizeComputerName(localName);
        if (normalizedClient.Length == 0 || normalizedLocal.Length == 0)
            return false;
        if (StringComparer.OrdinalIgnoreCase.Equals(normalizedClient, normalizedLocal))
            return true;

        return StringComparer.OrdinalIgnoreCase.Equals(
            GetShortComputerName(normalizedClient),
            GetShortComputerName(normalizedLocal));
    }

    private static string NormalizeComputerName(string? value)
        => (value ?? string.Empty).Trim().Trim('\\', '.');

    private static string GetShortComputerName(string value)
    {
        var separator = value.IndexOf('.');
        return separator < 0 ? value : value[..separator];
    }

    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeClientComputerNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientComputerName(
        SafePipeHandle pipe,
        StringBuilder clientComputerName,
        uint clientComputerNameLength);
}
