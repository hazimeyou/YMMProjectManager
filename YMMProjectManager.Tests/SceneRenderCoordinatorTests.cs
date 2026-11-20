using YMMProjectManager.Application.PreviewComparison;

internal static class SceneRenderCoordinatorTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid Revision = Guid.NewGuid();
    private static readonly Guid Configuration = Guid.NewGuid();
    private static readonly Guid StateVersion = Guid.NewGuid();
    private static readonly SceneSnapshot A = Snapshot(24, 48);
    private static readonly SceneSnapshot B = Snapshot(30, 30);
    private static readonly TimeSpan Timestamp = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    public static async Task RunAsync()
    {
        await CaseAsync("TL-2B serial replay, owned bytes and fresh operations", TestSequence);
        await CaseAsync("TL-2B stale host, project revision and Scene lifetime", TestStaleObservations);
        await CaseAsync("TL-2B stale state during source Open and Render", TestInvalidationWithinCapture);
        await CaseAsync("TL-2B incorrect Scene, snapshot, frame and operation receipts", TestReceipts);
        await CaseAsync("TL-2B fail closed for replay mismatch and invalid buffer format", TestReplayAndFormat);
        await CaseAsync("TL-2B cooperative cancellation and timeout with cleanup", TestCancellationAndTimeout);
        await Program.RunNamedCaseAsync("TL-2B overlapping and reentrant requests fail fast", TestBusyAsync);
        await CaseAsync("TL-2B render, disposal and observation failures preserve evidence", TestFailures);
        await CaseAsync("TL-2B unobservable and changed editor state fail closed", TestState);
        await CaseAsync("TL-2B experimental Pbgra32 conversion policy", TestExperimentalConversion);
    }

    private static Task CaseAsync(string name, Action operation) => Program.RunNamedCaseAsync(name, () =>
    {
        operation();
        return Task.CompletedTask;
    });

    private static SceneSnapshot Snapshot(int fps, int count) =>
        new(Session, Project, Revision, Guid.NewGuid(), Guid.NewGuid(), new(Guid.NewGuid(), Configuration, new(fps, 1), count));

    private static SceneComparisonPlan Plan(int height = 1) =>
        SceneComparisonPlan.Prepare(Timestamp, A, B, 1, height).Plan
        ?? throw new InvalidOperationException("Expected a valid synthetic plan.");

    private static void TestSequence()
    {
        var coordinator = new SceneRenderCoordinator(Session);
        var template = Plan();
        var operationIds = new HashSet<Guid> { template.A.OperationId, template.B.OperationId };
        for (int i = 0; i < 2; i++)
        {
            var host = new Host();
            var result = coordinator.Run(template, host, Budget);
            Assert(host.Opened.Select(x => x.Snapshot).SequenceEqual(new[] { A, B, A }), "Wrong Scene order.");
            Assert(host.Opened.Select(x => x.FrameIndex).SequenceEqual(new[] { 12, 15, 12 }), "Wrong frame order.");
            foreach (var operation in host.Opened)
            {
                Assert(operation.OperationId != Guid.Empty && operationIds.Add(operation.OperationId) &&
                    operation.Timestamp == Timestamp, "An operation reused identity or changed the timestamp.");
            }

            Assert(host.DisposedCount == 3 && host.Buffers.All(x => x.All(value => value == 0)), "A borrowed buffer outlived disposal.");
            Assert(host.Events[0] == "observe" && host.Events[^1] == "observe", "The run was not bracketed by observations.");
            Assert(result.Error == PreviewComparisonError.None &&
                result.Metrics == new PreviewImageMetrics(1, 1, 1, 20, 20),
                "Disposal destroyed input pixels or altered the comparison.");
        }
    }

    private static void TestStaleObservations()
    {
        var faults = new (SceneSnapshot Snapshot, SceneHandoffError Error)[]
        {
            (B with { SceneLifetimeId = Guid.NewGuid() }, SceneHandoffError.SceneLifetimeMismatch),
            (B with { ProjectRevisionId = Guid.NewGuid() }, SceneHandoffError.ProjectRevisionMismatch),
            (B with { HostSessionId = Guid.NewGuid() }, SceneHandoffError.HostSessionMismatch),
        };
        var coordinator = new SceneRenderCoordinator(Session);
        foreach (var (changed, error) in faults)
        {
            var host = new Host { EditObservation = observation => observation with { B = changed } };
            ExpectRejection(() => coordinator.Run(Plan(), host, Budget), error, PreviewComparisonSide.B);
            Assert(host.Opened.Count == 0, $"Opened a Scene despite {error} before rendering.");

            var expires = new Host();
            expires.EditObservation = observation => expires.DisposedCount == 3
                ? observation with { B = changed }
                : observation;
            ExpectRejection(() => coordinator.Run(Plan(), expires, Budget), error, PreviewComparisonSide.B);
            Assert(expires.DisposedCount == 3 && expires.Events[^1] == "observe", $"Published despite {error} after rendering.");
        }

        var otherSession = new SceneRenderCoordinator(Guid.NewGuid());
        var wrongHost = new Host();
        var exception = Expect<SceneRenderException>(() => otherSession.Run(Plan(), wrongHost, Budget));
        Assert(exception.Kind == SceneRenderFailure.HandoffRejected &&
            exception.Rejection?.Error == SceneHandoffError.HostSessionMismatch && wrongHost.Opened.Count == 0,
            "A host coordinator processed a foreign session.");
    }

    private static void TestInvalidationWithinCapture()
    {
        var coordinator = new SceneRenderCoordinator(Session);
        var stale = A with { SceneLifetimeId = Guid.NewGuid() };
        var duringOpen = new Host();
        duringOpen.EditObservation = observation => duringOpen.Opened.Count == 0
            ? observation : observation with { A = stale };

        ExpectRejection(() => coordinator.Run(Plan(), duringOpen, Budget), SceneHandoffError.SceneLifetimeMismatch,
            PreviewComparisonSide.A);
        Assert(duringOpen.Opened.Count == 1 && duringOpen.DisposedCount == 1 &&
            !duringOpen.Events.Contains("render"), "A source invalidated during Open reached Render or escaped disposal.");

        using var sentinel = new CancellationTokenSource();
        using var unreadable = new CancelOnSpanAccess(new byte[6], sentinel);
        var duringRender = new Host { EditResponse = (response, _) => response with { Pixels = unreadable.Buffer } };
        duringRender.EditObservation = observation => !duringRender.Events.Contains("render")
            ? observation : observation with { A = stale };

        ExpectRejection(() => coordinator.Run(Plan(), duringRender, Budget), SceneHandoffError.SceneLifetimeMismatch,
            PreviewComparisonSide.A);
        Assert(unreadable.AccessCount == 0 && duringRender.Opened.Count == 1 && duringRender.DisposedCount == 1,
            "A source invalidated during Render reached pixel access or escaped disposal.");
    }
    private static void TestReceipts()
    {
        var coordinator = new SceneRenderCoordinator(Session);
        var faults = new (Func<SceneRenderResponse, SceneRenderResponse> Edit, SceneHandoffError Error)[]
        {
            (receipt => receipt with { ObservedSnapshot = A with { SceneId = B.SceneId } }, SceneHandoffError.SnapshotMismatch),
            (receipt => receipt with { ObservedSnapshot = A with { Source = A.Source with { SnapshotId = Guid.NewGuid() } } },
                SceneHandoffError.SnapshotMismatch),
            (receipt => receipt with { FrameIndex = 13 }, SceneHandoffError.FrameMismatch),
            (receipt => receipt with { OperationId = Guid.NewGuid() }, SceneHandoffError.OperationMismatch),
        };
        foreach (var (edit, error) in faults)
        {
            var host = new Host { EditResponse = (response, _) => edit(response) };
            ExpectRejection(() => coordinator.Run(Plan(), host, Budget), error, PreviewComparisonSide.A);
            Assert(host.Opened.Count == 1 && host.DisposedCount == 1 && host.Events[^1] == "observe",
                $"Continued after {error} or skipped cleanup.");
        }
    }

    private static void TestReplayAndFormat()
    {
        var coordinator = new SceneRenderCoordinator(Session);
        var host = new Host();
        host.EditResponse = (response, _) => host.Opened.Count == 3
            ? response with { Pixels = new byte[] { 21, 0, 0, 255, 97, 98 } }
            : response;
        ExpectKind(() => coordinator.Run(Plan(), host, Budget), SceneRenderFailure.ReplayMismatch);
        Assert(host.DisposedCount == 3 && host.Events[^1] == "observe", "Mismatched replay escaped cleanup.");

        var premultiplied = new Host { EditResponse = (response, _) => response with { Format = PreviewPixelFormat.Pbgra32 } };
        ExpectRejection(() => coordinator.Run(Plan(), premultiplied, Budget), SceneHandoffError.UnsupportedPixelFormat,
            PreviewComparisonSide.A);
        var badStride = new Host { EditResponse = (response, _) => response with { Stride = 3 } };
        ExpectRejection(() => coordinator.Run(Plan(), badStride, Budget), SceneHandoffError.InvalidStride,
            PreviewComparisonSide.A);
    }

    private static void TestCancellationAndTimeout()
    {
        var clock = new ManualClock();
        var coordinator = new SceneRenderCoordinator(Session, clock);
        using var cancellation = new CancellationTokenSource();
        var host = new Host { EditResponse = (response, _) => { cancellation.Cancel(); return response; } };
        Expect<OperationCanceledException>(() => coordinator.Run(Plan(), host, Budget, cancellation.Token));
        Assert(host.Opened.Count == 1 && host.DisposedCount == 1 && host.Events[^1] == "observe",
            "Cancellation was ignored or skipped final observation and disposal.");

        var timeout = new Host { EditResponse = (response, _) => { clock.Advance(Budget); return response; } };
        Expect<TimeoutException>(() => coordinator.Run(Plan(), timeout, Budget));
        Assert(timeout.Opened.Count == 1 && timeout.DisposedCount == 1 && timeout.Events[^1] == "observe",
            "Cooperative timeout entered the next leg or skipped final observation and disposal.");
        Assert(coordinator.Run(Plan(), new Host(), Budget).Metrics?.ChangedPixels == 1, "Timeout or cancellation poisoned retries.");
    }

    private static async Task TestBusyAsync()
    {
        var coordinator = new SceneRenderCoordinator(Session, new ManualClock());
        var plan = Plan();
        var competingHost = new Host();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var host = new Host();
        host.EditResponse = (response, _) =>
        {
            if (host.Opened.Count == 1)
            {
                ExpectKind(() => coordinator.Run(plan, competingHost, Budget), SceneRenderFailure.Busy);
                entered.Set();
                if (!release.Wait(Budget))
                    throw new TimeoutException("The concurrent test did not release rendering.");
            }

            return response;
        };

        var running = Task.Factory.StartNew(() => coordinator.Run(plan, host, Budget),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert(entered.Wait(Budget), "The first test render did not enter.");
            await Task.Run(() => ExpectKind(() => coordinator.Run(plan, competingHost, Budget), SceneRenderFailure.Busy))
                .WaitAsync(Budget);
            Assert(competingHost.Events.Count == 0, "A rejected operation touched the host.");
        }
        finally
        {
            release.Set();
            await running.WaitAsync(Budget);
        }

        Assert(running.Result.Metrics?.ChangedPixels == 1 && host.DisposedCount == 3,
            "The admitted operation did not finish after rejecting competitors.");
    }

    private static void TestFailures()
    {
        var coordinator = new SceneRenderCoordinator(Session);
        var renderError = new InvalidOperationException("render fault");
        var disposeError = new InvalidOperationException("dispose fault");
        var observeError = new InvalidOperationException("observe fault");
        var rendering = new Host { EditResponse = (_, _) => throw renderError };
        Assert(ReferenceEquals(Expect<InvalidOperationException>(() => coordinator.Run(Plan(), rendering, Budget)), renderError),
            "The original render failure was lost.");
        Assert(rendering.DisposedCount == 1 && rendering.Events[^1] == "observe" &&
            coordinator.Run(Plan(), new Host(), Budget).Metrics?.ChangedPixels == 1,
            "Render failure skipped cleanup or prevented retry after successful disposal.");

        var disposal = new Host { OnDispose = () => throw disposeError };
        Assert(ReferenceEquals(Expect<InvalidOperationException>(() => coordinator.Run(Plan(), disposal, Budget)), disposeError),
            "A disposal failure was masked.");
        Assert(disposal.Opened.Count == 1 && disposal.Events[^1] == "observe", "Disposal failure was ignored.");
        var retry = new Host();
        ExpectKind(() => coordinator.Run(Plan(), retry, Budget), SceneRenderFailure.CleanupFailed);
        Assert(retry.Events.Count == 0, "A source was accessed after a cleanup failure.");

        coordinator = new SceneRenderCoordinator(Session);
        var compound = new Host { EditResponse = (_, _) => throw renderError, OnDispose = () => throw disposeError };
        compound.EditObservation = observation => compound.DisposedCount > 0 ? throw observeError : observation;
        var aggregate = Expect<AggregateException>(() => coordinator.Run(Plan(), compound, Budget)).Flatten().InnerExceptions;
        Assert(aggregate.Count == 3 && aggregate.Contains(renderError) && aggregate.Contains(disposeError) &&
            aggregate.Contains(observeError), "A compound failure dropped or duplicated evidence.");
        Assert(compound.Opened.Count == 1 && compound.DisposedCount == 1, "Compound failure skipped resource cleanup.");
        ExpectKind(() => coordinator.Run(Plan(), new Host(), Budget), SceneRenderFailure.CleanupFailed);
    }

    private static void TestState()
    {
        var coordinator = new SceneRenderCoordinator(Session);
        foreach (Guid? invalid in new Guid?[] { null, Guid.Empty })
        {
            var host = new Host { EditObservation = observation => observation with { EditorStateVersion = invalid } };
            ExpectKind(() => coordinator.Run(Plan(), host, Budget), SceneRenderFailure.UnobservableState);
            Assert(host.Opened.Count == 0, "Opened a Scene without a valid editor-state observation.");
        }

        var version = Guid.NewGuid();
        var changed = new Host();
        changed.EditObservation = observation => changed.DisposedCount > 0
            ? observation with { EditorStateVersion = version }
            : observation;
        ExpectKind(() => coordinator.Run(Plan(), changed, Budget), SceneRenderFailure.StateChanged);
        Assert(changed.Opened.Count == 1 && changed.DisposedCount == 1 && changed.Events[^1] == "observe",
            "Change in editing state entered the next leg or skipped cleanup.");

        var fault = new InvalidOperationException("baseline failed");
        var failed = new Host { EditObservation = _ => throw fault };
        ExpectOnly(() => coordinator.Run(Plan(), failed, Budget), error => ReferenceEquals(error, fault),
            "The observer fault was masked.");
        Assert(failed.Opened.Count == 0, "An unavailable observer allowed rendering.");
    }

    private static void TestExperimentalConversion()
    {
        var plan = Plan(height: 3);
        byte[] input = [90, 80, 70, 255, 4, 5, 0, 0, 64, 128, 6, 7, 0, 0, 0, 0, 8, 9];
        byte[] expected = [90, 80, 70, 255, 0, 0, 128, 128, 0, 0, 0, 0];
        var response = new SceneRenderResponse(plan.A.OperationId, A, 12, 1, 3, 6, PreviewPixelFormat.Pbgra32, input);
        var converted = SceneCapturedFrame.CopyExperimentalPbgra32(plan, PreviewComparisonSide.A, response);
        var straight = SceneCapturedFrame.Copy(plan, PreviewComparisonSide.B,
            new(plan.B.OperationId, B, 15, 1, 3, 4, PreviewPixelFormat.Bgra32, expected));
        Assert(converted.Frame is not null && straight.Frame is not null && input[8] == 64 &&
            input[10] == 6 && plan.Compare(converted.Frame, straight.Frame, A, B).Result?.Metrics?.ExactlyEqual == true,
            "Experimental conversion altered borrowed bytes, padding, alpha or rounded channels.");
        Assert(SceneCapturedFrame.Copy(plan, PreviewComparisonSide.A, response).Error == SceneHandoffError.UnsupportedPixelFormat,
            "Production capture accepted premultiplied pixels.");

        input[14] = 1;
        Assert(SceneCapturedFrame.CopyExperimentalPbgra32(plan, PreviewComparisonSide.A, response).Error ==
            SceneHandoffError.InvalidPremultipliedPixel, "Accepted nonzero RGB at zero alpha.");
        input[14] = 0;
        input[8] = 129;
        Assert(SceneCapturedFrame.CopyExperimentalPbgra32(plan, PreviewComparisonSide.A, response).Error ==
            SceneHandoffError.InvalidPremultipliedPixel, "Accepted a channel larger than alpha.");

        using var cancel = new CancellationTokenSource();
        using var borrowed = new CancelOnSpanAccess(input, cancel);
        Expect<OperationCanceledException>(() => SceneCapturedFrame.CopyExperimentalPbgra32(plan, PreviewComparisonSide.A,
            response with { Pixels = borrowed.Buffer }, cancel.Token));
    }

    private static void ExpectRejection(Action operation, SceneHandoffError error, PreviewComparisonSide side)
    {
        ExpectOnly(operation,
            exception => exception is SceneRenderException { Kind: SceneRenderFailure.HandoffRejected } failure &&
                failure.Rejection == new SceneHandoffRejection(error, side),
            $"Expected {error} on side {side}.");
    }

    private static void ExpectKind(Action operation, SceneRenderFailure kind) =>
        ExpectOnly(operation, exception => exception is SceneRenderException failure && failure.Kind == kind, $"Expected {kind}.");

    private static void ExpectOnly(Action operation, Func<Exception, bool> predicate, string message)
    {
        var exception = Expect<Exception>(operation);
        IEnumerable<Exception> errors = exception is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : new[] { exception };
        Assert(errors.Any() && errors.All(predicate), $"{message} Got {exception}.");
    }

    private static T Expect<T>(Action operation) where T : Exception
    {
        try { operation(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan amount) => timestamp += amount.Ticks;
    }

    private sealed class Host : ISceneRenderContext
    {
        public List<SceneRenderRequest> Opened { get; } = [];
        public List<string> Events { get; } = [];
        public List<byte[]> Buffers { get; } = [];
        public int DisposedCount { get; private set; }
        public Func<SceneRenderObservation, SceneRenderObservation>? EditObservation { get; set; }
        public Func<SceneRenderResponse, CancellationToken, SceneRenderResponse>? EditResponse { get; set; }
        public Action? OnDispose { get; init; }

        public SceneRenderObservation Observe()
        {
            Events.Add("observe");
            var observation = new SceneRenderObservation(A, B, StateVersion);
            return EditObservation?.Invoke(observation) ?? observation;
        }

        public ISceneRenderSource Open(SceneRenderRequest request)
        {
            Assert(Opened.Count == DisposedCount, "Two render sources were open at once.");
            Opened.Add(request);
            Events.Add("open");
            return new Source(this, request);
        }

        private sealed class Source(Host host, SceneRenderRequest request) : ISceneRenderSource
        {
            private readonly byte[] pixels = [request.Snapshot == A ? (byte)20 : (byte)40, 0, 0, 255, 97, 98];
            private bool disposed;

            public SceneRenderResponse Render(CancellationToken cancellationToken)
            {
                Assert(!disposed, "Rendered using a disposed source.");
                host.Events.Add("render");
                host.Buffers.Add(pixels);
                var response = new SceneRenderResponse(request.OperationId, request.Snapshot, request.FrameIndex,
                    request.Width, request.Height, 6, PreviewPixelFormat.Bgra32, pixels);
                return host.EditResponse?.Invoke(response, cancellationToken) ?? response;
            }

            public void Dispose()
            {
                Assert(!disposed, "Source was disposed twice.");
                disposed = true;
                Array.Clear(pixels);
                host.DisposedCount++;
                host.Events.Add("dispose");
                host.OnDispose?.Invoke();
            }
        }
    }
}
