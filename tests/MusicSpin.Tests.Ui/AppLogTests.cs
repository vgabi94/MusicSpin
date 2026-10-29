using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Services;
using MusicSpin.Core;
using MusicSpin.Core.Hosting;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// <c>app.log</c> and the notices that guard commands (#185): a failure nobody planned for is a notice with its stack
/// trace in the log, not the end of the app; the planned ones stay plain notices.
/// </summary>
public sealed class AppLogTests
{
    private readonly string _root = TempDir.Create("applog");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 29, 18, 56, 3, TimeSpan.Zero));

    private AppLog Log() => AppLog.For(new DataDirectory(_root, DataDirectorySource.Override), _time);

    private string LogText() => File.ReadAllText(Path.Combine(_root, AppLog.FileName));

    private static NotImplementedException Thrown()
    {
        try
        {
            throw new NotImplementedException("The method or operation is not implemented.");
        }
        catch (NotImplementedException ex)
        {
            return ex; // with a stack trace, as a real one has
        }
    }

    [Fact]
    public void A_line_has_the_time_the_process_the_message_and_the_stack_trace()
    {
        var log = Log();
        var ex = Thrown();

        log.Write("unexpected failure", ex);
        log.Write("second");

        var lines = LogText().Split(Environment.NewLine);
        Assert.Equal($"2026-10-29T18:56:03.000Z [{Environment.ProcessId}] unexpected failure", lines[0]);
        Assert.Contains($"System.NotImplementedException: {ex.Message}", lines[1], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains(nameof(Thrown), StringComparison.Ordinal));
        Assert.Contains($"[{Environment.ProcessId}] second", lines[^2], StringComparison.Ordinal);
        Assert.Equal(string.Empty, lines[^1]);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_full_log_moves_aside_and_the_next_line_starts_a_new_one()
    {
        var file = Path.Combine(_root, AppLog.FileName);
        File.WriteAllText(file + ".1", "the oldest");
        File.WriteAllText(file, new string('x', (int)AppLog.MaxBytes + 1));

        Log().Write("fresh");

        Assert.Equal(AppLog.MaxBytes + 1, new FileInfo(file + ".1").Length);
        Assert.EndsWith($"fresh{Environment.NewLine}", LogText(), StringComparison.Ordinal);
        Assert.DoesNotContain("x", LogText(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_log_that_cannot_be_written_is_skipped()
    {
        var missing = new AppLog(Path.Combine(_root, "gone", "deeper", AppLog.FileName));
        var directory = new AppLog(_root); // a folder, not a file

        missing.Write("lost", Thrown());
        directory.Write("lost");
        AppLog.None.Write("nowhere", Thrown());

        Assert.False(Directory.Exists(Path.Combine(_root, "gone")));
        Assert.Null(AppLog.None.File);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task An_unexpected_failure_is_a_notice_pointing_at_the_log_and_the_log_has_the_stack_trace()
    {
        var notices = new Notices(Log());
        var posted = new List<string>();
        notices.Posted += (_, m) => posted.Add(m);
        var ex = Thrown();

        await notices.GuardAsync(() => Task.FromException(ex));

        var file = Path.Combine(_root, AppLog.FileName);
        Assert.Equal([$"Something went wrong: {ex.Message} The details are in {file}."], posted);
        Assert.Contains("unexpected failure", LogText(), StringComparison.Ordinal);
        Assert.Contains(nameof(Thrown), LogText(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task An_unexpected_failure_thrown_before_the_first_await_is_a_notice_too()
    {
        var notices = new Notices();
        var posted = new List<string>();
        notices.Posted += (_, m) => posted.Add(m);

        await notices.GuardAsync(() => throw new InvalidOperationException("Broken."));

        Assert.Equal(["Something went wrong: Broken."], posted); // no log, no file to point at
    }

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData("library")]
    [InlineData("io")]
    [InlineData("access")]
    public async Task A_planned_failure_is_its_own_message_and_is_not_logged(string kind)
    {
        var notices = new Notices(Log());
        var posted = new List<string>();
        notices.Posted += (_, m) => posted.Add(m);
        Exception ex = kind switch
        {
            "library" => new MusicSpinException(MusicSpinErrorCode.LibraryCorrupt, "The library database is corrupt."),
            "io" => new IOException("The disk is gone."),
            _ => new UnauthorizedAccessException("Not yours."),
        };

        await notices.GuardAsync(() => Task.FromException(ex));

        Assert.Equal([ex.Message], posted);
        Assert.False(File.Exists(Path.Combine(_root, AppLog.FileName)));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_cancellation_still_reaches_the_caller_and_is_neither_a_notice_nor_logged()
    {
        var notices = new Notices(Log());
        var posted = new List<string>();
        notices.Posted += (_, m) => posted.Add(m);

        await Assert.ThrowsAsync<OperationCanceledException>(() => notices.GuardAsync(() => throw new OperationCanceledException()));

        Assert.Empty(posted);
        Assert.False(File.Exists(Path.Combine(_root, AppLog.FileName)));
    }
}
