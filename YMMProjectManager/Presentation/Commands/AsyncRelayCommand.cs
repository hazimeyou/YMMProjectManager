using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace YMMProjectManager.Presentation.Commands;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> executeAsync;
    private readonly Func<bool>? canExecute;
    private bool isExecuting;

    public AsyncRelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
    {
        this.executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter)
    {
        if (isExecuting)
        {
            return false;
        }

        return canExecute?.Invoke() ?? true;
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        isExecuting = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await executeAsync().ConfigureAwait(true);
        }
        finally
        {
            isExecuting = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

