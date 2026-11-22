using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace YMMProjectManager.Presentation.Commands;

//CSJ,class=AsyncRelayCommand,非同期処理委譲コマンド
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> executeAsync;
    //CSJ,field=canExecute,実行の可否
    private readonly Func<bool>? canExecute;
    private bool isExecuting;

    public AsyncRelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
    {
        this.executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        this.canExecute = canExecute;
    }

    //CSJ,event=CanExecuteChanged,実行可否の変更イベント
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    //CSJ,method=CanExecute,実行が可能かを判定する
    public bool CanExecute(object? parameter)
    {
        if (isExecuting)
        {
            return false;
        }

        return canExecute?.Invoke() ?? true;
    }

    //CSJ,method=Execute,実行する
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

