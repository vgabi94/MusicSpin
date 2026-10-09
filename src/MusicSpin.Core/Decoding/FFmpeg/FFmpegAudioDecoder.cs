using FFmpeg.AutoGen;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Metadata;

namespace MusicSpin.Core.Decoding.FFmpeg;

/// <summary>
/// Decodes the best audio stream of a file with libavformat/libavcodec and converts it with
/// libswresample to interleaved float32 at the native sample rate and channel layout (no resampling).
/// Not thread-safe: callers serialise access (the Player does).
/// </summary>
public sealed unsafe class FFmpegAudioDecoder : IAudioDecoder
{
    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private SwrContext* _swr;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private readonly int _streamIndex;
    private readonly AVRational _timeBase;

    private float[] _pending = new float[8192];
    private int _pendingOffset;
    private int _pendingCount;
    private bool _flushSent;
    private bool _eof;
    private long _positionFrames;
    private long _skipUntilFrame = -1;

    private FFmpegAudioDecoder(string path)
    {
        FFmpegRuntime.EnsureAvailable();
        if (!File.Exists(path))
        {
            throw new MusicSpinException(MusicSpinErrorCode.FileNotFound, $"File not found: {path}");
        }

        try
        {
            AVFormatContext* format = null;
            Check(ffmpeg.avformat_open_input(&format, path, null, null), MusicSpinErrorCode.UnsupportedFormat, "open");
            _format = format;
            Check(ffmpeg.avformat_find_stream_info(_format, null), MusicSpinErrorCode.UnsupportedFormat, "read stream info");

            AVCodec* codec = null;
            _streamIndex = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
            if (_streamIndex < 0 || codec is null)
            {
                throw new MusicSpinException(MusicSpinErrorCode.UnsupportedFormat,
                    _streamIndex == ffmpeg.AVERROR_DECODER_NOT_FOUND
                        ? $"No decoder available for the audio stream in '{path}'."
                        : $"No audio stream found in '{path}'.");
            }

            var stream = _format->streams[_streamIndex];
            _timeBase = stream->time_base;

            _codec = ffmpeg.avcodec_alloc_context3(codec);
            Check(ffmpeg.avcodec_parameters_to_context(_codec, stream->codecpar), MusicSpinErrorCode.DecodeFailed, "configure decoder");
            _codec->pkt_timebase = stream->time_base;
            Check(ffmpeg.avcodec_open2(_codec, codec, null), MusicSpinErrorCode.DecodeFailed, "open decoder");

            if (_codec->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC || _codec->ch_layout.nb_channels <= 0)
            {
                var channels = Math.Max(1, _codec->ch_layout.nb_channels);
                ffmpeg.av_channel_layout_uninit(&_codec->ch_layout);
                ffmpeg.av_channel_layout_default(&_codec->ch_layout, channels);
            }

            Track = BuildTrackInfo(path, stream, codec);
            Format = new PcmFormat(_codec->sample_rate, _codec->ch_layout.nb_channels, Track.BitsPerSample ?? 32);

            InitResampler();
            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static FFmpegAudioDecoder Open(string path) => new(path);

    public TrackInfo Track { get; }

    public PcmFormat Format { get; }

    public TimeSpan Position => TimeSpan.FromSeconds((double)_positionFrames / Format.SampleRate);

    public bool CanSeek => _format is not null && (_format->pb is null || _format->pb->seekable != 0);

    public int Read(Span<float> buffer)
    {
        ObjectDisposedException.ThrowIf(_format is null, this);
        var channels = Format.Channels;
        // Only hand out whole frames.
        buffer = buffer[..(buffer.Length / channels * channels)];
        var written = 0;

        while (written < buffer.Length)
        {
            if (_pendingCount > 0)
            {
                var n = Math.Min(_pendingCount, buffer.Length - written);
                _pending.AsSpan(_pendingOffset, n).CopyTo(buffer[written..]);
                _pendingOffset += n;
                _pendingCount -= n;
                written += n;
                continue;
            }

            if (_eof || !DecodeNextFrame())
            {
                _eof = true;
                break;
            }
        }

        _positionFrames += written / channels;
        return written;
    }

    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_format is null, this);
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        if (Track.Duration > TimeSpan.Zero && position > Track.Duration)
        {
            position = Track.Duration;
        }

        var timestamp = (long)(position.TotalSeconds / ffmpeg.av_q2d(_timeBase));
        var stream = _format->streams[_streamIndex];
        if (stream->start_time != ffmpeg.AV_NOPTS_VALUE)
        {
            timestamp += stream->start_time;
        }

        Check(ffmpeg.av_seek_frame(_format, _streamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD),
            MusicSpinErrorCode.DecodeFailed, "seek");
        ffmpeg.avcodec_flush_buffers(_codec);
        ffmpeg.swr_close(_swr);
        Check(ffmpeg.swr_init(_swr), MusicSpinErrorCode.DecodeFailed, "reset resampler");

        _pendingCount = 0;
        _pendingOffset = 0;
        _eof = false;
        _flushSent = false;
        _positionFrames = (long)Math.Round(position.TotalSeconds * Format.SampleRate);
        _skipUntilFrame = _positionFrames;
    }

