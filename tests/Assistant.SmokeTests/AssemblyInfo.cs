using Xunit;

// The checks use real windows, hotkeys, helper programs and a model engine, one at a time: run together they would fight over the
// foreground, the clipboard and the machine's cores, and a failure would not say which check caused it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
