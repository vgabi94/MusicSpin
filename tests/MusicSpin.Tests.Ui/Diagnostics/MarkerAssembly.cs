#if PERF_MARKERS
using Xunit;

// This project is on xunit.v3 3.2, which still disables parallelization here. Marker listeners are process-wide.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
#endif
