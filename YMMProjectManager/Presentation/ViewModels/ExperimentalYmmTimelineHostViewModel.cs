using System.Windows;

namespace YMMProjectManager.Presentation.ViewModels;

public sealed class ExperimentalYmmTimelineHostViewModel : ViewModelBase, IDisposable
{
    private readonly Stopwatch initializeStopwatch = new();
    private readonly Stopwatch disposeStopwatch = new();
    private object? timelineView;
    private object? timelineViewModel;
    private string status = "Not initialized";
    private string summary = string.Empty;
    private bool disposed;

    public ObservableCollection<string> Logs { get; } = [];

    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    public string Summary
    {
        get => summary;
        private set => SetProperty(ref summary, value);
    }

    public FrameworkElement? TimelineViewElement { get; private set; }

    public long InitializeMs { get; private set; }
    public long DisposeMs { get; private set; }

    public bool TryInitialize(bool useReflection)
    {
        if (disposed)
        {
            Logs.Add("Initialization skipped: already disposed.");
            return false;
        }

        initializeStopwatch.Restart();
        Logs.Clear();
        Logs.Add($"Initialize start (useReflection={useReflection})");

        try
        {
            var timelineViewType = ResolveType("YukkuriMovieMaker.Views.TimelineView");
            if (timelineViewType is null)
            {
                Logs.Add("Type not found: YukkuriMovieMaker.Views.TimelineView");
                Status = "TypeMissing";
                return false;
            }

            Logs.Add($"Type found: {timelineViewType.FullName}");
            timelineView = Activator.CreateInstance(timelineViewType);
            TimelineViewElement = timelineView as FrameworkElement;
            Logs.Add(timelineView is null ? "TimelineView instance create failed." : "TimelineView instance created.");

            var timelineViewModelType = ResolveType("YukkuriMovieMaker.ViewModels.TimelineViewModel");
            if (timelineViewModelType is null)
            {
                Logs.Add("Type not found: YukkuriMovieMaker.ViewModels.TimelineViewModel");
                Status = "ViewModelTypeMissing";
                return false;
            }

            Logs.Add($"Type found: {timelineViewModelType.FullName}");
            var constructors = timelineViewModelType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            Logs.Add($"TimelineViewModel ctor count: {constructors.Length}");
            foreach (var ctor in constructors)
            {
                var args = string.Join(", ", ctor.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                Logs.Add($"Ctor: ({args})");
            }

            Status = "Initialized (partial)";
            Summary = "TimelineView created. TimelineViewModel constructor signatures collected.";
            return true;
        }
        catch (Exception ex)
        {
            Logs.Add($"Initialize exception: {ex.GetType().Name}: {ex.Message}");
            Status = "InitializeFailed";
            Summary = ex.Message;
            return false;
        }
        finally
        {
            initializeStopwatch.Stop();
            InitializeMs = initializeStopwatch.ElapsedMilliseconds;
            OnPropertyChanged(nameof(InitializeMs));
            OnPropertyChanged(nameof(TimelineViewElement));
            Logs.Add($"Initialize finished in {InitializeMs} ms");
        }
    }

    private static Type? ResolveType(string fullName)
    {
        return AppDomain.CurrentDomain
            .GetAssemblies()
            .Select(a => a.GetType(fullName, false))
            .FirstOrDefault(t => t is not null);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposeStopwatch.Restart();
        try
        {
            if (timelineViewModel is IDisposable vmDisposable)
            {
                vmDisposable.Dispose();
                Logs.Add("TimelineViewModel disposed.");
            }

            if (timelineView is IDisposable viewDisposable)
            {
                viewDisposable.Dispose();
                Logs.Add("TimelineView disposed.");
            }
        }
        catch (Exception ex)
        {
            Logs.Add($"Dispose exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            timelineView = null;
            timelineViewModel = null;
            TimelineViewElement = null;
            disposed = true;
            disposeStopwatch.Stop();
            DisposeMs = disposeStopwatch.ElapsedMilliseconds;
            OnPropertyChanged(nameof(DisposeMs));
            OnPropertyChanged(nameof(TimelineViewElement));
            Logs.Add($"Dispose finished in {DisposeMs} ms");
        }
    }
}
