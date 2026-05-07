using System.Windows;
using System.Windows.Controls;
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
                        result.PassiveEventBoundary = passiveEventResult;

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
}
