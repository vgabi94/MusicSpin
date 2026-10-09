using System.Buffers;
using System.Text;

namespace MusicSpin.Core.Remote;

/// <summary>The peer sent a line longer than the allowed maximum.</summary>
public sealed class MessageTooLargeException(int limit)
    : IOException($"Message exceeds the {limit}-byte limit.")
{
    public int Limit { get; } = limit;
}

/// <summary>UTF-8 JSON-lines over a stream with a hard per-line size limit and serialized writes.</summary>
public sealed class LineChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly int _maxLineBytes;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _buffer = new byte[16 * 1024];
    private readonly ArrayBufferWriter<byte> _line = new();
    private int _start;
    private int _end;

    public LineChannel(Stream stream, int maxLineBytes)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineBytes, 16);
        _maxLineBytes = maxLineBytes;
    }

    /// <summary>Reads the next line (without the newline); null at end of stream.</summary>
    /// <exception cref="MessageTooLargeException">The line exceeds the limit (the channel should then be closed).</exception>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        _line.ResetWrittenCount();
        while (true)
        {
            if (_start < _end)
            {
                var span = _buffer.AsSpan(_start, _end - _start);
                var newline = span.IndexOf((byte)'\n');
                var take = newline >= 0 ? newline : span.Length;
                if (_line.WrittenCount + take > _maxLineBytes)
                {
                    throw new MessageTooLargeException(_maxLineBytes);
                }

                _line.Write(span[..take]);
                _start += newline >= 0 ? take + 1 : take;
                if (newline >= 0)
                {
                    var bytes = _line.WrittenSpan;
                    if (bytes.Length > 0 && bytes[^1] == '\r')
                    {
                        bytes = bytes[..^1];
                    }

                    return Encoding.UTF8.GetString(bytes);
                }
            }

            _start = 0;
            _end = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            if (_end == 0)
            {
                // EOF: a trailing partial line (no newline) is discarded, like an aborted request.
                return null;
            }
        }
    }

    public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
