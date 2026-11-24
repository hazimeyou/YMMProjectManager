using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;
using YMMProjectManager.Infrastructure;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;

namespace YMMProjectManager.Infrastructure.Output;

internal interface ILiveThumbnailSource : IDisposable
{
    int Length { get; }
    int Frame { get; }
    TimeSpan Position { get; }
    TimeSpan TimeAtFrame(int frame);
    bool IsCurrent();
    bool MatchesLoadedProject(SafeFileHandle projectFile);
    Task SeekAsync(TimeSpan position);
    BitmapSource Capture();
}

/// <summary>Thumbnail capture is allowed only when model, timeline and player have the same owner.</summary>
internal sealed class LiveThumbnailSource : ILiveThumbnailSource
{
    // The private host links below were inspected in the matching reference assembly.
    private const string InspectedHostBuild = "4.57.0.1+f7f1e4a691d0b476c5693586b4be2ce0c9b13a3c";
    private const BindingFlags InstanceField = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private readonly Window mainView;
    private readonly object mainViewModel;
    private readonly object model;
    private readonly object previewArea;
    private readonly object preview;
    private readonly object player;
    private readonly TimelineToolInfo timelineInfo;
    private readonly string projectPath;
    private readonly HostMembers members;
    private readonly INotifyPropertyChanged modelChanges;
    private readonly INotifyPropertyChanged previewAreaChanges;
    private readonly INotifyPropertyChanged previewChanges;
    private readonly INotifyPropertyChanged playerChanges;
    private int invalidated;
    private long expectedPositionTicks = long.MinValue;

    private LiveThumbnailSource(Window mainView, object mainViewModel, object model, object previewArea, object preview,
        object player, TimelineToolInfo timelineInfo, string path, HostMembers members)
    {
        this.mainView = mainView;
        this.mainViewModel = mainViewModel;
        this.model = model;
        this.previewArea = previewArea;
        this.preview = preview;
        this.player = player;
        this.timelineInfo = timelineInfo;
        projectPath = path;
        this.members = members;
        modelChanges = (INotifyPropertyChanged)model;
        previewAreaChanges = (INotifyPropertyChanged)previewArea;
        previewChanges = (INotifyPropertyChanged)preview;
        playerChanges = (INotifyPropertyChanged)player;
        modelChanges.PropertyChanged += OnModelChanged;
        previewAreaChanges.PropertyChanged += OnPreviewAreaChanged;
        previewChanges.PropertyChanged += OnPreviewChanged;
        playerChanges.PropertyChanged += OnPlayerChanged;
        mainView.DataContextChanged += OnDataContextChanged;
        mainView.Unloaded += OnUnloaded;
        timelineInfo.UndoRedoManager.HistoryChanged += OnHistoryChanged;
    }

    public int Length => timelineInfo.Timeline.Length;
    public int Frame
    {
        get
        {
            var timeline = timelineInfo.Timeline;
            var currentFrame = timeline.CurrentFrame;
            return timeline.VideoInfo.GetFrameFrom(Position) == currentFrame ? currentFrame : -1;
        }
    }
    public TimeSpan Position => (TimeSpan)members.Position.GetValue(player)!;
    public TimeSpan TimeAtFrame(int frame) => timelineInfo.Timeline.VideoInfo.GetTimeFrom(frame);

    public bool MatchesLoadedProject(SafeFileHandle projectFile)
    {
        // This build exposes a path and a saved flag, but no file identity or revision
        // from the load. A replacement at that path can coexist with the old live model.
        return false;
    }

    internal static LiveThumbnailSource? TryBind(string projectPath, TimelineToolInfo timelineInfo)
    {
        var app = System.Windows.Application.Current;
        if (app is null || !app.Dispatcher.CheckAccess() ||
            !ReferenceEquals(TimelineContextService.Info, timelineInfo) || timelineInfo.Timeline is null || timelineInfo.UndoRedoManager is null)
            return null;

        var members = HostMembers.TryResolve(typeof(TimelineToolInfo).Assembly);
        if (members is null)
            return null;

        var matches = app.Windows.Cast<Window>()
            .Where(window => window.GetType() == members.MainView && window.IsVisible && window.IsLoaded &&
                window.DataContext?.GetType() == members.MainViewModel)
            .Select(window => (Window: window, ViewModel: window.DataContext, Model: members.Model.GetValue(window.DataContext)))
            .Where(item => item.Model is INotifyPropertyChanged &&
                ReferenceEquals(members.ModelTimeline.GetValue(item.Model), timelineInfo.Timeline) &&
                members.ModelSaved.GetValue(item.Model) is true &&
                IsSamePath(members.ModelPath.GetValue(item.Model), projectPath))
            .Take(2).ToArray();
        if (matches.Length != 1)
            return null;

        var host = matches[0];
        // This is the main preview area used by YMM's own SeekPreviewAsync. Scene previews are separate.
        if (members.PreviewArea.GetValue(host.ViewModel) is not INotifyPropertyChanged area ||
            members.PreviewAreaVisible.GetValue(area) is not true ||
            members.PreviewAreaViewModel.GetValue(area) is not INotifyPropertyChanged preview ||
            preview.GetType() != members.PreviewViewModel ||
            members.Player.GetValue(preview) is not INotifyPropertyChanged player ||
            player.GetType() != members.Player.FieldType ||
            !ReferenceEquals(members.PreviewTimeline.GetValue(preview), timelineInfo.Timeline) ||
            !ReferenceEquals(members.PlayerTimeline.GetValue(player), timelineInfo.Timeline) ||
            !ReferenceEquals(members.PlayerScenes.GetValue(player), timelineInfo.Scenes))
            return null;

        return new LiveThumbnailSource(host.Window, host.ViewModel, host.Model!, area, preview, player,
            timelineInfo, projectPath, members);
    }

