using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using YMMProjectManager.Infrastructure;
using YMMProjectManager.Infrastructure.Checkpoint;
using YMMProjectManager.Domain;

namespace YMMProjectManager.Presentation.Checkpoint;

public partial class CheckpointDiagnosticsWindow : Window, INotifyPropertyChanged
{
    private readonly CheckpointService checkpointService;
    private readonly string projectPath;
    private readonly string checkpointId;
    private string windowTitle = string.Empty;
    private string summaryText = string.Empty;

    public CheckpointDiagnosticsWindow(string projectPath, string checkpointId, FileLogger logger)
    {
        InitializeComponent();
        this.projectPath = projectPath;
        this.checkpointId = checkpointId;
        checkpointService = new CheckpointService(logger);
        DataContext = this;
        Loaded += OnLoaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<DiagnosticRow> Items { get; } = [];

    public string WindowTitle
    {
        get => windowTitle;
        private set
        {
            if (windowTitle != value)
            {
                windowTitle = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WindowTitle)));
            }
        }
    }

    public string SummaryText
    {
        get => summaryText;
        private set
        {
            if (summaryText != value)
            {
                summaryText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummaryText)));
            }
        }
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var result = await checkpointService.DiagnoseAsync(projectPath, checkpointId);
        WindowTitle = $"チェックポイント診断: {result.CheckpointName}";
        SummaryText = $"復元可能見込み: {(result.CanRestore ? "はい" : "いいえ")}\n保存先: {result.CheckpointDirectory}";

        Items.Clear();
        foreach (var item in result.Items)
        {
            Items.Add(new DiagnosticRow
            {
                Severity = item.Severity,
                SeverityText = item.Severity switch
                {
                    CheckpointDiagnosticSeverity.Ok => "OK",
                    CheckpointDiagnosticSeverity.Warning => "Warning",
                    _ => "Error",
                },
                Title = item.Title,
                Message = item.Message,
            });
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    public sealed class DiagnosticRow
    {
        public CheckpointDiagnosticSeverity Severity { get; set; }
        public string SeverityText { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }
}
