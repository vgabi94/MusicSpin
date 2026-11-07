#if PERF_MARKERS
using Xunit.Sdk;
using Xunit.v3;

// Marker listeners are process-wide. Keep an instrumented run serial so one test's scan is not another's span.
[assembly: Parallelization(Mode = ParallelMode.None)]
#endif
