using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Output.MiniAudio;

namespace MusicSpin.App.Services;

/// <summary>The audio output devices (an OS boundary: tests use a fake).</summary>
public interface IAudioDevices
{
    /// <summary>The playback devices, as <c>music-spin devices</c> lists them. AudioDeviceError when they can't be listed.</summary>
    Task<IReadOnlyList<AudioDeviceInfo>> ListAsync();
}

/// <summary>The devices the player's output backend sees (miniaudio: WASAPI, CoreAudio, PulseAudio/ALSA).</summary>
public sealed class MiniAudioDevices : IAudioDevices
{
    public Task<IReadOnlyList<AudioDeviceInfo>> ListAsync() => Task.Run(() =>
    {
        try
        {
            using var provider = new MiniAudioOutputProvider();
            return provider.GetDevices();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            throw new MusicSpinException(MusicSpinErrorCode.AudioDeviceError, $"The audio devices couldn't be listed: {ex.Message}", ex);
        }
    });
}
