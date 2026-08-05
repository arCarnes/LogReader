namespace LogReader.Infrastructure.Repositories;

using LogReader.Core;

internal interface INonInteractiveStorageRootResolver
{
    string ResolveStorageRoot();
}

internal sealed class NonInteractiveStorageRootResolver : INonInteractiveStorageRootResolver
{
    public string ResolveStorageRoot()
    {
        var configurationPath = AppPaths.GetInstallConfigPath();
        var baseDirectory = Path.GetDirectoryName(configurationPath);
        if (string.IsNullOrWhiteSpace(baseDirectory))
            baseDirectory = AppContext.BaseDirectory;

        var configuration = AppPaths.LoadStorageConfiguration();
        return configuration.ResolveStorageRoot(
            baseDirectory,
            configurationPath,
            AppPaths.GetMsiUserStorageSelectionPath(),
            AppPaths.GetDefaultStorageRoot());
    }
}
