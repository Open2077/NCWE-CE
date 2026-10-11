// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;

namespace NCWE.Core;

// Custom sounds: audio files imported into the user's library, placed as CustomSound elements and
// played by Open77 (package audio, op77.62+). The game's own audio is Wwise, which cannot take new
// sounds, so only an Open77 export carries them: the files go into the resource, a generated
// script plays them.
public sealed record CustomSoundInfo(string Id, string Name)
{
    public int Version { get; init; } = 1;
    // The file in the library folder (sound.mp3 or sound.wav), ready for Open77.
    public string File { get; init; } = "";
    public string SourceFile { get; init; } = "";
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Modified { get; init; } = DateTimeOffset.UtcNow;
    public double Duration { get; init; }           // seconds
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public long Bytes { get; init; }
    // The source was re-encoded (format, size or length beyond what Open77 plays as is).
    public bool Converted { get; init; }
    public string Credits { get; init; } = "";
    public ImmutableArray<string> Warnings { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public string Extension => Path.GetExtension(File).ToLowerInvariant();
    [System.Text.Json.Serialization.JsonIgnore]   // its name inside an Open77 resource: sounds/<id>.mp3
    public string PublishedPath => $"sounds/{Id}{Extension}";
    [System.Text.Json.Serialization.JsonIgnore]
    public string DurationText => Duration >= 60 ? $"{(int)(Duration / 60)}:{Duration % 60:00}" : $"{Duration:0.0} s";
}

// Open77 package audio limits and mix (CyberM scripting/src/PackageAudio.cpp, wiki/package-audio.md).
public static class Open77Audio
{
    public const long MaxFileBytes = 16L << 20;     // encoded file
    public const long MaxPcmBytes = 32L << 20;      // decoded PCM16 per clip
    public const double MaxDuration = 600;          // seconds
    public const float MaxDistance = 1000;          // metres
    public const int HandlesPerResource = 16;
    public const int MinSampleRate = 8000, MaxSampleRate = 96000;

    public static long PcmBytes(double seconds, int sampleRate, int channels) => (long)Math.Ceiling(seconds * sampleRate) * Math.Max(1, channels) * 2;

    // What a listener hears of a spatial sound: linear fall-off from minDistance (full) to
    // maxDistance (silent), and an equal-power pan from the side the source is on.
    public static (float Left, float Right, float Gain) Spatial(Vector3 source, Vector3 listener, Vector3 listenerRight, float volume, float minDistance, float maxDistance)
    {
        var offset = source - listener;
        float distance = offset.Length();
        float gain = volume * Math.Clamp((maxDistance - distance) / Math.Max(1e-3f, maxDistance - minDistance), 0, 1);
        float pan = distance > .001f ? Math.Clamp(Vector3.Dot(offset, listenerRight) / distance, -1, 1) : 0;
        return (gain * MathF.Sqrt((1 - pan) * .5f), gain * MathF.Sqrt((1 + pan) * .5f), gain);
    }
}
