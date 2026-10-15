namespace LogReader.App.Services;

using System.IO;

internal sealed partial class DashboardOpenCoordinator
{
    private readonly Dictionary<string, Dictionary<string, DashboardMemberOpen>> _memberOpens = new(StringComparer.Ordinal);

    public bool TryGetInteractiveDashboardLoadTask(string dashboardId, out Task task)
    {
        task = _interactiveLoads.GetValueOrDefault(dashboardId)?.Task ?? Task.CompletedTask;
        return _interactiveLoads.ContainsKey(dashboardId);
    }

    public Task OpenDashboardMemberAsync(string dashboardId, string filePath, Func<CancellationToken, Task> openAsync)
    {
        if (_dashboardTransitions.TryGetValue(dashboardId, out var transition))
            return transition.Completion.Task;
        var path = Path.GetFullPath(filePath);
        if (!_memberOpens.TryGetValue(dashboardId, out var requests))
            _memberOpens.Add(dashboardId, requests = new(StringComparer.OrdinalIgnoreCase));
        if (requests.TryGetValue(path, out var existing))
            return existing.Completion.Task;

        var request = new DashboardMemberOpen();
        requests.Add(path, request);
        _ = RunMemberOpenAsync();
        return request.Completion.Task;

        async Task RunMemberOpenAsync()
        {
            Exception? error = null;
            try
            {
                await openAsync(request.Cancellation.Token);
            }
            catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested) { }
            catch (Exception ex) { error = ex; }
            finally
            {
                requests.Remove(path);
                if (requests.Count == 0)
                    _memberOpens.Remove(dashboardId);
                request.Cancellation.Dispose();
            }

            if (error != null) request.Completion.TrySetException(error);
            else request.Completion.TrySetResult();
        }
    }

    private bool TryGetMemberOpenTask(string dashboardId, string filePath, out Task task)
    {
        var request = _memberOpens.GetValueOrDefault(dashboardId)?.GetValueOrDefault(Path.GetFullPath(filePath));
        task = request?.Completion.Task ?? Task.CompletedTask;
        return request != null;
    }

    private IEnumerable<Task> GetMemberOpenTasks(string? dashboardId = null)
        => _memberOpens.Where(entry => dashboardId == null || entry.Key == dashboardId)
            .SelectMany(entry => entry.Value.Values).Select(request => request.Completion.Task);

    private void CancelMemberOpens(string dashboardId)
    {
        if (_memberOpens.TryGetValue(dashboardId, out var requests))
            foreach (var request in requests.Values.ToArray())
                request.Cancellation.Cancel();
    }

    private sealed class DashboardMemberOpen
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
