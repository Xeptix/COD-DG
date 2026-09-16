using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CODDowngrader.App;
using CODDowngrader.Gui.ViewModels;
using CODDowngrader.Gui.Views;

namespace CODDowngrader.Gui;

/// <summary>The window's application: its styles and pages, and the main window once the platform is up.</summary>
public sealed partial class GuiApp : Application
{
    /// <summary>The options the command line opened the window with: a Steam folder, DepotDownloader, a game to open first.</summary>
    public static Options StartOptions { get; set; } = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var model = new MainViewModel(StartOptions, new WindowPlatform(window));
            window.DataContext = model;
            desktop.MainWindow = window;
            window.Opened += async (_, _) => await model.LoadAsync(StartOptions.AppId);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
