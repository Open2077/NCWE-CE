// SPDX-License-Identifier: GPL-3.0-only
namespace NCWE.Studio.Session;

// UI-thread stage timings between two automation reads: the average hides hitches,
// so each stage keeps its maximum too. Snapshot(reset) starts a new window.
internal static class FrameProfile
{
    private sealed class Stage { public double Sum, Max; public int Count; }
    private static readonly Dictionary<string, Stage> Stages = new(StringComparer.Ordinal);

    public static void Record(string name, double milliseconds)
    {
        if (!Stages.TryGetValue(name, out var stage)) Stages[name] = stage = new();
        stage.Sum += milliseconds; stage.Count++;
        if (milliseconds > stage.Max) stage.Max = milliseconds;
    }

    public static object Snapshot(bool reset)
    {
        var result = Stages.OrderBy(s => s.Key, StringComparer.Ordinal).ToDictionary(s => s.Key,
            s => new { avg = s.Value.Count == 0 ? 0 : Math.Round(s.Value.Sum / s.Value.Count, 2), max = Math.Round(s.Value.Max, 1), n = s.Value.Count });
        if (reset) Stages.Clear();
        return result;
    }
}
