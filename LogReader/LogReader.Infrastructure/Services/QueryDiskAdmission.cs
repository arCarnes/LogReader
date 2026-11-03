namespace LogReader.Infrastructure.Services;

using LogReader.Core;

/// <summary>Shared atomic admission: global capacity plus normalized storage-group bounds.</summary>
internal sealed class QueryDiskAdmission(int globalLimit) : IDisposable
{
    private readonly object _gate = new();
    private readonly int _globalLimit = globalLimit > 0 ? globalLimit : throw new ArgumentOutOfRangeException(nameof(globalLimit));
    private readonly Dictionary<string, Group> _groups = new(StringComparer.Ordinal);
    private readonly LinkedList<Waiter> _waiters = new();
    private int _active;
    private bool _disposed;

    internal ValueTask<IDisposable> AcquireAsync(string path, CancellationToken ct)
    {
        var topology = AdaptiveParallelismPolicy.GetTopology(path);
        var limit = topology.Kind switch
        {
            ParallelismGroupKind.UncHost => 4,
            ParallelismGroupKind.LocalRoot => 2,
            _ => 1
        };
        Waiter waiter;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            var group = ReferenceGroup(topology.Key, limit);
            var share = topology.ShareKey == null ? null : ReferenceGroup(topology.ShareKey, 4);
            if (_waiters.Count == 0 && CanAdmit(group, share))
                return ValueTask.FromResult<IDisposable>(Admit(group, share));
            waiter = new Waiter(group, share, ct);
            waiter.Node = _waiters.AddLast(waiter);
            AdmitEligible();
        }
        return AwaitQueuedAsync(waiter, ct);
    }

    private async ValueTask<IDisposable> AwaitQueuedAsync(Waiter waiter, CancellationToken ct)
    {
        using var registration = ct.UnsafeRegister(_ => Cancel(waiter), null);
        return await waiter.Completion.Task.ConfigureAwait(false);
    }

    private Group ReferenceGroup(string key, int limit)
    {
        if (!_groups.TryGetValue(key, out var group))
        {
            group = new Group(key, limit);
            _groups.Add(key, group);
        }
        group.References++;
        return group;
    }

    private void ReleaseReference(Group? group)
    {
        if (group != null && --group.References == 0)
            _groups.Remove(group.Key);
    }

    private bool CanAdmit(Group group, Group? share)
        => _active < _globalLimit && group.Active < group.Limit && (share == null || share.Active < share.Limit);

    private Lease Admit(Group group, Group? share)
    {
        _active++;
        group.Active++;
        if (share != null) share.Active++;
        return new Lease(this, group, share);
    }

    private void AdmitEligible()
    {
        var node = _waiters.First;
        while (node != null && _active < _globalLimit)
        {
            var next = node.Next;
            var waiter = node.Value;
            if (waiter.Token.IsCancellationRequested)
                CancelQueued(waiter);
            else if (CanAdmit(waiter.Group, waiter.Share))
            {
                _waiters.Remove(node);
                waiter.Node = null;
                waiter.Completion.TrySetResult(Admit(waiter.Group, waiter.Share));
            }
            node = next;
        }
    }

    private void Cancel(Waiter waiter)
    {
        lock (_gate)
        {
            if (waiter.Node != null) CancelQueued(waiter);
            AdmitEligible();
        }
    }

    private void CancelQueued(Waiter waiter)
    {
        _waiters.Remove(waiter.Node!);
        waiter.Node = null;
        ReleaseReference(waiter.Group);
        ReleaseReference(waiter.Share);
        waiter.Completion.TrySetCanceled(waiter.Token.IsCancellationRequested ? waiter.Token : new CancellationToken(true));
    }

    private void Release(Group group, Group? share)
    {
        lock (_gate)
        {
            _active--;
            group.Active--;
            if (share != null) share.Active--;
            ReleaseReference(group);
            ReleaseReference(share);
            if (!_disposed) AdmitEligible();
        }
    }

    internal (int ActiveOperations, int QueuedOperations, int TopologyEntries) GetSnapshot()
    {
        lock (_gate) return (_active, _waiters.Count, _groups.Count);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            while (_waiters.First is { } node) CancelQueued(node.Value);
        }
    }

    private sealed class Group(string key, int limit)
    {
        internal string Key { get; } = key;
        internal int Limit { get; } = limit;
        internal int Active;
        internal int References;
    }

    private sealed class Waiter(Group group, Group? share, CancellationToken token)
    {
        internal Group Group { get; } = group;
        internal Group? Share { get; } = share;
        internal CancellationToken Token { get; } = token;
        internal TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Waiter>? Node;
    }

    private sealed class Lease(QueryDiskAdmission owner, Group group, Group? share) : IDisposable
    {
        private QueryDiskAdmission? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(group, share);
    }
}
