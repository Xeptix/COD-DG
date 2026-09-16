using CODDowngrader.Builds;

namespace CODDowngrader.Gui.ViewModels;

/// <summary>A build to hand to someone else: the text to paste or save, and what it holds.</summary>
public sealed class SharePageViewModel : Observable, IHasBack
{
    readonly MainViewModel _main;
    readonly SharedBuild _shared;
    string? _status;

    public SharePageViewModel(MainViewModel main, SharedBuild shared)
    {
        _main = main;
        _shared = shared;
        Text = shared.Text();

        CopyCommand = new Command(async () =>
        {
            await _main.Platform.CopyAsync(Text);
            Status = "Copied. Paste it wherever you are sending it.";
        });
        SaveCommand = new Command(SaveAsync);
        BackCommand = new Command(() => _main.Back());
    }

    public string Subtitle => $"{_shared.Title}{(_shared.PartLabel is { } part ? $", {part}" : "")} · {_shared.Game}";

    public string Holds
    {
        get
        {
            var depots = _shared.Manifests.Count == 1 ? "1 depot's manifest" : $"{_shared.Manifests.Count} depots' manifests";
            var part = _shared.PartLabel is { } label ? $", {label}" : ", every file that differs";
            return $"It names {depots}{part}{(_shared.Siblings ? ", and takes the games sharing the folder along" : "")}.";
        }
    }

    public string Text { get; }

    public string? Status
    {
        get => _status;
        private set
        {
            Set(ref _status, value);
            Raise(nameof(HasStatus));
        }
    }

    public bool HasStatus => _status is not null;

    public Command CopyCommand { get; }
    public Command SaveCommand { get; }
    public Command BackCommand { get; }

    async Task SaveAsync()
    {
        if (await _main.Platform.SaveFileAsync("Save the shared build", _shared.FileName) is not { } path) return;
        try
        {
            await File.WriteAllTextAsync(path, Text);
            Status = $"Saved to {path}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = $"It could not be saved: {e.Message}";
        }
    }
}
