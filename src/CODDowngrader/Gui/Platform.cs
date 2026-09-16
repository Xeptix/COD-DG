using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace CODDowngrader.Gui;

/// <summary>What the view models need from the desktop: folder and file pickers, the clipboard, and opening a link or a folder.</summary>
public interface IPlatform
{
    Task<string?> PickFolderAsync(string title, string? start);

    /// <summary>A text file to open.</summary>
    Task<string?> PickFileAsync(string title);

    /// <summary>Where to save a text file, suggesting <paramref name="suggestedName"/>.</summary>
    Task<string?> SaveFileAsync(string title, string suggestedName);

    Task CopyAsync(string text);

    void Open(string target);
}

/// <summary>The desktop as the main window reaches it.</summary>
public sealed class WindowPlatform : IPlatform
{
    readonly TopLevel _top;

    public WindowPlatform(TopLevel top) => _top = top;

    public async Task<string?> PickFolderAsync(string title, string? start)
    {
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (start is { Length: > 0 } && await Task.Run(() => Directory.Exists(start)))
            options.SuggestedStartLocation = await _top.StorageProvider.TryGetFolderFromPathAsync(start);
        var picked = await _top.StorageProvider.OpenFolderPickerAsync(options);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    static readonly FilePickerFileType TextFiles = new("Text files") { Patterns = new[] { "*.txt" }, MimeTypes = new[] { "text/plain" } };

    public async Task<string?> PickFileAsync(string title)
    {
        var picked = await _top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { TextFiles, FilePickerFileTypes.All },
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName)
    {
        var file = await _top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "txt",
            FileTypeChoices = new[] { TextFiles },
        });
        return file?.TryGetLocalPath();
    }

    public async Task CopyAsync(string text)
    {
        if (_top.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    public void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }
}
