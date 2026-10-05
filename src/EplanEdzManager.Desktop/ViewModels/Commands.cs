using System.Windows.Input;

namespace EplanEdzManager.Desktop.ViewModels;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _isExecuting;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_isExecuting && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _isExecuting = true;
        RaiseCanExecuteChanged();
        try { await execute(); }
        finally { _isExecuting = false; RaiseCanExecuteChanged(); }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
