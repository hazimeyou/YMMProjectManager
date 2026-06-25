using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Presentation.Checkpoint;

public partial class CheckpointCreateWindow : Window, INotifyPropertyChanged
{
    private string checkpointName = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm");
    private string? description;
    private string? comment;
    private CheckpointThumbnailMode selectedMode = CheckpointThumbnailMode.EvenSplit;
    private string customValue = "15";

    public CheckpointCreateWindow()
    {
        InitializeComponent();
        DataContext = this;
        ModeItems.Add(new ComboBoxItem { Content = "均等分割", Tag = CheckpointThumbnailMode.EvenSplit });
        ModeItems.Add(new ComboBoxItem { Content = "1秒ごと", Tag = CheckpointThumbnailMode.Every1Second });
        ModeItems.Add(new ComboBoxItem { Content = "5秒ごと", Tag = CheckpointThumbnailMode.Every5Seconds });
        ModeItems.Add(new ComboBoxItem { Content = "10秒ごと", Tag = CheckpointThumbnailMode.Every10Seconds });
        ModeItems.Add(new ComboBoxItem { Content = "30秒ごと", Tag = CheckpointThumbnailMode.Every30Seconds });
        ModeItems.Add(new ComboBoxItem { Content = "1分ごと", Tag = CheckpointThumbnailMode.Every1Minute });
        ModeItems.Add(new ComboBoxItem { Content = "5分ごと", Tag = CheckpointThumbnailMode.Every5Minutes });
        ModeItems.Add(new ComboBoxItem { Content = "任意秒数", Tag = CheckpointThumbnailMode.CustomSeconds });
        ModeItems.Add(new ComboBoxItem { Content = "任意分数", Tag = CheckpointThumbnailMode.CustomMinutes });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public List<ComboBoxItem> ModeItems { get; } = [];

    public string CheckpointName
    {
        get => checkpointName;
        set => SetField(ref checkpointName, value);
    }

    public string? Description
    {
        get => description;
        set => SetField(ref description, value);
    }

    public string? Comment
    {
        get => comment;
        set => SetField(ref comment, value);
    }

    public CheckpointThumbnailMode SelectedMode
    {
        get => selectedMode;
        set => SetField(ref selectedMode, value);
    }

    public string CustomValue
    {
        get => customValue;
        set => SetField(ref customValue, value);
    }

    public CheckpointThumbnailSettings BuildSettings()
    {
        _ = int.TryParse(CustomValue, out var value);
        return new CheckpointThumbnailSettings
        {
            Mode = SelectedMode,
            CustomValue = Math.Max(1, value),
            SampleCount = 64,
            IncludeLastFrame = true,
        };
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
