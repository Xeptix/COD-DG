using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace CODDowngrader.Gui;

/// <summary>A view model the window binds to: it says when a property changes.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A page with a Back button: Esc, Alt+Left and the mouse's back button do what it does, when it can.</summary>
public interface IHasBack
{
    Command BackCommand { get; }
}

/// <summary>A button's command. An asynchronous one cannot be pressed again while it runs.</summary>
public sealed class Command : ICommand
{
    readonly Func<object?, Task> _run;
    readonly Func<object?, bool>? _can;
    bool _running;

    public Command(Action run, Func<bool>? can = null) : this(_ => { run(); return Task.CompletedTask; }, can is null ? null : _ => can())
    {
    }

    public Command(Func<Task> run, Func<bool>? can = null) : this(_ => run(), can is null ? null : _ => can())
    {
    }

    public Command(Func<object?, Task> run, Func<object?, bool>? can = null)
    {
        _run = run;
        _can = can;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (_can?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        Changed();
        try
        {
            await _run(parameter);
        }
        finally
        {
            _running = false;
            Changed();
        }
    }

    public void Changed() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
