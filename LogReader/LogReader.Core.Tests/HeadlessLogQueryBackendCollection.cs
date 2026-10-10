namespace LogReader.Core.Tests;

// These tests deliberately block disk workers and assert short scheduling deadlines.
// Run them apart from other collections that can occupy the shared thread pool.
[CollectionDefinition(nameof(HeadlessLogQueryBackendCollection), DisableParallelization = true)]
public sealed class HeadlessLogQueryBackendCollection
{
}
