// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;

namespace NCWE.Studio;

// Current system memory load (0..1) from Windows, unlike GC data, which is only updated
// at collections.
internal static class SystemMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage,
            PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool GetProcessMemoryInfo(nint process, ref ProcessCounters counters, uint size);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();

    // This process's private bytes, in microseconds (Process.Refresh takes about 10 ms: it reads
    // every process of the system).
    public static long PrivateBytes()
    {
        var counters = new ProcessCounters { Size = (uint)Marshal.SizeOf<ProcessCounters>() };
        return GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size) ? (long)counters.PrivateUsage : 0;
    }

    public static double Load()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) && status.TotalPhysical > 0 ? 1 - (double)status.AvailablePhysical / status.TotalPhysical : 0;
    }
}
