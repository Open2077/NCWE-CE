// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;

namespace NCWE.Core;

// The XAML UI thread is STA, where a WaitHandle wait (Process.HasExited, WaitForExit, WaitOne,
// a Mutex) pumps window messages. A mouse move dispatched that way inside a XAML callback
// (rendering, layout, an event) re-enters XAML, which fails fast: 0xC000027B from
// CXcpDispatcher::CheckReentrancy (a tester's crash dump of 0.1.0-beta.1, Process.HasExited read
// every frame). Installed on that thread, this context keeps the dispatcher's posting and makes
// those waits plain kernel waits, as WPF's dispatcher context does. Monitor (lock) waits do not
// come here: they must stay uncontended on the UI thread.
public sealed partial class NonPumpingSynchronizationContext : SynchronizationContext
{
    private readonly SynchronizationContext _inner;

    public NonPumpingSynchronizationContext(SynchronizationContext inner)
    {
        _inner = inner;
        SetWaitNotificationRequired();
    }

    public static void InstallOnCurrentThread()
    {
        if (Current is NonPumpingSynchronizationContext) return;
        if (Current is { } inner) SetSynchronizationContext(new NonPumpingSynchronizationContext(inner));
    }

    public override void Post(SendOrPostCallback d, object? state) => _inner.Post(d, state);
    public override void Send(SendOrPostCallback d, object? state) => _inner.Send(d, state);
    public override void OperationStarted() => _inner.OperationStarted();
    public override void OperationCompleted() => _inner.OperationCompleted();
    public override SynchronizationContext CreateCopy() => new NonPumpingSynchronizationContext(_inner.CreateCopy());

    // Same contract as the default (WAIT_OBJECT_0 + index, WAIT_TIMEOUT...), without pumping.
    public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)
    {
        Waits++;
        return (int)WaitForMultipleObjectsEx((uint)waitHandles.Length, waitHandles, waitAll, unchecked((uint)millisecondsTimeout), false);
    }

    // Diagnostics and tests: how many waits went through this context.
    public static int Waits { get; private set; }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForMultipleObjectsEx(uint count, IntPtr[] handles, [MarshalAs(UnmanagedType.Bool)] bool waitAll, uint milliseconds, [MarshalAs(UnmanagedType.Bool)] bool alertable);
}
