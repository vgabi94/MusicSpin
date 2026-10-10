using System.Text;

namespace MusicSpin.Tests.Shared;

/// <summary>
/// StringWriter safe for concurrent writes (a player host or watch command logging on other threads) and reads (test
/// polling). A plain StringWriter's StringBuilder can throw or return garbage when read while being written.
/// </summary>
public sealed class LockedStringWriter : StringWriter
{
    private readonly Lock _gate = new();

    public override void Write(char value)
    {
        lock (_gate)
        {
            base.Write(value);
        }
    }

    public override void Write(string? value)
    {
        lock (_gate)
        {
            base.Write(value);
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_gate)
        {
            base.Write(buffer, index, count);
        }
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        lock (_gate)
        {
            base.Write(buffer);
        }
    }

    public override void Write(StringBuilder? value)
    {
        lock (_gate)
        {
            base.Write(value);
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            base.WriteLine(value);
        }
    }

    public override void WriteLine(ReadOnlySpan<char> buffer)
    {
        lock (_gate)
        {
            base.WriteLine(buffer);
        }
    }

    public override void WriteLine(StringBuilder? value)
    {
        lock (_gate)
        {
            base.WriteLine(value);
        }
    }

    public string Snapshot() => ToString();

    public override string ToString()
    {
        lock (_gate)
        {
            return base.ToString();
        }
    }
}
