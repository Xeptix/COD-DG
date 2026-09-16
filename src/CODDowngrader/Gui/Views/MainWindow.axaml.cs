using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CODDowngrader.Gui.ViewModels;

namespace CODDowngrader.Gui.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Handled) return;
            // Esc in a text box belongs to what is being typed.
            var escape = e.Key == Key.Escape && FocusManager?.GetFocusedElement() is not TextBox;
            var altLeft = e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt;
            if ((escape || altLeft) && GoBack()) e.Handled = true;
        };
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsXButton1Pressed && GoBack()) e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>What the page's Back button does, when it is there to press.</summary>
    bool GoBack()
    {
        if (DataContext is not MainViewModel { Page: IHasBack page } || !page.BackCommand.CanExecute(null)) return false;
        page.BackCommand.Execute(null);
        return true;
    }
}
