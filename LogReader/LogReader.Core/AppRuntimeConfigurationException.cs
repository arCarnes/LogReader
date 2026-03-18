namespace LogReader.Core;

public sealed class AppRuntimeConfigurationException : InvalidOperationException
{
    public AppRuntimeConfigurationException(string configurationPath, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ConfigurationPath = configurationPath;
    }

    public string ConfigurationPath { get; }
}
