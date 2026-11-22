using System.ComponentModel;
using System.Windows;

namespace YMMProjectManager.Presentation.Generation;

//CSJ,class=ProjectGenerationSaveDialog,プロジェクト世代保存ダイアログ
public partial class ProjectGenerationSaveDialog : Window, INotifyPropertyChanged
{
    //CSJ,field=displayName,表示名
    private string displayName = string.Empty;
    //CSJ,field=memo,メモ
    private string memo = string.Empty;

    public ProjectGenerationSaveDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    //CSJ,property=DisplayName,表示名
    public string DisplayName
    {
        get => displayName;
        set
        {
            if (displayName != value)
            {
                displayName = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            }
        }
    }

    //CSJ,property=Memo,メモ
    public string Memo
    {
        get => memo;
        set
        {
            if (memo != value)
            {
                memo = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Memo)));
            }
        }
    }

    //CSJ,event=PropertyChanged,プロパティ変更済み
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
