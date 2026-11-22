using YMMProjectManager.Application.PreviewComparison;

//CSJ,class=SceneRenderCoordinatorTests,シーン描画調整役テスト
internal static class SceneRenderCoordinatorTests
{
    //CSJ,field=Session,セッション
    private static readonly Guid Session = Guid.NewGuid();
    //CSJ,field=Project,プロジェクト
    private static readonly Guid Project = Guid.NewGuid();
    //CSJ,field=Revision,版
    private static readonly Guid Revision = Guid.NewGuid();
    //CSJ,field=Configuration,構成
    private static readonly Guid Configuration = Guid.NewGuid();
    //CSJ,field=StateVersion,状態バージョン
    private static readonly Guid StateVersion = Guid.NewGuid();
    private static readonly SceneSnapshot A = Snapshot(24, 48);
    private static readonly SceneSnapshot B = Snapshot(30, 30);
    //CSJ,field=Timestamp,タイムスタンプ
    private static readonly TimeSpan Timestamp = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    //CSJ,method=RunAsync,実行する（非同期）
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

    //CSJ,method=TestSequence,順序をテストする
    private static void TestSequence()
    {
        //CSJ,variable=coordinator,調整役
        var coordinator = new SceneRenderCoordinator(Session);
        //CSJ,variable=template,テンプレート
        var template = Plan();
        //CSJ,variable=operationIds,操作ID一覧
        var operationIds = new HashSet<Guid> { template.A.OperationId, template.B.OperationId };
        for (int i = 0; i < 2; i++)
        {
            //CSJ,variable=host,ホスト
            var host = new Host();
            var result = coordinator.Run(template, host, Budget);
            Assert(host.Opened.Select(x => x.Snapshot).SequenceEqual(new[] { A, B, A }), "Wrong Scene order.");
            Assert(host.Opened.Select(x => x.FrameIndex).SequenceEqual(new[] { 12, 15, 12 }), "Wrong frame order.");
            //CSJ,variable=operation,操作
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
        //CSJ,variable=coordinator,調整役
        var coordinator = new SceneRenderCoordinator(Session);
        foreach (var (changed, error) in faults)
        {
            //CSJ,variable=host,ホスト
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
        //CSJ,variable=exception,例外
        var exception = Expect<SceneRenderException>(() => otherSession.Run(Plan(), wrongHost, Budget));
        Assert(exception.Kind == SceneRenderFailure.HandoffRejected &&
            exception.Rejection?.Error == SceneHandoffError.HostSessionMismatch && wrongHost.Opened.Count == 0,
            "A host coordinator processed a foreign session.");
    }

    private static void TestInvalidationWithinCapture()
    {
        //CSJ,variable=coordinator,調整役
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
        //CSJ,variable=coordinator,調整役
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
            //CSJ,variable=host,ホスト
            var host = new Host { EditResponse = (response, _) => edit(response) };
            ExpectRejection(() => coordinator.Run(Plan(), host, Budget), error, PreviewComparisonSide.A);
            Assert(host.Opened.Count == 1 && host.DisposedCount == 1 && host.Events[^1] == "observe",
                $"Continued after {error} or skipped cleanup.");
        }
    }

    private static void TestReplayAndFormat()
    {
        //CSJ,variable=coordinator,調整役
        var coordinator = new SceneRenderCoordinator(Session);
        //CSJ,variable=host,ホスト
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
        //CSJ,variable=clock,時計
        var clock = new ManualClock();
        //CSJ,variable=coordinator,調整役
        var coordinator = new SceneRenderCoordinator(Session, clock);
        //CSJ,variable=cancellation,キャンセル
        using var cancellation = new CancellationTokenSource();
        //CSJ,variable=host,ホスト
        var host = new Host { EditResponse = (response, _) => { cancellation.Cancel(); return response; } };
        Expect<OperationCanceledException>(() => coordinator.Run(Plan(), host, Budget, cancellation.Token));
        Assert(host.Opened.Count == 1 && host.DisposedCount == 1 && host.Events[^1] == "observe",
            "Cancellation was ignored or skipped final observation and disposal.");

        //CSJ,variable=timeout,タイムアウト
        var timeout = new Host { EditResponse = (response, _) => { clock.Advance(Budget); return response; } };
        Expect<TimeoutException>(() => coordinator.Run(Plan(), timeout, Budget));
        Assert(timeout.Opened.Count == 1 && timeout.DisposedCount == 1 && timeout.Events[^1] == "observe",
            "Cooperative timeout entered the next leg or skipped final observation and disposal.");
        Assert(coordinator.Run(Plan(), new Host(), Budget).Metrics?.ChangedPixels == 1, "Timeout or cancellation poisoned retries.");
    }

    private static async Task TestBusyAsync()
    {
        //CSJ,variable=coordinator,調整役
        var coordinator = new SceneRenderCoordinator(Session, new ManualClock());
        //CSJ,variable=plan,計画
        var plan = Plan();
        var competingHost = new Host();
        using var entered = new ManualResetEventSlim();
        //CSJ,variable=release,解放
        using var release = new ManualResetEventSlim();
        //CSJ,variable=host,ホスト
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
        //CSJ,variable=coordinator,調整役
        var coordinator = new SceneRenderCoordinator(Session);
        //CSJ,variable=renderError,描画エラー
        var renderError = new InvalidOperationException("render fault");
        //CSJ,variable=disposeError,破棄エラー
        var disposeError = new InvalidOperationException("dispose fault");
        //CSJ,variable=observeError,観測エラー
        var observeError = new InvalidOperationException("observe fault");
        //CSJ,variable=rendering,描画
        var rendering = new Host { EditResponse = (_, _) => throw renderError };
        Assert(ReferenceEquals(Expect<InvalidOperationException>(() => coordinator.Run(Plan(), rendering, Budget)), renderError),
            "The original render failure was lost.");
        Assert(rendering.DisposedCount == 1 && rendering.Events[^1] == "observe" &&
            coordinator.Run(Plan(), new Host(), Budget).Metrics?.ChangedPixels == 1,
            "Render failure skipped cleanup or prevented retry after successful disposal.");

        //CSJ,variable=disposal,破棄
        var disposal = new Host { OnDispose = () => throw disposeError };
        Assert(ReferenceEquals(Expect<InvalidOperationException>(() => coordinator.Run(Plan(), disposal, Budget)), disposeError),
            "A disposal failure was masked.");
        Assert(disposal.Opened.Count == 1 && disposal.Events[^1] == "observe", "Disposal failure was ignored.");
        //CSJ,variable=retry,再試行
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

    //CSJ,method=TestState,状態をテストする
    private static void TestState()
    {
        //CSJ,variable=coordinator,調整役
        var coordinator = new SceneRenderCoordinator(Session);
        //CSJ,variable=invalid,無効
        foreach (Guid? invalid in new Guid?[] { null, Guid.Empty })
        {
            //CSJ,variable=host,ホスト
            var host = new Host { EditObservation = observation => observation with { EditorStateVersion = invalid } };
            ExpectKind(() => coordinator.Run(Plan(), host, Budget), SceneRenderFailure.UnobservableState);
            Assert(host.Opened.Count == 0, "Opened a Scene without a valid editor-state observation.");
        }

        //CSJ,variable=version,バージョン
        var version = Guid.NewGuid();
        //CSJ,variable=changed,変更済み
        var changed = new Host();
        changed.EditObservation = observation => changed.DisposedCount > 0
            ? observation with { EditorStateVersion = version }
            : observation;
        ExpectKind(() => coordinator.Run(Plan(), changed, Budget), SceneRenderFailure.StateChanged);
        Assert(changed.Opened.Count == 1 && changed.DisposedCount == 1 && changed.Events[^1] == "observe",
            "Change in editing state entered the next leg or skipped cleanup.");

        var fault = new InvalidOperationException("baseline failed");
        //CSJ,variable=failed,失敗
        var failed = new Host { EditObservation = _ => throw fault };
        ExpectOnly(() => coordinator.Run(Plan(), failed, Budget), error => ReferenceEquals(error, fault),
            "The observer fault was masked.");
        Assert(failed.Opened.Count == 0, "An unavailable observer allowed rendering.");
    }

    private static void TestExperimentalConversion()
    {
        //CSJ,variable=plan,計画
        var plan = Plan(height: 3);
        byte[] input = [90, 80, 70, 255, 4, 5, 0, 0, 64, 128, 6, 7, 0, 0, 0, 0, 8, 9];
        byte[] expected = [90, 80, 70, 255, 0, 0, 128, 128, 0, 0, 0, 0];
        //CSJ,variable=response,応答
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

        //CSJ,variable=cancel,キャンセル
        using var cancel = new CancellationTokenSource();
        using var borrowed = new CancelOnSpanAccess(input, cancel);
        Expect<OperationCanceledException>(() => SceneCapturedFrame.CopyExperimentalPbgra32(plan, PreviewComparisonSide.A,
            response with { Pixels = borrowed.Buffer }, cancel.Token));
    }

    private static void ExpectRejection(Action operation, SceneHandoffError error, PreviewComparisonSide side)
    {
        ExpectOnly(operation,
            //CSJ,parameter=exception,例外
            exception => exception is SceneRenderException { Kind: SceneRenderFailure.HandoffRejected } failure &&
                failure.Rejection == new SceneHandoffRejection(error, side),
            $"Expected {error} on side {side}.");
    }

    private static void ExpectKind(Action operation, SceneRenderFailure kind) =>
        ExpectOnly(operation, exception => exception is SceneRenderException failure && failure.Kind == kind, $"Expected {kind}.");

    private static void ExpectOnly(Action operation, Func<Exception, bool> predicate, string message)
    {
        //CSJ,variable=exception,例外
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

    //CSJ,method=Assert,検査する
    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    //CSJ,class=ManualClock,手動時計
    private sealed class ManualClock : TimeProvider
    {
        //CSJ,field=timestamp,タイムスタンプ
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        //CSJ,method=GetTimestamp,タイムスタンプを取得する
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan amount) => timestamp += amount.Ticks;
    }

    //CSJ,class=Host,ホスト
    private sealed class Host : ISceneRenderContext
    {
        //CSJ,property=Opened,表示済み
        public List<SceneRenderRequest> Opened { get; } = [];
        public List<string> Events { get; } = [];
        public List<byte[]> Buffers { get; } = [];
        //CSJ,property=DisposedCount,破棄済みの件数
        public int DisposedCount { get; private set; }
        //CSJ,property=EditObservation,編集観測記録
        public Func<SceneRenderObservation, SceneRenderObservation>? EditObservation { get; set; }
        //CSJ,property=EditResponse,編集応答
        public Func<SceneRenderResponse, CancellationToken, SceneRenderResponse>? EditResponse { get; set; }
        public Action? OnDispose { get; init; }

        //CSJ,method=Observe,観測する
        public SceneRenderObservation Observe()
        {
            Events.Add("observe");
            //CSJ,variable=observation,観測記録
            var observation = new SceneRenderObservation(A, B, StateVersion);
            return EditObservation?.Invoke(observation) ?? observation;
        }

        //CSJ,method=Open,開く
        public ISceneRenderSource Open(SceneRenderRequest request)
        {
            Assert(Opened.Count == DisposedCount, "Two render sources were open at once.");
            Opened.Add(request);
            Events.Add("open");
            return new Source(this, request);
        }

        //CSJ,class=Source,元
        private sealed class Source(Host host, SceneRenderRequest request) : ISceneRenderSource
        {
            //CSJ,field=pixels,画素列
            private readonly byte[] pixels = [request.Snapshot == A ? (byte)20 : (byte)40, 0, 0, 255, 97, 98];
            //CSJ,field=disposed,破棄済み
            private bool disposed;

            //CSJ,method=Render,描画する
            public SceneRenderResponse Render(CancellationToken cancellationToken)
            {
                Assert(!disposed, "Rendered using a disposed source.");
                host.Events.Add("render");
                host.Buffers.Add(pixels);
                //CSJ,variable=response,応答
                var response = new SceneRenderResponse(request.OperationId, request.Snapshot, request.FrameIndex,
                    request.Width, request.Height, 6, PreviewPixelFormat.Bgra32, pixels);
                return host.EditResponse?.Invoke(response, cancellationToken) ?? response;
            }

            //CSJ,method=Dispose,破棄する
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
