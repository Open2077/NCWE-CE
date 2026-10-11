// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using Concentus;

namespace NCWE.Formats.GameAudio;

// Decoded audio: interleaved samples in -1..1.
public sealed record GamePcm(float[] Samples, int Channels, int Rate)
{
    public int Frames => Channels > 0 ? Samples.Length / Channels : 0;
    public double Duration => Rate > 0 ? (double)Frames / Rate : 0;

    // Front left and right, with what the other speakers carry folded in (quad, 5.1 in Wwise's
    // order: left, right, centre, low, back left, back right).
    public GamePcm ToStereo()
    {
        if (Channels == 2) return this;
        int frames = Frames; var stereo = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            int s = i * Channels; float left, right;
            switch (Channels)
            {
                case 1: left = right = Samples[s] * .7071f; break;
                case 4: left = (Samples[s] + Samples[s + 2] * .7071f) * .7071f; right = (Samples[s + 1] + Samples[s + 3] * .7071f) * .7071f; break;
                case >= 6:
                    float centre = Samples[s + 2] * .7071f;
                    left = (Samples[s] + centre + Samples[s + 4] * .7071f) * .6f; right = (Samples[s + 1] + centre + Samples[s + 5] * .7071f) * .6f; break;
                default: left = Samples[s]; right = Samples[s + 1]; break;
            }
            stereo[2 * i] = left; stereo[2 * i + 1] = right;
        }
        return new(stereo, 2, Rate);
    }

    public GamePcm ToMono()
    {
        if (Channels == 1) return this;
        int frames = Frames; var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < Channels; c++) sum += Samples[i * Channels + c];
            mono[i] = sum / MathF.Sqrt(Channels);
        }
        return new(mono, 1, Rate);
    }
}

// Opus in an Ogg stream (RFC 7845), as the game's sound container stores its effects: pages are
// split into packets, the head gives channels, the samples to skip and the gain, and the last
// page's position ends the stream to the sample. Decoded by Concentus (libopus in managed code)
// at Opus's own 48 kHz.
public static class OggOpus
{
    public const int Rate = 48000;
    private const int MaxFrame = 5760;   // 120 ms

    // The packets of the first logical stream, with the last page's granule position.
    public static List<ReadOnlyMemory<byte>> Packets(ReadOnlyMemory<byte> ogg, out long lastGranule)
    {
        var packets = new List<ReadOnlyMemory<byte>>(); lastGranule = 0;
        var d = ogg.Span; int position = 0; byte[]? pending = null; uint serial = 0; bool first = true;
        while (position + 27 <= d.Length && d[position] == 'O' && d[position + 1] == 'g' && d[position + 2] == 'g' && d[position + 3] == 'S')
        {
            long granule = BinaryPrimitives.ReadInt64LittleEndian(d[(position + 6)..]);
            uint stream = BinaryPrimitives.ReadUInt32LittleEndian(d[(position + 14)..]);
            int segments = d[position + 26], table = position + 27, body = table + segments;
            if (body > d.Length) break;
            if (first) { serial = stream; first = false; }
            int start = body, length = 0;
            for (int i = 0; i < segments; i++)
            {
                int lace = d[table + i];
                if (body + lace > d.Length) return packets;
                length += lace; body += lace;
                if (lace == 255) continue;
                if (stream == serial)
                {
                    if (pending is null) packets.Add(ogg.Slice(start, length));
                    else { var joined = new byte[pending.Length + length]; pending.CopyTo(joined, 0); d.Slice(start, length).CopyTo(joined.AsSpan(pending.Length)); packets.Add(joined); pending = null; }
                }
                start = body; length = 0;
            }
            // A packet that continues on the next page.
            if (length > 0 && stream == serial)
            {
                var part = d.Slice(start, length).ToArray();
                if (pending is null) pending = part;
                else { var joined = new byte[pending.Length + part.Length]; pending.CopyTo(joined, 0); part.CopyTo(joined, pending.Length); pending = joined; }
            }
            if (granule >= 0 && stream == serial) lastGranule = granule;
            position = body;
        }
        return packets;
    }

    public static bool IsOpus(ReadOnlySpan<byte> data) => data.Length > 36 && data[0] == 'O' && data[1] == 'g' && data[2] == 'g' && data[3] == 'S' && data.IndexOf("OpusHead"u8) is >= 0 and < 64;

    // maxSeconds: longer streams are cut there.
    public static GamePcm Decode(ReadOnlyMemory<byte> ogg, double maxSeconds = double.MaxValue)
    {
        var packets = Packets(ogg, out long granule);
        if (packets.Count < 2) throw new InvalidDataException("Not an Ogg Opus stream.");
        var head = packets[0].Span;
        if (head.Length < 19 || !head[..8].SequenceEqual("OpusHead"u8)) throw new InvalidDataException("Not an Ogg Opus stream.");
        int channels = head[9], preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head[10..]), family = head[18];
        float gain = MathF.Pow(10, BinaryPrimitives.ReadInt16LittleEndian(head[16..]) / 256f / 20f);
        if (channels is < 1 or > 8) throw new InvalidDataException("Unsupported Opus channel count.");
        long limit = maxSeconds >= long.MaxValue / Rate ? long.MaxValue : (long)(maxSeconds * Rate) + preSkip;
        var frame = new float[MaxFrame * channels];
        var output = new List<float>(Math.Min(packets.Count * 960 * channels, 64 << 20));
        Func<ReadOnlySpan<byte>, int> decode;
        if (family == 0)
        {
            var decoder = OpusCodecFactory.CreateDecoder(Rate, channels);
            decode = packet => decoder.Decode(packet, frame, MaxFrame, false);
        }
        else
        {
            if (head.Length < 21 + channels) throw new InvalidDataException("Truncated Opus head.");
            var decoder = OpusCodecFactory.CreateMultiStreamDecoder(Rate, channels, head[19], head[20], head.Slice(21, channels).ToArray());
            decode = packet => decoder.DecodeMultistream(packet, frame, MaxFrame, false);
        }
        long frames = 0;
        for (int i = 2; i < packets.Count && frames < limit; i++)   // after the head and the tags
        {
            int n = decode(packets[i].Span);
            if (n <= 0) continue;
            output.AddRange(frame.AsSpan(0, n * channels)); frames += n;
        }
        long end = Math.Min(Math.Min(frames, limit), granule > 0 ? granule : frames);
        int count = (int)Math.Max(0, end - preSkip) * channels;
        var samples = new float[count];
        if (count > 0) output.CopyTo(preSkip * channels, samples, 0, count);
        if (gain != 1) for (int i = 0; i < samples.Length; i++) samples[i] *= gain;
        return new(samples, channels, Rate);
    }
}
