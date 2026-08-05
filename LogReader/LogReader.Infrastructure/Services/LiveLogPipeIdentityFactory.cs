namespace LogReader.Infrastructure.Services;

using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using LogReader.Core.Models;

public static class LiveLogPipeIdentityFactory
{
    private const string PipePrefix = "weeztail-log-v1-";

    public static LiveLogPipeIdentity CreateCurrent(string storageRoot)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Live WeezTail IPC requires Windows.");

        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
            throw new InvalidOperationException("The current Windows user SID is unavailable.");

        return Create(storageRoot, sid);
    }

    public static LiveLogPipeIdentity Create(string storageRoot, string userSid)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
            throw new ArgumentException("A storage root is required.", nameof(storageRoot));
        if (string.IsNullOrWhiteSpace(userSid))
            throw new ArgumentException("A Windows user SID is required.", nameof(userSid));

        var normalizedRoot = Path.GetFullPath(storageRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var identityMaterial = $"WeezTail|live-log|{LiveLogIpcProtocol.CurrentVersion}|{userSid}|{normalizedRoot}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identityMaterial));
        var storageIdentity = Convert.ToHexString(hash).ToLowerInvariant();
        return new LiveLogPipeIdentity(
            PipePrefix + storageIdentity[..32],
            storageIdentity);
    }
}
