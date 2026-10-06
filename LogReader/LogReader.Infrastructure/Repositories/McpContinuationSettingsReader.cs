namespace LogReader.Infrastructure.Repositories;

using System.Text.Json;
using LogReader.Core;
using LogReader.Core.Models;

/// <summary>Reads server settings without migration, directory creation, or persistence writes.</summary>
public static class McpContinuationSettingsReader
{
    public static Task<LogQueryEffectiveLimits> ReadAsync(CancellationToken ct = default)
        => ReadAsync(new NonInteractiveStorageRootResolver(), ct);

    internal static async Task<LogQueryEffectiveLimits> ReadAsync(INonInteractiveStorageRootResolver resolver, CancellationToken ct = default)
    {
        string root;
        try
        {
            root = resolver.ResolveStorageRoot();
        }
        catch (Exception ex) when (ex is StorageSetupRequiredException or InstallConfigurationException)
        {
            // Keep initialization available so catalog/status tools can report storage setup errors.
            return LogQueryEffectiveLimits.Default;
        }
        return await ReadFileAsync(Path.Combine(root, AppPaths.DataFolderName, "settings.json"), ct)
            .ConfigureAwait(false);
    }

    internal static async Task<LogQueryEffectiveLimits> ReadFileAsync(string path, CancellationToken ct = default)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException) { return LogQueryEffectiveLimits.Default; }
        catch (DirectoryNotFoundException) { return LogQueryEffectiveLimits.Default; }
        await using var ownedStream = stream;
        if (stream.Length > PersistedDashboardSnapshotReader.DefaultMaximumStoreBytes)
            throw new InvalidDataException("MCP settings exceed the supported file size.");
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("MCP settings must contain an object.");
        if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("MCP settings data must contain an object.");
        var (settings, _) = JsonRepositoryEnvelope.Deserialize<AppSettings>(document.RootElement, 1, "settings");
        var configuration = settings.McpContinuationLimits ?? new McpContinuationLimits();
        if (configuration.Validate() is { } error)
            throw new InvalidDataException(error);
        return configuration.ToEffectiveLimits();
    }
}
