using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Services;
using MusicSpin.App.ViewModels;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui.Diagnostics;

/// <summary>Spin and spectrum sessions are one span each, not one span per frame.</summary>
public sealed class AnimationMarkerTests
{
    [Fact]
    public void Spin_session_is_one_span_for_the_whole_run()
    {
        var time = new FakeTimeProvider();
        var ui = new QueueDispatcher();
        using var clock = new SpinClock(time, ui);
        using var capture = MarkerCapture.Start();

        clock.IsSpinning = true;
        Assert.True(clock.IsRunning);
        time.Advance(TimeSpan.FromMilliseconds(48));
        ui.Run();
        clock.Dispose();

#if PERF_MARKERS
        Assert.Single(capture.Named("ui.spin.session"));
        Assert.DoesNotContain(capture.Marks, mark => mark.Name.Contains("tick", StringComparison.Ordinal));
#else
        Assert.Empty(capture.Marks);
#endif
    }

    [Fact]
    public void Spectrum_session_counts_published_frames_without_a_span_per_frame()
    {
        var time = new FakeTimeProvider();
        var ui = new QueueDispatcher();
        using var spectrum = new SpectrumViewModel(time, ui);
        using var capture = MarkerCapture.Start();

        spectrum.SetLevels([1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f]);
        Assert.True(spectrum.IsRunning);
        time.Advance(TimeSpan.FromMilliseconds(48));
        ui.Run();
        spectrum.Dispose();

#if PERF_MARKERS
        var session = Assert.Single(capture.Named("ui.spectrum.session"));
        Assert.True(int.Parse(session.Tags["frames"], System.Globalization.CultureInfo.InvariantCulture) >= 1);
#else
        Assert.Empty(capture.Marks);
#endif
    }
}