    public bool IsCurrent()
    {
        return Volatile.Read(ref invalidated) == 0 &&
            ReferenceEquals(TimelineContextService.Info, timelineInfo) &&
            mainView.IsLoaded && mainView.IsVisible && ReferenceEquals(mainView.DataContext, mainViewModel) &&
            ReferenceEquals(members.PreviewArea.GetValue(mainViewModel), previewArea) &&
            members.PreviewAreaVisible.GetValue(previewArea) is true &&
            ReferenceEquals(members.PreviewAreaViewModel.GetValue(previewArea), preview) &&
            ReferenceEquals(members.Model.GetValue(mainViewModel), model) &&
            ReferenceEquals(members.ModelTimeline.GetValue(model), timelineInfo.Timeline) &&
            members.ModelSaved.GetValue(model) is true &&
            IsSamePath(members.ModelPath.GetValue(model), projectPath) &&
            ReferenceEquals(members.Player.GetValue(preview), player) &&
            ReferenceEquals(members.PreviewTimeline.GetValue(preview), timelineInfo.Timeline) &&
            ReferenceEquals(members.PlayerTimeline.GetValue(player), timelineInfo.Timeline) &&
            ReferenceEquals(members.PlayerScenes.GetValue(player), timelineInfo.Scenes) &&
            members.IsPlaying.GetValue(preview) is false && members.IsCrashed.GetValue(preview) is false;
    }

    public async Task SeekAsync(TimeSpan position)
    {
        Interlocked.Exchange(ref expectedPositionTicks, position.Ticks);
        try
        {
            var seek = (Task)(members.Seek.Invoke(player, [position]) ??
                throw new InvalidOperationException("YMM のフレーム移動結果を確認できません。"));
            await seek.ConfigureAwait(true);
        }
        finally { Interlocked.Exchange(ref expectedPositionTicks, long.MinValue); }
    }

    public BitmapSource Capture()
    {
        // The inspected GetBitmap overload creates a new System.Drawing.Bitmap per call.
        using var ownedBitmap = members.GetBitmap.Invoke(player, [true]) as IDisposable
            ?? throw new InvalidOperationException("YMM の描画結果を確認できません。");
        return YmmPreviewBitmapCaptureAdapter.TryNormalizeBitmap(ownedBitmap, out var bitmap, out _, out _, out _)
            ? bitmap : throw new InvalidOperationException("YMM のプレビューを画像に変換できません。");
    }

