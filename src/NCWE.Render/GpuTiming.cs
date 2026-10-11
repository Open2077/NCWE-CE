// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace NCWE.Render;

// GPU time of the frame (timestamp queries): the world scene (sky, geometry, lines, MSAA resolve)
// and everything until Present (overlay included). Results are read a few frames later, without
// waiting for the GPU; a frame whose results are not there yet is simply not measured.
public sealed partial class D3DRenderer
{
    private const int GpuQueryFrames = 6;
    private readonly ID3D11Query?[] _gpuDisjoint = new ID3D11Query?[GpuQueryFrames], _gpuStart = new ID3D11Query?[GpuQueryFrames],
        _gpuScene = new ID3D11Query?[GpuQueryFrames], _gpuEnd = new ID3D11Query?[GpuQueryFrames];
    private readonly bool[] _gpuIssued = new bool[GpuQueryFrames];
    private int _gpuSlot;
    private bool _gpuOpen;

    // NaN until measured; the newest result read.
    public double GpuSceneMilliseconds { get; private set; } = double.NaN;
    public double GpuFrameMilliseconds { get; private set; } = double.NaN;
    // True on the frames that read a new result (callers record it once).
    public bool GpuTimingFresh { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimestampDisjoint { public ulong Frequency; public int Disjoint; }

    public void BeginGpuFrame()
    {
        GpuTimingFresh = false;
        int slot = _gpuSlot;
        if (_gpuDisjoint[slot] is null)
        {
            _gpuDisjoint[slot] = Device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
            _gpuStart[slot] = Device.CreateQuery(new QueryDescription(QueryType.Timestamp));
            _gpuScene[slot] = Device.CreateQuery(new QueryDescription(QueryType.Timestamp));
            _gpuEnd[slot] = Device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        }
        else if (_gpuIssued[slot]) ReadGpuSlot(slot);
        Context.Begin(_gpuDisjoint[slot]!);
        Context.End(_gpuStart[slot]!);
        _gpuOpen = true;
    }

    public void MarkGpuSceneDone() { if (_gpuOpen) Context.End(_gpuScene[_gpuSlot]!); }

    public void EndGpuFrame()
    {
        if (!_gpuOpen) return;
        int slot = _gpuSlot;
        Context.End(_gpuEnd[slot]!);
        Context.End(_gpuDisjoint[slot]!);
        _gpuIssued[slot] = true; _gpuOpen = false;
        _gpuSlot = (slot + 1) % GpuQueryFrames;
    }

    private unsafe void ReadGpuSlot(int slot)
    {
        _gpuIssued[slot] = false;
        TimestampDisjoint disjoint;
        ulong start, scene, end;
        if (Context.GetData(_gpuDisjoint[slot]!, (nint)(&disjoint), (uint)sizeof(TimestampDisjoint), AsyncGetDataFlags.DoNotFlush).Code != 0) return;
        if (disjoint.Disjoint != 0 || disjoint.Frequency == 0) return;
        if (Context.GetData(_gpuStart[slot]!, (nint)(&start), 8, AsyncGetDataFlags.DoNotFlush).Code != 0
            || Context.GetData(_gpuScene[slot]!, (nint)(&scene), 8, AsyncGetDataFlags.DoNotFlush).Code != 0
            || Context.GetData(_gpuEnd[slot]!, (nint)(&end), 8, AsyncGetDataFlags.DoNotFlush).Code != 0) return;
        double toMs = 1000.0 / disjoint.Frequency;
        GpuSceneMilliseconds = (scene - start) * toMs;
        GpuFrameMilliseconds = (end - start) * toMs;
        GpuTimingFresh = true;
    }

    private void DisposeGpuTiming()
    {
        for (int i = 0; i < GpuQueryFrames; i++)
        {
            _gpuDisjoint[i]?.Dispose(); _gpuStart[i]?.Dispose(); _gpuScene[i]?.Dispose(); _gpuEnd[i]?.Dispose();
            _gpuDisjoint[i] = _gpuStart[i] = _gpuScene[i] = _gpuEnd[i] = null; _gpuIssued[i] = false;
        }
    }
}
