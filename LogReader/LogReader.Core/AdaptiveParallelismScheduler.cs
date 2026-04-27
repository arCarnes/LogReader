namespace LogReader.Core;

internal static class AdaptiveParallelismScheduler
{
    internal static IReadOnlyList<int> BuildInterleavedWorkOrder(ParallelismPlan plan)
    {
        var groups = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        var groupOrder = new List<string>();
        for (var index = 0; index < plan.Targets.Count; index++)
        {
            var targetPlan = plan.Targets[index];
            var groupKey = targetPlan.SecondaryGroupKey ?? targetPlan.TopLevelGroupKey;
            if (!groups.TryGetValue(groupKey, out var groupIndexes))
            {
                groupIndexes = new Queue<int>();
                groups[groupKey] = groupIndexes;
                groupOrder.Add(groupKey);
            }

            groupIndexes.Enqueue(index);
        }

        var workOrder = new List<int>(plan.Targets.Count);
        while (workOrder.Count < plan.Targets.Count)
        {
            foreach (var groupKey in groupOrder)
            {
                var groupIndexes = groups[groupKey];
                if (groupIndexes.Count > 0)
                    workOrder.Add(groupIndexes.Dequeue());
            }
        }

        return workOrder;
    }
}

internal sealed class AdaptiveParallelismGateSet : IDisposable
{
    private readonly SemaphoreSlim _globalGate;
    private readonly IReadOnlyDictionary<string, SemaphoreSlim> _groupGates;

    private AdaptiveParallelismGateSet(
        SemaphoreSlim globalGate,
        IReadOnlyDictionary<string, SemaphoreSlim> groupGates)
    {
        _globalGate = globalGate;
        _groupGates = groupGates;
    }

    public static AdaptiveParallelismGateSet Create(ParallelismPlan plan)
    {
        var globalLimit = Math.Max(1, plan.GlobalLimit);
        return new AdaptiveParallelismGateSet(
            new SemaphoreSlim(globalLimit, globalLimit),
            plan.Groups.ToDictionary(
                group => group.Key,
                group => new SemaphoreSlim(Math.Max(1, group.Limit), Math.Max(1, group.Limit)),
                StringComparer.Ordinal));
    }

    public async Task<GateLease> AcquireAsync(ParallelismTargetPlan targetPlan, CancellationToken ct)
    {
        var acquiredGates = new List<SemaphoreSlim>(3);
        try
        {
            await _globalGate.WaitAsync(ct);
            acquiredGates.Add(_globalGate);
            await AcquireGroupGateAsync(targetPlan.TopLevelGroupKey, acquiredGates, ct);
            if (targetPlan.SecondaryGroupKey is not null)
                await AcquireGroupGateAsync(targetPlan.SecondaryGroupKey, acquiredGates, ct);

            return new GateLease(acquiredGates);
        }
        catch
        {
            Release(acquiredGates);
            throw;
        }
    }

    private async Task AcquireGroupGateAsync(
        string groupKey,
        List<SemaphoreSlim> acquiredGates,
        CancellationToken ct)
    {
        if (!_groupGates.TryGetValue(groupKey, out var groupGate))
            return;

        await groupGate.WaitAsync(ct);
        acquiredGates.Add(groupGate);
    }

    public void Dispose()
    {
        _globalGate.Dispose();
        foreach (var gate in _groupGates.Values)
            gate.Dispose();
    }

    private static void Release(IReadOnlyList<SemaphoreSlim> acquiredGates)
    {
        for (var index = acquiredGates.Count - 1; index >= 0; index--)
            acquiredGates[index].Release();
    }

    internal sealed class GateLease : IDisposable
    {
        private readonly IReadOnlyList<SemaphoreSlim> _acquiredGates;
        private bool _disposed;

        public GateLease(IReadOnlyList<SemaphoreSlim> acquiredGates)
        {
            _acquiredGates = acquiredGates;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Release(_acquiredGates);
        }
    }
}