    public void Dispose()
    {
        if (_frame is not null)
        {
            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }

        if (_packet is not null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (_swr is not null)
        {
            var swr = _swr;
            ffmpeg.swr_free(&swr);
            _swr = null;
        }

        if (_codec is not null)
        {
            var codec = _codec;
            ffmpeg.avcodec_free_context(&codec);
            _codec = null;
        }

        if (_format is not null)
        {
            var format = _format;
            ffmpeg.avformat_close_input(&format);
            _format = null;
        }
    }

    /// <summary>Decodes until one frame was converted into <see cref="_pending"/>. False at end of stream.</summary>
    private bool DecodeNextFrame()
    {
        while (true)
        {
            var ret = ffmpeg.avcodec_receive_frame(_codec, _frame);
            if (ret == 0)
            {
                var ok = ConvertFrame();
                ffmpeg.av_frame_unref(_frame);
                if (ok)
                {
                    return true;
                }

                continue;
            }

            if (ret == FFmpegHelpers.ErrorEof)
            {
                return false;
            }

            if (ret != FFmpegHelpers.ErrorAgain)
            {
                Check(ret, MusicSpinErrorCode.DecodeFailed, "decode");
            }

            if (_flushSent)
            {
                return false;
            }

            ret = ffmpeg.av_read_frame(_format, _packet);
            if (ret < 0)
            {
                // End of file (or read error): drain the decoder.
                ffmpeg.avcodec_send_packet(_codec, null);
                _flushSent = true;
                continue;
            }

            if (_packet->stream_index == _streamIndex)
            {
                ret = ffmpeg.avcodec_send_packet(_codec, _packet);
                // Corrupt packets are skipped rather than aborting playback.
                if (ret < 0 && ret != FFmpegHelpers.ErrorAgain && ret != FFmpegHelpers.ErrorEof)
                {
                    ffmpeg.av_packet_unref(_packet);
                    continue;
                }
            }

            ffmpeg.av_packet_unref(_packet);
        }
    }

    private bool ConvertFrame()
    {
        var channels = Format.Channels;
        var maxOut = ffmpeg.swr_get_out_samples(_swr, _frame->nb_samples);
        if (maxOut <= 0)
        {
            return false;
        }

        if (_pending.Length < maxOut * channels)
        {
            _pending = new float[maxOut * channels];
        }

        int converted;
        fixed (float* p = _pending)
        {
            var outPtr = (byte*)p;
            converted = ffmpeg.swr_convert(_swr, &outPtr, maxOut, _frame->extended_data, _frame->nb_samples);
        }

        Check(converted, MusicSpinErrorCode.DecodeFailed, "convert samples");
        _pendingOffset = 0;
        _pendingCount = converted * channels;

        // After a seek the demuxer lands on the packet before the target: drop samples up to it.
        if (_skipUntilFrame >= 0)
        {
            var pts = _frame->best_effort_timestamp;
            if (pts != ffmpeg.AV_NOPTS_VALUE)
            {
                var stream = _format->streams[_streamIndex];
                if (stream->start_time != ffmpeg.AV_NOPTS_VALUE)
                {
                    pts -= stream->start_time;
                }

                var frameStart = (long)Math.Round(pts * ffmpeg.av_q2d(_timeBase) * Format.SampleRate);
                var drop = Math.Clamp(_skipUntilFrame - frameStart, 0, converted);
                _pendingOffset = (int)drop * channels;
                _pendingCount -= _pendingOffset;
            }

            _skipUntilFrame = -1;
        }

        return _pendingCount > 0;
    }

    private void InitResampler()
    {
        SwrContext* swr = null;
        var layout = _codec->ch_layout;
        Check(ffmpeg.swr_alloc_set_opts2(&swr,
                &layout, AVSampleFormat.AV_SAMPLE_FMT_FLT, _codec->sample_rate,
                &layout, _codec->sample_fmt, _codec->sample_rate,
                0, null),
            MusicSpinErrorCode.DecodeFailed, "create resampler");
        _swr = swr;
        Check(ffmpeg.swr_init(_swr), MusicSpinErrorCode.DecodeFailed, "init resampler");
    }

    private TrackInfo BuildTrackInfo(string path, AVStream* stream, AVCodec* codec)
    {
        var par = stream->codecpar;
        var codecId = par->codec_id;
        var codecName = ffmpeg.avcodec_get_name(codecId) ?? "unknown";
        var descriptor = ffmpeg.avcodec_descriptor_get(codecId);
        var isDsd = FFmpegHelpers.IsDsd(codecId);
        var lossless = isDsd
                       || codecName.StartsWith("pcm_", StringComparison.Ordinal)
                       || (descriptor is not null && (descriptor->props & ffmpeg.AV_CODEC_PROP_LOSSLESS) != 0
                           && (descriptor->props & ffmpeg.AV_CODEC_PROP_LOSSY) == 0);

        int? bits = isDsd ? 1
            : par->bits_per_raw_sample > 0 ? par->bits_per_raw_sample
            : lossless && par->bits_per_coded_sample > 0 ? par->bits_per_coded_sample
            : lossless ? ffmpeg.av_get_bytes_per_sample(_codec->sample_fmt) * 8
            : null;

        var duration = _format->duration > 0
            ? TimeSpan.FromSeconds((double)_format->duration / ffmpeg.AV_TIME_BASE)
            : stream->duration > 0
                ? TimeSpan.FromSeconds(stream->duration * ffmpeg.av_q2d(stream->time_base))
                : TimeSpan.Zero;

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        FFmpegHelpers.ReadDictionary(_format->metadata, tags);
        FFmpegHelpers.ReadDictionary(stream->metadata, tags);

        var bitRate = par->bit_rate > 0 ? par->bit_rate : _format->bit_rate > 0 ? _format->bit_rate : (long?)null;

        return new TrackInfo
        {
            Path = Path.GetFullPath(path),
            Container = FFmpegHelpers.Str(_format->iformat->name) ?? "unknown",
            Codec = codecName,
            CodecLongName = FFmpegHelpers.Str(codec->long_name),
            // FFmpeg decodes DSD to PCM at 1/8 of the DSD bit rate; report the native DSD rate.
            SampleRate = isDsd ? _codec->sample_rate * 8 : _codec->sample_rate,
            Channels = _codec->ch_layout.nb_channels,
            ChannelLayout = FFmpegHelpers.DescribeLayout(&_codec->ch_layout),
            BitsPerSample = bits,
            SampleFormat = ffmpeg.av_get_sample_fmt_name(_codec->sample_fmt),
            BitRate = bitRate,
            Duration = duration,
            IsLossless = lossless,
            Tags = tags,
        };
    }

    private static void Check(int result, MusicSpinErrorCode code, string action)
    {
        if (result < 0)
        {
            throw new MusicSpinException(code, $"FFmpeg failed to {action}: {FFmpegHelpers.ErrorString(result)}");
        }
    }
}
