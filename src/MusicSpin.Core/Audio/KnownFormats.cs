namespace MusicSpin.Core.Audio;

/// <summary>Catalog of music formats MusicSpin targets, mapped to FFmpeg decoder names.</summary>
public static class KnownFormats
{
    public sealed record Definition(
        string Id, string Name, string[] Extensions, bool Lossless, bool HiResCapable, string[] Decoders);

    public static IReadOnlyList<Definition> All { get; } =
    [
        new("flac", "FLAC", ["flac"], true, true, ["flac"]),
        new("alac", "Apple Lossless (ALAC)", ["m4a", "caf"], true, true, ["alac"]),
        new("wav", "WAV / PCM", ["wav", "w64", "rf64"], true, true,
            ["pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_f32le", "pcm_f64le"]),
        new("aiff", "AIFF / PCM", ["aif", "aiff", "aifc"], true, true, ["pcm_s16be", "pcm_s24be", "pcm_s32be"]),
        new("dsd", "DSD (DSF / DFF)", ["dsf", "dff"], true, true, ["dsd_lsbf", "dsd_msbf", "dsd_lsbf_planar", "dsd_msbf_planar"]),
        new("wavpack", "WavPack", ["wv"], true, true, ["wavpack"]),
        new("ape", "Monkey's Audio (APE)", ["ape"], true, true, ["ape"]),
        new("tta", "True Audio (TTA)", ["tta"], true, true, ["tta"]),
        new("mlp", "MLP / TrueHD", ["mlp", "thd"], true, true, ["mlp", "truehd"]),
        new("mp3", "MP3", ["mp3"], false, false, ["mp3float", "mp3"]),
        new("aac", "AAC", ["m4a", "aac", "mp4"], false, false, ["aac"]),
        new("vorbis", "Ogg Vorbis", ["ogg", "oga"], false, false, ["vorbis", "libvorbis"]),
        new("opus", "Opus", ["opus", "ogg"], false, false, ["opus", "libopus"]),
        new("wma", "Windows Media Audio", ["wma"], false, false, ["wmav2", "wmapro", "wmalossless"]),
        new("mpc", "Musepack", ["mpc"], false, false, ["mpc7", "mpc8"]),
    ];
}
