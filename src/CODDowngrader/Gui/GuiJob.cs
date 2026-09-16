using System.Text.Json.Nodes;
using Avalonia.Threading;
using CODDowngrader.App;
using CODDowngrader.Jobs;

namespace CODDowngrader.Gui;

/// <summary>What a job in the window reports to: a progress page, or nothing when the job only works something out.</summary>
public interface IJobView
{
    void Progress(JobProgress progress);

    void Log(string line);
}

/// <summary>
/// A job run from the window. It never asks anything in a console of its own: a sign-in opens DepotDownloader in a window of
/// its own, and everything else was chosen on the page before the job started.
/// </summary>
public sealed class GuiJob : Job
{
    readonly IJobView? _view;

    public GuiJob(string name, JobSettings settings, Options options, IJobView? view, CancellationToken cancel)
        : base(name, settings, options, cancel)
    {
        _view = view;
        ToolOutput = new ToolProgress(this, line => Post(v => v.Log(line)));
    }

    public List<string> Lines { get; } = new();

    public override bool CanPromptHere => false;

    public override TextWriter? ToolOutput { get; }

    public override void Line(string text = "")
    {
        lock (Lines) Lines.Add(text);
        if (text.Length > 0) Post(v => v.Log(text));
    }

    public override void Warn(string text) => Line(text);

    public override void Progress(JobProgress progress) => Post(v => v.Progress(progress));

    protected override void Failed(string message) => Post(v => v.Log(message));

    protected override void Finished(JsonObject result)
    {
    }

    void Post(Action<IJobView> action)
    {
        if (_view is { } view) Dispatcher.UIThread.Post(() => action(view));
    }
}
