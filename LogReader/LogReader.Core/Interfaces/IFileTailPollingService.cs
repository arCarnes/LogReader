namespace LogReader.Core.Interfaces;

/// <summary>Releases a monitor reference with its requested polling interval.</summary>
internal interface IFileTailPollingService
{
    void StopTailing(string filePath, int pollingIntervalMs);
}
