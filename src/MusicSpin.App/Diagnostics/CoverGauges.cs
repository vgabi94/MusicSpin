#if PERF_MARKERS
using System.Diagnostics.Metrics;
using MusicSpin.App.Services;
using MusicSpin.Core.Diagnostics;

namespace MusicSpin.App.Diagnostics;

/// <summary>
/// Publishes <see cref="CoverService"/>'s own decoded-bitmap accounting on <see cref="Perf.Meter"/>. The byte figure is
/// the service's width×height×4 estimate, not a GC measurement and not process or GPU memory. The service is held
/// weakly so a test double can be collected.
/// </summary>
internal static class CoverGauges
{
    private static WeakReference<CoverService>? _service;
    private static int _registered;

    public static void Watch(CoverService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = new WeakReference<CoverService>(service);
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        Perf.Meter.CreateObservableGauge("musicspin.covers.cached_bytes", static () => Target()?.CachedBytes ?? 0);
        Perf.Meter.CreateObservableGauge("musicspin.covers.bitmaps_loaded", static () => (long)(Target()?.BitmapsLoaded ?? 0));
        Perf.Meter.CreateObservableGauge("musicspin.covers.bitmaps_disposed", static () => (long)(Target()?.BitmapsDisposed ?? 0));
    }

    private static CoverService? Target() => _service is not null && _service.TryGetTarget(out var service) ? service : null;
}
#endif
