using CODDowngrader.Patching;
using Spectre.Console;

namespace CODDowngrader.App;

public sealed partial class Interactive
{
    /// <summary>What of a build goes in. <paramref name="Label"/> names the part, such as "content only"; null for all of it.</summary>
    sealed record PartChoice(string? Label, Func<string, bool> Includes);

    sealed record Pick(string Name, string Label);

    static readonly PartChoice Everything = new(null, _ => true);

    /// <summary>
    /// Asks which of the files a plan changes go in: all of them, only the content, only the exes and DLLs, or files picked from a
    /// list. A plan inside one folder is taken whole. Null when the user goes back.
    /// </summary>
    static PartChoice? ChoosePart(PatchPlan plan, IReadOnlySet<string>? alreadyThere)
    {
        var files = plan.Writes.Where(w => alreadyThere is null || !alreadyThere.Contains(w.Name)).Select(w => (w.Name, Size: (ulong?)w.Size))
            .Concat(plan.Removes.Select(r => (r.Name, Size: (ulong?)null)))
            .OrderBy(f => f.Name, PathRules.Comparer)
            .ToList();
        if (files.Select(f => FolderOf(f.Name)).Distinct(PathRules.Comparer).Count() < 2) return Everything;

        var binaries = files.Where(f => PatchPlan.IsBinary(f.Name)).ToList();
        var items = new List<Item> { new("Everything that differs", "all") };
        if (binaries.Count > 0 && binaries.Count < files.Count)
        {
            var names = string.Join(", ", binaries.Take(3).Select(b => Path.GetFileName(b.Name))) + (binaries.Count > 3 ? $" and {binaries.Count - 3} more" : "");
            items.Add(new($"Only the content: keep your exes and DLLs ({Markup.Escape(names)})", "content"));
            items.Add(new("Only the exes and DLLs: keep your content", "binaries"));
        }
        items.Add(new("Choose folders and files", "custom"));
        items.Add(new("Back", "back"));

        switch (Prompt("Which of these files?", items).Kind)
        {
            case "all":
                return Everything;
            case "content":
                return new PartChoice("content only", name => !PatchPlan.IsBinary(name));
            case "binaries":
                return new PartChoice("exes and DLLs only", PatchPlan.IsBinary);
            case "back":
                return null;
        }

        var prompt = new MultiSelectionPrompt<Pick>()
            .Title("Which files? [grey](Space picks a file or a whole folder, Enter accepts)[/]")
            .PageSize(20)
            .Mode(SelectionMode.Leaf)
            .NotRequired()
            .UseConverter(p => p.Label);
        foreach (var folder in files.GroupBy(f => FolderOf(f.Name), PathRules.Comparer))
        {
            var picks = folder.Select(f => new Pick(f.Name, $"{Markup.Escape(f.Name)} [grey]{(f.Size is { } size ? Format.Size(size) : "removed")}[/]")).ToList();
            var bytes = folder.Aggregate(0UL, (sum, f) => sum + (f.Size ?? 0));
            var where = folder.Key.Length == 0 ? "The game folder" : Markup.Escape(folder.Key + "/");
            prompt.AddChoiceGroup(new Pick("", $"{where} [grey]{picks.Count} files, {Format.Size(bytes)}[/]"), picks);
            foreach (var pick in picks) prompt.Select(pick);
        }

        var chosen = new HashSet<string>(AnsiConsole.Prompt(prompt).Select(p => p.Name), PathRules.Comparer);
        return chosen.Count == files.Count ? Everything : new PartChoice("chosen files", chosen.Contains);
    }

    /// <summary>The top folder a build's file is in; empty for the game folder itself.</summary>
    static string FolderOf(string name) => name.IndexOf('/') is var cut and > 0 ? name[..cut] : "";

    static string TitleOf(string build, string? part) => part is null ? build : $"{build}, {part}";
}
