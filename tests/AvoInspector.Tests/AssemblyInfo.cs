using Xunit;

// The SDK has process-wide state (the static logging flag) and tests mutate the shared
// AVO_INSPECTOR_MOCK_ENDPOINT environment variable, so tests must not run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
