namespace YMMProjectManager.Presentation.Views;

public partial class ProgressPopupWindow : Window
{
    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(
            nameof(Message),
            typeof(string),
            typeof(ProgressPopupWindow),
            new PropertyMetadata("処理を実行しています..."));

    public static readonly DependencyProperty PercentProperty =
        DependencyProperty.Register(
            nameof(Percent),
            typeof(double),
            typeof(ProgressPopupWindow),
            new PropertyMetadata(0d));

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public ProgressPopupWindow()
    {
        InitializeComponent();
    }
}
