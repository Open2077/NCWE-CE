// SPDX-License-Identifier: GPL-3.0-only
namespace NCWE.Export;

// An export's progress as a window shows it: the stages it goes through (only those this project
// needs, each weighted by the time it usually takes), the current one, what it works on (a road's
// name, a file: words of the project, not of the exporter) and how far it is.
public sealed record ExportStage(string Key, string Title, double Weight);

public sealed record ExportProgress(IReadOnlyList<ExportStage> Stages, int Current, string Detail, int Done, int Total, double Fraction)
{
    // The current stage has no count of its own (its share moves when it ends).
    public bool Indeterminate => Total <= 0;
    public double StageFraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
    public ExportStage? Stage => Current >= 0 && Current < Stages.Count ? Stages[Current] : null;
}

// Reports an export's stages: planned first (stages without work are left out), then begun in
// order, each counting its items. Sends the stage view and, for the status line, the text.
public sealed class ExportTracker(IProgress<ExportProgress>? stages, IProgress<string>? text)
{
    private readonly List<ExportStage> _stages = [];
    private int _current = -1, _done, _total;
    private bool _estimated;
    private string _detail = "";

    public IReadOnlyList<ExportStage> Stages => _stages;
    // The status line alone (a part's own steps, which are not the stage's items).
    public IProgress<string>? Text => text;

    // A stage this export goes through (weight: its usual share of the time, 0: none to do).
    public ExportTracker Plan(string key, string title, double weight)
    {
        if (weight > 0) _stages.Add(new(key, title, weight));
        return this;
    }

    // Also after the exporter's own stages (a packaging step of the caller's).
    public void Append(string key, string title, double weight) => Plan(key, title, weight);

    // Starts a planned stage (skipped ones before it count as done); `total`: its items, 0 unknown;
    // `estimated`: a guess, raised when more items come.
    public void Begin(string key, int total = 0, string detail = "", bool estimated = false)
    {
        int index = _stages.FindIndex(s => s.Key == key);
        if (index < 0) return;
        _current = index; _done = 0; _total = Math.Max(0, total); _estimated = estimated; _detail = detail;
        Send(null);
    }

    // One more item of the current stage done or under way; `message`: the status line's text.
    public void Item(string detail, string? message = null)
    {
        _detail = detail;
        if (_total > 0)
        {
            _done = Math.Min(_total, _done + 1);
            if (_estimated && _done >= _total) _total = _done + Math.Max(1, _done / 4);
        }
        Send(message);
    }

    // What the current stage works on, without an item more.
    public void Detail(string detail, string? message = null) { _detail = detail; Send(message); }

    // More items for the current stage, once they are known (a stage of several parts).
    public void More(int items) { _total += Math.Max(0, items); Send(null); }

    // A text progress of a part (the terrain...) whose reports are the stage's items (their text
    // goes to the status line: the stage's title tells what it does).
    public IProgress<string> Messages() => new Progress(this);

    // The current stage is done: the next one waits (a caller's), or all are.
    public void Next(string? message = null)
    {
        _current = Math.Min(_stages.Count, _current + 1); _done = _total = 0; _estimated = false; _detail = "";
        if (message is not null) text?.Report(message);
        if (stages is null || _stages.Count == 0) return;
        double all = _stages.Sum(s => s.Weight);
        stages.Report(new(_stages.ToArray(), _current, "", 0, 0, Math.Clamp(_stages.Take(_current).Sum(s => s.Weight) / all, 0, 1)));
    }

    private void Send(string? message)
    {
        if (message is not null) text?.Report(message);
        if (stages is null || _stages.Count == 0 || _current < 0) return;
        double all = _stages.Sum(s => s.Weight), before = _stages.Take(_current).Sum(s => s.Weight);
        double within = _total > 0 ? (double)_done / _total : 0;
        stages.Report(new(_stages.ToArray(), _current, _detail, _done, _total, Math.Clamp((before + _stages[_current].Weight * within) / all, 0, 1)));
    }

    // Synchronous: parts report from worker threads, the stage view follows them in order.
    private sealed class Progress(ExportTracker tracker) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (tracker) tracker.Item("", value);
        }
    }
}
