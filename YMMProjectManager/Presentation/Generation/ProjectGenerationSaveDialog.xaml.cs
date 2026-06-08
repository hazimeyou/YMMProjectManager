using System.ComponentModel;
using System.Windows;

namespace YMMProjectManager.Presentation.Generation;

public partial class ProjectGenerationSaveDialog : Window, INotifyPropertyChanged
{
    private string displayName = string.Empty;
    private string memo = string.Empty;

    public ProjectGenerationSaveDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
