// SPDX-License-Identifier: GPL-3.0-only
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NCWE.Core;

namespace NCWE.Formats.CustomSounds;

// What an audio file holds. PcmWav: a plain 16-bit PCM WAV (the only WAV Open77 decodes).
public sealed record AudioProbe(string Extension, double Duration, int SampleRate, int Channels, long Bytes, bool PcmWav);

// Reading audio files and making them playable by Open77: MP3 and 16-bit PCM WAV within its limits
// are kept as they are, everything else (OGG, FLAC, M4A, WMA, other WAVs, too long or too big) is
// re-encoded to mono (Open77 downmixes spatial sounds to a point source anyway).
public static class AudioFiles
{
    public static readonly string[] Extensions = [".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".wma"];
    public static bool IsAudio(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // A decoder: Vorbis in managed code, everything else through Media Foundation.
    public static WaveStream Open(string path) => Path.GetExtension(path).ToLowerInvariant() == ".ogg"
        ? new NAudio.Vorbis.VorbisWaveReader(path) : new MediaFoundationReader(path);

    public static AudioProbe Probe(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        long bytes = new FileInfo(path).Length;
        try
        {
            if (extension == ".wav")
            {
                using var wav = new WaveFileReader(path);
                var f = wav.WaveFormat;
                return new(extension, wav.TotalTime.TotalSeconds, f.SampleRate, f.Channels, bytes, f.Encoding == WaveFormatEncoding.Pcm && f.BitsPerSample == 16);
            }
            using var reader = Open(path);
            return new(extension, reader.TotalTime.TotalSeconds, reader.WaveFormat.SampleRate, reader.WaveFormat.Channels, bytes, false);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        { throw new InvalidDataException($"{Path.GetFileName(path)} could not be read as audio ({ex.Message.Trim()}).", ex); }
    }

    // Why Open77 cannot play the file as it is (null: it can).
    public static string? Open77Problem(AudioProbe p)
    {
        if (p.Extension is not (".mp3" or ".wav")) return $"{p.Extension.TrimStart('.').ToUpperInvariant()} is not played by Open77 (MP3 and WAV are)";
        if (p.Extension == ".wav" && !p.PcmWav) return "this WAV is not 16-bit PCM";
        if (p.Bytes > Open77Audio.MaxFileBytes) return $"the file is {p.Bytes / 1048576.0:0.#} MiB (16 MiB at most)";
        if (p.Channels is < 1 or > 2) return $"{p.Channels} channels (mono or stereo only)";
        if (p.SampleRate is < Open77Audio.MinSampleRate or > Open77Audio.MaxSampleRate) return $"{p.SampleRate} Hz (8 to 96 kHz)";
        if (p.Duration > Open77Audio.MaxDuration) return $"{p.Duration:0} s long (10 minutes at most)";
        if (Open77Audio.PcmBytes(p.Duration, p.SampleRate, p.Channels) > Open77Audio.MaxPcmBytes) return "too long once decoded at this quality (32 MiB of sound per clip)";
        return null;
    }

    // Re-encodes to mono 16-bit: WAV at the source's rate (48 kHz at most) when it fits
    // Open77's 16 MiB (MP3 pads the start and end, which breaks seamless loops); longer clips as MP3
    // at 44.1 or 32 kHz while the decoded clip fits (needs Windows' MP3 encoder); else WAV at a
    // lower rate. Returns the file written.
    public static string ConvertForOpen77(string source, string destinationStem, IList<string> notes)
    {
        double seconds;
        int sourceRate;
        using (var probe = Open(source)) { seconds = probe.TotalTime.TotalSeconds; sourceRate = probe.WaveFormat.SampleRate; }
        if (seconds <= 0) throw new InvalidDataException($"{Path.GetFileName(source)} holds no sound.");
        if (seconds > Open77Audio.MaxDuration) throw new InvalidDataException($"{Path.GetFileName(source)} lasts {seconds / 60:0.#} minutes: Open77 plays clips of 10 minutes at most.");
        int[] rates = [48000, 44100, 32000, 22050, 16000, 11025, 8000];
        bool Fits(int rate) { long bytes = Open77Audio.PcmBytes(seconds, rate, 1); return bytes + 44 <= Open77Audio.MaxFileBytes && bytes <= Open77Audio.MaxPcmBytes; }
        string Wav(int rate, string note)
        {
            string wav = destinationStem + ".wav";
            using var reader = Open(source);
            WaveFileWriter.CreateWaveFile16(wav, Mono(reader, rate));
            notes.Add($"converted to mono 16-bit WAV ({rate / 1000.0:0.#} kHz){note}");
            return wav;
        }
        int best = rates.FirstOrDefault(r => r <= Math.Max(sourceRate, 8000), 8000);
        if (Fits(best)) return Wav(best, "");
        foreach (int rate in new[] { 44100, 32000 })
        {
            if (Open77Audio.PcmBytes(seconds, rate, 1) > Open77Audio.MaxPcmBytes) continue;
            string mp3 = destinationStem + ".mp3";
            try
            {
                using var reader = Open(source);
                MediaFoundationEncoder.EncodeToMp3(new SampleToWaveProvider16(Mono(reader, rate)), mp3, 160_000);
                notes.Add($"converted to mono MP3 ({rate / 1000.0:0.#} kHz)");
                return mp3;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
            { if (File.Exists(mp3)) File.Delete(mp3); break; }   // no MP3 encoder on this system: WAV below
        }
        foreach (int rate in rates.Where(r => r < best && Fits(r)))
            return Wav(rate, rate < 22050 ? ", reduced quality to fit Open77's 16 MiB" : "");
        throw new InvalidDataException($"{Path.GetFileName(source)} is too long for Open77, even at reduced quality.");
    }

    // Float samples, mono, at the given rate.
    public static ISampleProvider Mono(WaveStream reader, int rate)
    {
        ISampleProvider samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels == 2) samples = new StereoToMonoSampleProvider(samples) { LeftVolume = .5f, RightVolume = .5f };
        else if (samples.WaveFormat.Channels > 2) samples = new MultiplexingSampleProvider([samples], 1);
        return samples.WaveFormat.SampleRate == rate ? samples : new WdlResamplingSampleProvider(samples, rate);
    }
}
