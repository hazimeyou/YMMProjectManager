using System;
using System.Windows.Input;

namespace YMMProjectManager.Presentation.Commands;

//CSJ,class=RelayCommand,処理委譲コマンド
public sealed class RelayCommand : ICommand
{
    //CSJ,field=execute,実行
    private readonly Action execute;
    //CSJ,field=canExecute,実行の可否
    private readonly Func<bool>? canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute;
    }

    //CSJ,event=CanExecuteChanged,実行可否の変更イベント
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    //CSJ,method=CanExecute,実行が可能かを判定する
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    //CSJ,method=Execute,実行する
    public void Execute(object? parameter) => execute();
}

