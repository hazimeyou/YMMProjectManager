using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace YMMProjectManager.Presentation.ViewModels;

//CSJ,class=ViewModelBase,ビューモデル基点
public abstract class ViewModelBase : INotifyPropertyChanged
{
    //CSJ,event=PropertyChanged,プロパティ変更済み
    public event PropertyChangedEventHandler? PropertyChanged;

    //CSJ,method=SetProperty,プロパティを設定する
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

