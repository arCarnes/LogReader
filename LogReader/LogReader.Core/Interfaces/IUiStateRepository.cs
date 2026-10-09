namespace LogReader.Core.Interfaces;

using LogReader.Core.Models;

public interface IUiStateRepository
{
    Task<UiState> LoadAsync();
    Task SaveAsync(UiState state);
}
