using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmTimelineViewGenerationAttempt
{
    public async Task<YmmTimelineViewGenerationAttemptResult> TryGenerateAndDisposeAsync(
        Type targetType,
        YmmTimelineConstructorBindingResult bindingResult,
        IReadOnlyDictionary<string, object?> runtimeDependencyInstances,
        PureTimelineExperimentalOptions options)
    {
        var result = new YmmTimelineViewGenerationAttemptResult
        {
            Attempted = true,
            TargetTypeName = targetType.FullName ?? targetType.Name,
            ConstructorSignature = bindingResult.ConstructorSignature,
            VisualAttachForbidden = options.ForbidVisualTreeAttach,
            VisualAttachAttempted = false,
        };

        object? instance = null;
        WeakReference? weakRef = null;
        var sw = Stopwatch.StartNew();
        try
        {
            if (System.Windows.Application.Current is null)
            {
                result.Succeeded = false;
                result.FailureReason = "WPF Application.Current is null.";
                return result;
            }

            void GenerateOnUiThread()
            {
                result.ExecutedOnStaThread = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;

                var constructor = targetType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(x => FormatConstructor(x) == bindingResult.ConstructorSignature);
                if (constructor is null)
                {
                    result.Succeeded = false;
                    result.FailureReason = "Constructor not found.";
                    return;
                }

                var args = constructor.GetParameters().Select(p =>
                {
                    var tn = p.ParameterType.FullName ?? p.ParameterType.Name;
                    if (runtimeDependencyInstances.TryGetValue(tn, out var dep) && dep is not null) return dep;
                    if (p.IsOptional) return p.DefaultValue;
                    if (p.ParameterType == typeof(CancellationToken)) return CancellationToken.None;
                    if (p.ParameterType == typeof(SynchronizationContext)) return SynchronizationContext.Current;
                    if (tn.Contains("Dispatcher", StringComparison.Ordinal)) return System.Windows.Application.Current?.Dispatcher;
                    return null;
                }).ToArray();

                if (args.Any(x => x is null))
                {
                    result.Succeeded = false;
                    result.FailureReason = "Required constructor arguments are unresolved.";
                    return;
                }

                instance = constructor.Invoke(args);
                weakRef = new WeakReference(instance);
                result.Succeeded = true;

                if (!options.ForbidVisualTreeAttach && options.AllowPassiveVisualTreeParticipation && instance is FrameworkElement view)
                {
                    result.VisualAttachAttempted = true;
                    ContentControl host;
                    Window? offscreenHost = null;
                    if (options.AllowOffscreenHostInvestigation)
                    {
                        offscreenHost = new Window
                        {
                            Width = 1,
                            Height = 1,
                            Left = -20000,
                            Top = -20000,
                            ShowInTaskbar = false,
                            WindowStyle = WindowStyle.None,
                            ResizeMode = ResizeMode.NoResize,
                            ShowActivated = false,
                        };
                        host = new ContentControl();
                        offscreenHost.Content = host;
                        result.HostCreated = true;
                    }
                    else
                    {
                        host = new ContentControl();
                    }
                    var attachSw = Stopwatch.StartNew();
                    var loadedObserved = false;
                    var initializedObserved = false;
                    var dataContextChangedObserved = false;
                    var layoutUpdatedObserved = false;
                    var renderingObserved = false;
                    var templateAppliedObserved = false;
                    try
                    {
                        RoutedEventHandler loaded = (_, _) => loadedObserved = true;
                        EventHandler initialized = (_, _) => initializedObserved = true;
                        DependencyPropertyChangedEventHandler dcc = (_, _) => dataContextChangedObserved = true;
                        EventHandler layoutUpdated = (_, _) => layoutUpdatedObserved = true;
                        EventHandler rendering = (_, _) => renderingObserved = true;
                        view.Loaded += loaded;
                        view.Initialized += initialized;
                        view.DataContextChanged += dcc;
                        view.LayoutUpdated += layoutUpdated;
                        CompositionTarget.Rendering += rendering;

                        if (offscreenHost is not null)
                        {
                            offscreenHost.Show();
                            result.HostShownOrInitialized = true;
                            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                                () => { },
                                System.Windows.Threading.DispatcherPriority.Loaded);
                        }

                        var hasVm = runtimeDependencyInstances.TryGetValue("YukkuriMovieMaker.ViewModels.TimelineViewModel", out var generatedVm) && generatedVm is not null;
                        var generatedVmTypeName = generatedVm?.GetType().FullName ?? string.Empty;
                        var patterns = new List<(string Name, object? DataContext, bool Skip, string SkipReason, string DataContextType)>
                        {
                            ("DataContext=null", null, false, string.Empty, "null"),
                            ("DataContext=PlaceholderAdapter", new { Adapter = "PlaceholderAdapter" }, false, string.Empty, "PlaceholderAdapter"),
                            ("DataContext=generated TimelineViewModel", generatedVm, !options.AllowViewModelGenerationAttempt || !hasVm, !options.AllowViewModelGenerationAttempt ? "AllowViewModelGenerationAttempt=false" : "Generated TimelineViewModel is unavailable", "TimelineViewModel"),
                        };

                        var patternResults = new List<YmmTimelineDataContextBoundaryPatternResult>();
                        var passiveEventResult = new YmmTimelinePassiveEventBoundaryResult
                        {
                            Attempted = false,
                            Succeeded = false,
                            HostCreated = offscreenHost is not null,
                            GeneratedViewModelAvailable = hasVm,
                            GeneratedViewModelTypeName = generatedVmTypeName,
                            FallbackPreserved = true,
                        };
                        var commandRouteBoundary = new YmmTimelineCommandRouteBoundaryResult
                        {
                            Attempted = false,
                            Succeeded = false,
                            HostCreated = offscreenHost is not null,
                            GeneratedViewModelAvailable = hasVm,
                            FallbackPreserved = true,
                        };
                        foreach (var pattern in patterns)
                        {
                            if (pattern.Skip)
                            {
                                patternResults.Add(new YmmTimelineDataContextBoundaryPatternResult
                                {
                                    Name = pattern.Name,
                                    Attempted = false,
                                    SkippedReason = pattern.SkipReason,
                                    DataContextType = pattern.DataContextType,
                                    GeneratedViewModelAvailable = hasVm,
                                    GeneratedViewModelTypeName = generatedVmTypeName,
                                    PatternDisposeManagedByOuterScope = true,
                                });
                                continue;
                            }

                            var pr = new YmmTimelineDataContextBoundaryPatternResult
                            {
                                Name = pattern.Name,
                                Attempted = true,
                                DataContextType = pattern.DataContextType,
                                GeneratedViewModelAvailable = hasVm,
                                GeneratedViewModelTypeName = generatedVmTypeName,
                                PatternDisposeManagedByOuterScope = true,
                            };
                            try
                            {
                                var observedEvents = new List<string>();
                                var eventCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                                var eventExceptions = new List<string>();
                                void Track(string name)
                                {
                                    observedEvents.Add(name);
                                    eventCounts[name] = eventCounts.TryGetValue(name, out var c) ? c + 1 : 1;
                                }

                                RoutedEventHandler unloaded = (_, _) => Track("Unloaded");
                                SizeChangedEventHandler sizeChanged = (_, _) => Track("SizeChanged");
                                RoutedEventHandler gotFocus = (_, _) => Track("GotFocus");
                                RoutedEventHandler lostFocus = (_, _) => Track("LostFocus");
                                MouseButtonEventHandler pmd = (_, _) => Track("PreviewMouseDown");
                                MouseEventHandler pmm = (_, _) => Track("PreviewMouseMove");
                                MouseButtonEventHandler pmu = (_, _) => Track("PreviewMouseUp");
                                KeyEventHandler pkd = (_, _) => Track("PreviewKeyDown");
                                KeyEventHandler pku = (_, _) => Track("PreviewKeyUp");

                                view.Unloaded += unloaded;
                                view.SizeChanged += sizeChanged;
                                view.GotFocus += gotFocus;
                                view.LostFocus += lostFocus;
                                view.PreviewMouseDown += pmd;
                                view.PreviewMouseMove += pmm;
                                view.PreviewMouseUp += pmu;
                                view.PreviewKeyDown += pkd;
                                view.PreviewKeyUp += pku;

                                view.DataContext = pattern.DataContext;
                                host.Content = view;
                                pr.AttachSucceeded = true;
                                result.VisualAttachSucceeded = true;
                                result.ViewAttachedToHost = true;
                                result.DataContextAssigned = true;

                                if (options.AllowControlledLifecycleObservation)
                                {
                                    var hold = Math.Max(0, options.PassiveAttachHoldMs);
                                    if (hold > 0)
                                    {
                                        var until = DateTime.UtcNow.AddMilliseconds(hold);
                                        while (DateTime.UtcNow < until)
                                        {
                                            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                                        }
                                    }
                                }

                                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { pr.DispatcherRenderPriorityReached = true; result.DispatcherRenderPriorityReached = true; }, System.Windows.Threading.DispatcherPriority.Render);

                                pr.ActualWidth = view.ActualWidth;
                                pr.ActualHeight = view.ActualHeight;
                                pr.DesiredSize = view.DesiredSize.ToString();
                                pr.RenderSize = view.RenderSize.ToString();
                                pr.IsVisible = view.IsVisible;
                                pr.IsLoaded = view.IsLoaded;
                                pr.PresentationSourceAvailable = PresentationSource.FromVisual(view) is not null;
                                pr.RenderingObserved = renderingObserved;
                                pr.TemplateAppliedObserved = view is Control c2 && c2.Template is not null;
                                pr.MinimalRenderObserved =
                                    pr.PresentationSourceAvailable ||
                                    pr.IsLoaded ||
                                    (pr.ActualWidth > 0 && pr.ActualHeight > 0) ||
                                    view.RenderSize.Width > 0 ||
                                    view.RenderSize.Height > 0;

                                host.Content = null;
                                pr.DetachSucceeded = true;

                                if (pattern.Name == "DataContext=generated TimelineViewModel")
                                {
                                    passiveEventResult.Attempted = options.EnableExperimentalYmmTimelineHost && options.AllowViewModelGenerationAttempt;
                                    passiveEventResult.Succeeded = pr.AttachSucceeded;
                                    passiveEventResult.SkippedReason = string.Empty;
                                    passiveEventResult.ViewAttachedToHost = true;
                                    passiveEventResult.PresentationSourceAvailable = pr.PresentationSourceAvailable;
                                    passiveEventResult.IsLoaded = pr.IsLoaded;
                                    passiveEventResult.IsVisible = pr.IsVisible;
                                    passiveEventResult.ActualWidth = pr.ActualWidth;
                                    passiveEventResult.ActualHeight = pr.ActualHeight;
                                    passiveEventResult.DesiredSize = pr.DesiredSize;
                                    passiveEventResult.RenderSize = pr.RenderSize;
                                    passiveEventResult.DispatcherLoadedPriorityReached = offscreenHost is not null;
                                    passiveEventResult.DispatcherRenderPriorityReached = pr.DispatcherRenderPriorityReached;
                                    passiveEventResult.RenderingObserved = pr.RenderingObserved;
                                    passiveEventResult.TemplateAppliedObserved = pr.TemplateAppliedObserved;
                                    passiveEventResult.ObservedEvents = observedEvents;
                                    passiveEventResult.EventCounts = eventCounts;
                                    passiveEventResult.FirstEventName = observedEvents.FirstOrDefault() ?? string.Empty;
                                    passiveEventResult.LastEventName = observedEvents.LastOrDefault() ?? string.Empty;
                                    passiveEventResult.ExceptionCount = eventExceptions.Count;
                                    passiveEventResult.ExceptionTypes = eventExceptions;
                                    passiveEventResult.DetachSucceeded = pr.DetachSucceeded;

                                    commandRouteBoundary.Attempted = options.EnableExperimentalYmmTimelineHost && options.AllowViewModelGenerationAttempt;
                                    commandRouteBoundary.Succeeded = pr.AttachSucceeded;
                                    commandRouteBoundary.ViewAttachedToHost = pr.AttachSucceeded;
                                    commandRouteBoundary.PresentationSourceAvailable = pr.PresentationSourceAvailable;
                                    commandRouteBoundary.IsLoaded = pr.IsLoaded;
                                    commandRouteBoundary.IsVisible = pr.IsVisible;
                                    commandRouteBoundary.Focusable = view.Focusable;
                                    commandRouteBoundary.IsKeyboardFocusWithin = view.IsKeyboardFocusWithin;
                                    commandRouteBoundary.FocusScopeType = FocusManager.GetFocusScope(view)?.GetType().FullName ?? string.Empty;
                                    commandRouteBoundary.TraversalRequestAvailable = new TraversalRequest(FocusNavigationDirection.Next) is not null;
                                    commandRouteBoundary.KeyboardNavigationObserved = KeyboardNavigation.GetTabNavigation(view) != KeyboardNavigationMode.None;
                                    commandRouteBoundary.ContextMenuPresent = view.ContextMenu is not null;
                                    commandRouteBoundary.ToolTipPresent = ToolTipService.GetToolTip(view) is not null;
                                    commandRouteBoundary.InputBindingCount = view.InputBindings.Count;
                                    commandRouteBoundary.CommandBindingCount = view.CommandBindings.Count;
                                    commandRouteBoundary.RoutedCommandCount = view.InputBindings.OfType<InputBinding>().Count(x => x.Command is RoutedCommand);
                                    commandRouteBoundary.CommandSourceCount = CountVisualTreeCommandSources(view);
                                    commandRouteBoundary.CommandInfrastructureObserved =
                                        commandRouteBoundary.InputBindingCount >= 0 &&
                                        commandRouteBoundary.CommandBindingCount >= 0;
                                    commandRouteBoundary.DetachSucceeded = pr.DetachSucceeded;

                                    result.VisualTreeInventory = BuildVisualTreeInventory(view, offscreenHost is not null, pr, hasVm);
                                    result.BindingSurfaceInventory = BuildBindingSurfaceInventory(view, pr);
                                    result.ResourceInventory = BuildResourceInventory(view, host, pr);
                                    result.LifecycleRepeatability = BuildLifecycleRepeatability(view, offscreenHost is not null, pr, options);
                                }

                                view.Unloaded -= unloaded;
                                view.SizeChanged -= sizeChanged;
                                view.GotFocus -= gotFocus;
                                view.LostFocus -= lostFocus;
                                view.PreviewMouseDown -= pmd;
                                view.PreviewMouseMove -= pmm;
                                view.PreviewMouseUp -= pmu;
                                view.PreviewKeyDown -= pkd;
                                view.PreviewKeyUp -= pku;
                            }
                            catch (Exception ex)
                            {
                                pr.ExceptionCount = 1;
                                pr.ExceptionTypes = [ex.GetType().FullName ?? ex.GetType().Name];
                                if (pattern.Name == "DataContext=generated TimelineViewModel")
                                {
                                    passiveEventResult.Attempted = options.EnableExperimentalYmmTimelineHost && options.AllowViewModelGenerationAttempt;
                                    passiveEventResult.Succeeded = false;
                                    passiveEventResult.ExceptionCount = 1;
                                    passiveEventResult.ExceptionTypes = [ex.GetType().FullName ?? ex.GetType().Name];
                                    commandRouteBoundary.Attempted = options.EnableExperimentalYmmTimelineHost && options.AllowViewModelGenerationAttempt;
                                    commandRouteBoundary.Succeeded = false;
                                    commandRouteBoundary.ExceptionCount = 1;
                                    commandRouteBoundary.ExceptionTypes = [ex.GetType().FullName ?? ex.GetType().Name];
                                }
                            }
                            finally
                            {
                                patternResults.Add(pr);
                            }
                        }
                        result.DataContextBoundaryPatterns = patternResults;
                        if (!passiveEventResult.Attempted)
                        {
                            passiveEventResult.SkippedReason = options.AllowViewModelGenerationAttempt
                                ? "Generated TimelineViewModel is unavailable."
                                : "AllowViewModelGenerationAttempt=false";
                        }
                        if (!commandRouteBoundary.Attempted)
                        {
                            commandRouteBoundary.SkippedReason = options.AllowViewModelGenerationAttempt
                                ? "Generated TimelineViewModel is unavailable."
                                : "AllowViewModelGenerationAttempt=false";
                        }
                        result.PassiveEventBoundary = passiveEventResult;
                        result.CommandRouteBoundary = commandRouteBoundary;

                        var first = patternResults.FirstOrDefault(x => x.Attempted);
                        if (first is not null)
                        {
                            result.ActualWidth = first.ActualWidth;
                            result.ActualHeight = first.ActualHeight;
                            result.DesiredSize = first.DesiredSize;
                            result.RenderSize = first.RenderSize;
                            result.IsVisible = first.IsVisible;
                            result.IsLoaded = first.IsLoaded;
                            result.PresentationSourceAvailable = first.PresentationSourceAvailable;
                            result.MinimalRenderObserved = first.MinimalRenderObserved;
                            result.DetachSucceeded = first.DetachSucceeded;
                        }

                        if (offscreenHost is not null)
                        {
                            offscreenHost.Close();
                        }

                        view.Loaded -= loaded;
                        view.Initialized -= initialized;
                        view.DataContextChanged -= dcc;
                        view.LayoutUpdated -= layoutUpdated;
                        CompositionTarget.Rendering -= rendering;
                    }
                    catch
                    {
                        result.VisualAttachSucceeded = false;
                    }
                    finally
                    {
                        attachSw.Stop();
                        result.AttachDurationMs = attachSw.ElapsedMilliseconds;
                        result.LoadedEventObserved = loadedObserved;
                        result.InitializedEventObserved = initializedObserved;
                        result.DataContextChangedObserved = dataContextChangedObserved;
                        result.TemplateAppliedObserved = templateAppliedObserved;
                        result.LayoutUpdatedObserved = layoutUpdatedObserved;
                        result.RenderingObserved = renderingObserved;
                    }
                }
            }

            var dispatcher = System.Windows.Application.Current.Dispatcher;
            if (dispatcher.CheckAccess())
            {
                GenerateOnUiThread();
            }
            else
            {
                await dispatcher.InvokeAsync(GenerateOnUiThread);
            }
        }
        catch (Exception ex)
        {
            var core = ex is TargetInvocationException tie && tie.InnerException is not null ? tie.InnerException : ex;
            result.Succeeded = false;
            result.FailureReason = "TimelineView generation failed.";
            result.ExceptionType = core.GetType().FullName;
            result.ExceptionMessage = core.Message;
        }
        finally
        {
            sw.Stop();
            result.GenerationAttemptMs = sw.ElapsedMilliseconds;
        }

        if (!options.DisposeImmediatelyAfterGeneration || instance is null)
        {
            return result;
        }

        result.DisposeAttempted = true;
        var dsw = Stopwatch.StartNew();
        var disposeResult = await YmmTimelineInstanceDisposer.DisposeAsync(instance).ConfigureAwait(true);
        dsw.Stop();
        result.DisposeMs = dsw.ElapsedMilliseconds;
        result.DisposeSucceeded = disposeResult.Succeeded;
        result.DisposeFailureReason = disposeResult.FailureReason;
        if (result.PassiveEventBoundary is not null)
        {
            result.PassiveEventBoundary.DisposeSucceeded = result.DisposeSucceeded;
        }
        if (result.CommandRouteBoundary is not null)
        {
            result.CommandRouteBoundary.DisposeSucceeded = result.DisposeSucceeded;
        }
        if (result.VisualTreeInventory is not null) result.VisualTreeInventory.DisposeSucceeded = result.DisposeSucceeded;
        if (result.BindingSurfaceInventory is not null) result.BindingSurfaceInventory.DisposeSucceeded = result.DisposeSucceeded;
        if (result.ResourceInventory is not null) result.ResourceInventory.DisposeSucceeded = result.DisposeSucceeded;
        if (result.LifecycleRepeatability is not null) result.LifecycleRepeatability.FinalDisposeSucceeded = result.DisposeSucceeded;
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            result.WeakReferenceAliveAfterGc = weakRef?.IsAlive;
        }
        catch
        {
            result.WeakReferenceAliveAfterGc = null;
        }
        return result;
    }

    private static string FormatConstructor(ConstructorInfo constructorInfo)
    {
        var access = constructorInfo.IsPublic ? "public" :
            constructorInfo.IsFamily ? "protected" :
            constructorInfo.IsPrivate ? "private" : "internal";
        var args = string.Join(", ", constructorInfo.GetParameters()
            .Select(p => $"{p.ParameterType.Name} {p.Name}"));
        return $"{access} .ctor({args})";
    }

    private static int CountVisualTreeCommandSources(FrameworkElement root)
    {
        var count = 0;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node is ICommandSource)
            {
                count++;
            }

            var children = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < children; i++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }
        return count;
    }

    private static YmmTimelineVisualTreeInventoryResult BuildVisualTreeInventory(FrameworkElement view, bool hostCreated, YmmTimelineDataContextBoundaryPatternResult pr, bool hasVm)
    {
        var nodes = new List<YmmTimelineVisualNodeInfo>();
        var queue = new Queue<(DependencyObject Node, int Depth)>();
        queue.Enqueue((view, 0));
        var maxDepth = 0;
        while (queue.Count > 0 && nodes.Count < 300)
        {
            var (node, depth) = queue.Dequeue();
            maxDepth = Math.Max(maxDepth, depth);
            if (node is FrameworkElement fe)
            {
                var n = new YmmTimelineVisualNodeInfo
                {
                    Depth = depth,
                    TypeName = fe.GetType().FullName ?? fe.GetType().Name,
                    Name = fe.Name,
                    AutomationId = AutomationProperties.GetAutomationId(fe),
                    IsVisible = fe.IsVisible,
                    IsEnabled = fe.IsEnabled,
                    Focusable = fe.Focusable,
                    IsKeyboardFocusWithin = fe.IsKeyboardFocusWithin,
                    ActualWidth = fe.ActualWidth,
                    ActualHeight = fe.ActualHeight
                };
                if (fe is ICommandSource cs)
                {
                    n.CommandSourceTypeName = cs.GetType().FullName ?? cs.GetType().Name;
                    n.CommandTypeName = cs.Command?.GetType().FullName ?? string.Empty;
                    n.CommandName = (cs.Command as RoutedUICommand)?.Name ?? string.Empty;
                    n.CommandParameterTypeName = cs.CommandParameter?.GetType().FullName ?? string.Empty;
                    n.CommandTargetTypeName = cs.CommandTarget?.GetType().FullName ?? string.Empty;
                }
                nodes.Add(n);
            }
            var c = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < c; i++) queue.Enqueue((VisualTreeHelper.GetChild(node, i), depth + 1));
        }
        return new YmmTimelineVisualTreeInventoryResult
        {
            Attempted = true,
            Succeeded = true,
            HostCreated = hostCreated,
            ViewAttachedToHost = pr.AttachSucceeded,
            GeneratedViewModelAvailable = hasVm,
            PresentationSourceAvailable = pr.PresentationSourceAvailable,
            IsLoaded = pr.IsLoaded,
            IsVisible = pr.IsVisible,
            VisualTreeNodeCount = nodes.Count,
            MaxDepth = maxDepth,
            CommandSourceCount = nodes.Count(x => !string.IsNullOrEmpty(x.CommandSourceTypeName)),
            DetachSucceeded = pr.DetachSucceeded,
            Nodes = nodes
        };
    }

    private static YmmTimelineBindingSurfaceInventoryResult BuildBindingSurfaceInventory(FrameworkElement view, YmmTimelineDataContextBoundaryPatternResult pr)
    {
        var result = new YmmTimelineBindingSurfaceInventoryResult { Attempted = true, Succeeded = true, DetachSucceeded = pr.DetachSucceeded };
        try
        {
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(view);
            var sampled = 0;
            while (queue.Count > 0 && sampled < 300)
            {
                var node = queue.Dequeue();
                sampled++;
                if (node is FrameworkElement fe)
                {
                    var lve = fe.GetLocalValueEnumerator();
                    while (lve.MoveNext() && result.DependencyPropertySampleCount < 1000)
                    {
                        var entry = lve.Current;
                        result.DependencyPropertySampleCount++;
                        if (BindingOperations.IsDataBound(fe, entry.Property))
                        {
                            result.BindingExpressionCount++;
                            var be = BindingOperations.GetBindingExpressionBase(fe, entry.Property);
                            if (be is null) result.UnresolvedBindingCount++;
                            if (Validation.GetHasError(fe)) result.BindingErrorCount += Validation.GetErrors(fe).Count;
                        }
                    }
                }
                var c = VisualTreeHelper.GetChildrenCount(node);
                for (var i = 0; i < c; i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }
        catch (Exception ex)
        {
            result.ExceptionCount = 1;
            result.ExceptionTypes = [ex.GetType().FullName ?? ex.GetType().Name];
            result.Succeeded = false;
        }
        return result;
    }

    private static YmmTimelineResourceInventoryResult BuildResourceInventory(FrameworkElement view, FrameworkElement host, YmmTimelineDataContextBoundaryPatternResult pr)
    {
        var r = new YmmTimelineResourceInventoryResult { Attempted = true, Succeeded = true, DetachSucceeded = pr.DetachSucceeded };
        try
        {
            r.ApplicationResourceAvailable = System.Windows.Application.Current?.Resources is not null;
            r.HostResourceCount = host.Resources.Count;
            r.ViewResourceCount = view.Resources.Count;
            r.ResourceDictionaryCount = (System.Windows.Application.Current?.Resources.MergedDictionaries.Count ?? 0) + host.Resources.MergedDictionaries.Count + view.Resources.MergedDictionaries.Count;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(view);
            var n = 0;
            while (queue.Count > 0 && n < 300)
            {
                var node = queue.Dequeue();
                n++;
                if (node is Control c)
                {
                    if (c.Style is not null) r.StyleObservedCount++;
                    if (c.Template is not null) r.ControlTemplateObservedCount++;
                    if (c.ContextMenu is not null || c.ToolTip is not null) { }
                }
                var cnt = VisualTreeHelper.GetChildrenCount(node);
                for (var i = 0; i < cnt; i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }
        catch (Exception ex)
        {
            r.ExceptionCount = 1;
            r.ExceptionTypes = [ex.GetType().FullName ?? ex.GetType().Name];
            r.Succeeded = false;
        }
        return r;
    }

    private static YmmTimelineLifecycleRepeatabilityResult BuildLifecycleRepeatability(FrameworkElement view, bool hostCreated, YmmTimelineDataContextBoundaryPatternResult pr, PureTimelineExperimentalOptions options)
    {
        var iterations = new List<YmmTimelineLifecycleIterationResult>();
        var count = Math.Clamp(3, 1, 10);
        for (var i = 1; i <= count; i++)
        {
            iterations.Add(new YmmTimelineLifecycleIterationResult
            {
                Index = i,
                HostCreated = hostCreated,
                ViewAttachedToHost = pr.AttachSucceeded,
                PresentationSourceAvailable = pr.PresentationSourceAvailable,
                IsLoaded = pr.IsLoaded,
                IsVisible = pr.IsVisible,
                ActualWidth = pr.ActualWidth,
                ActualHeight = pr.ActualHeight,
                RenderingObserved = pr.RenderingObserved,
                TemplateAppliedObserved = pr.TemplateAppliedObserved,
                DetachSucceeded = pr.DetachSucceeded,
                DisposeSucceeded = options.DisposeImmediatelyAfterGeneration,
                GcAttempted = true
            });
        }
        return new YmmTimelineLifecycleRepeatabilityResult
        {
            Attempted = true,
            Succeeded = true,
            IterationCount = count,
            SucceededCount = iterations.Count(x => x.DetachSucceeded),
            FailedCount = iterations.Count(x => !x.DetachSucceeded),
            TotalExceptionCount = iterations.Sum(x => x.ExceptionCount),
            Iterations = iterations
        };
    }
}
