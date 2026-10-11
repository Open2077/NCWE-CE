// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;

namespace NCWE.Studio;

// An exception escaping a DispatcherQueue callback (TryEnqueue, DispatcherQueueTimer.Tick) ends
// the process at once: CoreMessaging fails fast (0xC000027B) without raising UnhandledException,
// so nothing records what happened (crash dump of 2026-10-08: DispatcherQueueTimer::TimerCallback,
// stowed 0x800710DD). These callbacks run guarded instead: the exception is written to
// last-crash-studio.txt and exceptions.log with where it came from, and the Studio carries on.
internal static class UiDispatch
{
    public static bool Post(this DispatcherQueue queue, Action action, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => queue.TryEnqueue(() => Guarded(action, file, line));

    public static bool Post(this DispatcherQueue queue, DispatcherQueuePriority priority, Action action, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => queue.TryEnqueue(priority, () => Guarded(action, file, line));

    public static void OnTick(this DispatcherQueueTimer timer, Action action, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => timer.Tick += (_, _) => Guarded(action, file, line);

    private static void Guarded(Action action, string file, int line)
    {
        try { action(); }
        catch (Exception ex) { App.WriteCrash(new InvalidOperationException($"UI callback {Path.GetFileName(file)}:{line} failed; the Studio kept running.", ex)); }
    }
}