    private static bool IsSamePath(object? activePath, string selectedPath)
    {
        if (activePath is not string path || !Path.IsPathFullyQualified(path))
            return false;

        var full = Path.GetFullPath(path);
        if (string.Equals(full, selectedPath, StringComparison.Ordinal))
            return true;
        if (!string.Equals(full, selectedPath, StringComparison.OrdinalIgnoreCase))
            return false;

        // Do not assume the host's volume treats differently cased paths as aliases.
        using var activeParent = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(full)!, enforceShare: true);
        using var selectedParent = OwnedFilePublication.DirectoryFence.Open(Path.GetDirectoryName(selectedPath)!, enforceShare: true);
        using var activeFile = OwnedFilePublication.OpenOrdinaryPath(full, expectDirectory: false, allowRename: false);
        using var selectedFile = OwnedFilePublication.OpenOrdinaryPath(selectedPath, expectDirectory: false, allowRename: false);
        return ProjectFileEvidence.ReadPhysicalIdentity(activeFile) == ProjectFileEvidence.ReadPhysicalIdentity(selectedFile);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName is "Timeline" or "ProjectFilePath" or "IsProjectFileSaved")
            Invalidate();
    }

    private void OnPreviewAreaChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName is "ViewModel" or "IsVisible")
            Invalidate();
    }

    private void OnHistoryChanged(object? sender, EventArgs args) => Invalidate();

    private void OnPreviewChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName is "Host" or "IsPlaying" or "IsCrashed")
            Invalidate();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == "IsPlaying")
            Invalidate();
        else if (args.PropertyName == "Position" && Position.Ticks != Interlocked.Read(ref expectedPositionTicks))
            Invalidate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => Invalidate();
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs args) => Invalidate();
    private void Invalidate() => Interlocked.Exchange(ref invalidated, 1);

    public void Dispose()
    {
        modelChanges.PropertyChanged -= OnModelChanged;
        previewAreaChanges.PropertyChanged -= OnPreviewAreaChanged;
        timelineInfo.UndoRedoManager.HistoryChanged -= OnHistoryChanged;
        previewChanges.PropertyChanged -= OnPreviewChanged;
        playerChanges.PropertyChanged -= OnPlayerChanged;
        mainView.DataContextChanged -= OnDataContextChanged;
        mainView.Unloaded -= OnUnloaded;
    }

    private sealed record HostMembers(Type MainView, Type MainViewModel, Type PreviewViewModel,
        FieldInfo Model, PropertyInfo ModelTimeline, PropertyInfo ModelPath, PropertyInfo ModelSaved, FieldInfo PreviewArea,
        PropertyInfo PreviewAreaVisible, PropertyInfo PreviewAreaViewModel, FieldInfo PreviewTimeline,
        FieldInfo Player, FieldInfo PlayerTimeline, FieldInfo PlayerScenes, PropertyInfo IsPlaying,
        PropertyInfo IsCrashed, PropertyInfo Position, MethodInfo Seek, MethodInfo GetBitmap)
    {
        internal static HostMembers? TryResolve(Assembly assembly)
        {
            if (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion != InspectedHostBuild)
                return null;
            var mainView = assembly.GetType("YukkuriMovieMaker.Views.MainView");
            var mainViewModel = assembly.GetType("YukkuriMovieMaker.ViewModels.MainViewModel");
            var previewViewModel = assembly.GetType("YukkuriMovieMaker.ViewModels.PreviewViewModel");
            var model = mainViewModel?.GetField("model", InstanceField);
            var modelTimeline = model?.FieldType.GetProperty("Timeline");
            var previewArea = mainViewModel?.GetField("previewAreaViewModel", InstanceField);
            var previewAreaVisible = previewArea?.FieldType.GetProperty("IsVisible");
            var previewAreaViewModel = previewArea?.FieldType.GetProperty("ViewModel");
            var modelPath = model?.FieldType.GetProperty("ProjectFilePath");
            var modelSaved = model?.FieldType.GetProperty("IsProjectFileSaved");
            var previewTimeline = previewViewModel?.GetField("timeline", InstanceField);
            var player = previewViewModel?.GetField("player", InstanceField);
            var playerTimeline = player?.FieldType.BaseType?.GetField("timeline", InstanceField);
            var playerScenes = player?.FieldType.BaseType?.GetField("scenes", InstanceField);
            var isPlaying = previewViewModel?.GetProperty("IsPlaying");
            var isCrashed = previewViewModel?.GetProperty("IsCrashed");
            var position = player?.FieldType.GetProperty("Position");
            var seek = player?.FieldType.GetMethod("SeekAsync", [typeof(TimeSpan)]);
            var getBitmap = player?.FieldType.GetMethod("GetBitmap", [typeof(bool)]);
            if (mainView is null || mainViewModel is null || previewViewModel is null ||
                model is null || modelTimeline?.PropertyType != typeof(Timeline) || modelPath?.PropertyType != typeof(string) ||
                modelSaved?.PropertyType != typeof(bool) ||
                previewArea is null || previewAreaVisible?.PropertyType != typeof(bool) || previewAreaViewModel is null ||
                previewTimeline?.FieldType != typeof(Timeline) || player is null || playerTimeline?.FieldType != typeof(Timeline) ||
                playerScenes is null || isPlaying?.PropertyType != typeof(bool) || isCrashed?.PropertyType != typeof(bool) ||
                position?.PropertyType != typeof(TimeSpan) || seek?.ReturnType != typeof(Task) ||
                getBitmap?.ReturnType.FullName != "System.Drawing.Bitmap")
                return null;
            return new HostMembers(mainView, mainViewModel, previewViewModel, model, modelTimeline, modelPath, modelSaved,
                previewArea, previewAreaVisible, previewAreaViewModel,
                previewTimeline, player, playerTimeline, playerScenes, isPlaying, isCrashed, position, seek, getBitmap);
        }
    }
}
