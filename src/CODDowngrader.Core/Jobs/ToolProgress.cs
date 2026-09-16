using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CODDowngrader.App;

namespace CODDowngrader.Jobs;

/// <summary>
/// DepotDownloader's output, read as it arrives: every whole line goes on to <paramref name="log"/>, and the lines that say how
/// far a depot has got become progress. DepotDownloader downloads one depot after another: "Downloading depot N" as each
/// starts, "NN.NN% path" each time a file is done, where the percentage is how much of that depot is downloaded, and
/// "Depot N - Downloaded" when it is finished. With <see cref="Job.DownloadSizes"/> known, those add up to one bar across every
/// depot of the download; without, the bar is the depot under way.
/// </summary>
public sealed partial class ToolProgress : TextWriter
{
    readonly Job _job;
    readonly Action<string>? _log;
    readonly StringBuilder _line = new();
    readonly object _gate = new();
    readonly HashSet<uint> _finished = new();
    uint? _depot;

    /// <param name="log">Given every whole line of DepotDownloader's output.</param>
    public ToolProgress(Job job, Action<string>? log)
    {
        _job = job;
        _log = log;
    }

    public override Encoding Encoding => Encoding.UTF8;

    [GeneratedRegex(@"^\s*(\d{1,3}(?:\.\d+)?)%\s+(.+?)\s*$")]
    private static partial Regex PercentRegex();

    // "Downloading depot N manifest" comes first for every depot, before any of them downloads: it is not a depot starting.
    [GeneratedRegex(@"^Downloading depot (\d+)\s*$")]
    private static partial Regex DepotRegex();

    [GeneratedRegex(@"^Depot (\d+) - Downloaded")]
    private static partial Regex FinishedRegex();

    public override void Write(char value)
    {
        lock (_gate)
        {
            if (value == '\n')
            {
                Flush(_line.ToString().TrimEnd('\r'));
                _line.Clear();
            }
            else
            {
                _line.Append(value);
            }
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        for (var i = index; i < index + count; i++) Write(buffer[i]);
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        foreach (var c in value) Write(c);
    }

    void Flush(string line)
    {
        if (line.Length == 0) return;
        _log?.Invoke(line);

        if (FinishedRegex().Match(line) is { Success: true } finished
            && uint.TryParse(finished.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var done))
        {
            _finished.Add(done);
            if (_job.DownloadSizes is { Count: > 0 }) Report(null, 1);
            return;
        }
        if (DepotRegex().Match(line) is { Success: true } depot
            && uint.TryParse(depot.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var starting))
        {
            _depot = starting;
            Report(null, 0);
            return;
        }
        if (PercentRegex().Match(line) is { Success: true } percent
            && double.TryParse(percent.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            Report(Path.GetFileName(percent.Groups[2].Value), value / 100);
        }
    }

    /// <param name="file">The file just finished; null as a depot starts or finishes.</param>
    /// <param name="ofDepot">How much of the depot under way is done.</param>
    void Report(string? file, double ofDepot)
    {
        ofDepot = Math.Clamp(ofDepot, 0, 1);
        if (_job.DownloadSizes is { Count: > 0 } sizes && _depot is { } depot && sizes.TryGetValue(depot, out var size))
        {
            var total = sizes.Values.Aggregate(0.0, (sum, s) => sum + s);
            if (total > 0)
            {
                var before = sizes.Where(s => s.Key != depot && _finished.Contains(s.Key)).ToList();
                var done = before.Aggregate(0.0, (sum, s) => sum + s.Value) + (_finished.Contains(depot) ? size : ofDepot * size);
                var number = Math.Min(before.Count + 1, sizes.Count);
                var detail = $"{Format.Size((ulong)done)} of {Format.Size((ulong)total)} · depot {depot}, {number} of {sizes.Count}"
                             + (file is null ? "" : $": {file}");
                _job.Progress(new JobProgress("Downloading", detail, Math.Clamp(done / total, 0, 1)));
                return;
            }
        }
        var only = _depot is null ? file : file is null ? $"depot {_depot}" : $"depot {_depot}: {file}";
        _job.Progress(new JobProgress("Downloading", only, ofDepot));
    }
}
