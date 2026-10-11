// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.ExceptionServices;

namespace NCWE.Core;

// Parallel loop safe to call from a XAML UI (STA) thread. Parallel.For blocks the caller
// while helpers finish, and a blocking wait on an STA thread pumps window messages: inside
// a XAML callback that re-enters the dispatcher, which WinUI 3 answers with a fail-fast
// (0xC000027B, E_UNEXPECTED from CXcpDispatcher::OnWindowMessage). Here the caller takes
// items itself and only spins (no Sleep(1), no kernel wait) for items helpers already claimed.
// Helpers that start late find no work left, so a busy thread pool cannot delay the caller.
public static class UiParallel
{
    public static void For(int count, Action<int> body, int maximumDegree = 0)
    {
        if (count <= 0) return;
        if (count == 1) { body(0); return; }
        int degree = maximumDegree > 0 ? maximumDegree : Environment.ProcessorCount;
        int helpers = Math.Min(count, degree) - 1;
        var state = new LoopState(count, body);
        for (int i = 0; i < helpers; i++) ThreadPool.UnsafeQueueUserWorkItem(static s => s.Run(), state, preferLocal: false);
        state.Run();
        var spin = new SpinWait();
        while (Volatile.Read(ref state.InFlight) > 0) spin.SpinOnce(sleep1Threshold: -1);
        if (state.Error is { } error) ExceptionDispatchInfo.Throw(error);
    }

    private sealed class LoopState(int count, Action<int> body)
    {
        private int _next = -1;
        public int InFlight;
        public Exception? Error;

        public void Run()
        {
            while (true)
            {
                // Announce before claiming: the caller then cannot observe zero while an
                // item is claimed but not finished.
                Interlocked.Increment(ref InFlight);
                int index = Interlocked.Increment(ref _next);
                if (index >= count) { Interlocked.Decrement(ref InFlight); return; }
                try { body(index); }
                catch (Exception ex) { Interlocked.CompareExchange(ref Error, ex, null); Volatile.Write(ref _next, count); }
                finally { Interlocked.Decrement(ref InFlight); }
            }
        }
    }
}
