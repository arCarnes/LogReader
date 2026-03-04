namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

/// <summary>
/// Persists the application session state (open tabs, active tab).
/// </summary>
public interface ISessionRepository
{
    /// <summary>Loads the last saved session, or returns a default empty session.</summary>
    Task<SessionState> LoadAsync();

    /// <summary>Saves the current session state.</summary>
    Task SaveAsync(SessionState state);
}
