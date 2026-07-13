using Xunit;

// The plugin intentionally keeps room/lifetime/settings state in process-wide singletons. Tests
// that exercise those production types must not race each other in the same test process.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
