# テスト・開発ツールの全型一覧

Git管理下のテストコードの全C#型を、[製品コードの全型一覧](05-product-types.md) と同じ条件で抽出した。テストクラス、プロセス・WPF・SDKの疑似実装、fixture用データ構造を含む。

**全76型、76宣言。** class 69件、record class 6件、delegate 1件。

## C#テストプロジェクト

| 型名 | 種別・可視性 | 役割 | 主な操作・データ | 宣言元 |
| --- | --- | --- | --- | --- |
| `AssetInventoryTests` | class / internal | 素材の棚卸しテスト | `RunAsync` | [AssetInventoryTests.cs L7](../../YMMProjectManager.Tests/AssetInventoryTests.cs#L7) |
| `AssetManifestTests` | class / internal | 素材目録テスト | `RunAsync`、`TestHistoryPinnedForPublicationAsync`、`AssertDirectoryPinned`、`TestRelinkRecoveryAsync`、`TestLegacyImportPublicationAsync`、`TestAbandonedRevisionStagingAsync`、`TestStalePreparedOutputRecoveryAsync` ほか | [AssetManifestTests.cs L8](../../YMMProjectManager.Tests/AssetManifestTests.cs#L8) |
| `AssetPoolTests` | class / internal | 素材プールテスト | `RunAsync`、`CreateFileAsync`、`RunChildAsync`、`PublishAsync`、`ReadAfterReleaseAsync`、`Check`、`ChildArgument` ほか | [AssetPoolTests.cs L12](../../YMMProjectManager.Tests/AssetPoolTests.cs#L12) |
| `DerivedRelinkAssociationTests` | class / internal | 派生再リンク関連付けテスト | `RunAsync`、`Check`、`FixtureAsync`、`RejectAsync` | [DerivedRelinkAssociationTests.cs L9](../../YMMProjectManager.Tests/DerivedRelinkAssociationTests.cs#L9) |
| `DetachedThumbnailTests` | class / internal | 別PFや別フレーム、取消・解放失敗時に拒否し公開しないことを検証する | `RunAsync`、`GenerateAsync`、`WithFixtureAsync`、`EnsureNoPublication`、`Ensure` | [DetachedThumbnailTests.cs L8](../../YMMProjectManager.Tests/DetachedThumbnailTests.cs#L8) |
| `DetachedThumbnailTests.Source` | class / private | PF版とフレーム応答、描画失敗・終了のタイミングを制御する合成描画源 | `version`、`length`、`CaptureAsync`、`ProjectVersion`、`Length`、`ObservedFrameOffset`、`AfterDraw` ほか | [DetachedThumbnailTests.cs L123](../../YMMProjectManager.Tests/DetachedThumbnailTests.cs#L123) |
| `ExternalRelinkRecoveryTests` | class / internal | 外部再リンク復旧テスト | `RunAsync`、`RefusedAsync`、`EnsureWriteDenied`、`Ensure` | [ExternalRelinkRecoveryTests.cs L7](../../YMMProjectManager.Tests/ExternalRelinkRecoveryTests.cs#L7) |
| `ExternalRelinkRecoveryTests.Fixture` | record class / private | テスト用データ | `Source`、`Output`、`SourceJson`、`Pool`、`Series`、`Head`、`Context` ほか | [ExternalRelinkRecoveryTests.cs L231](../../YMMProjectManager.Tests/ExternalRelinkRecoveryTests.cs#L231) |
| `GitConfirmedPullTests` | class / internal | 確認付きGitプルのテスト | `RunAsync`、`CreateAsync`、`VerifyUpdatedAsync`、`RecoveryAsync`、`RunGitAsync`、`Check`、`Incoming` ほか | [GitConfirmedPullTests.cs L7](../../YMMProjectManager.Tests/GitConfirmedPullTests.cs#L7) |
| `GitConfirmedPullTests.Fixture` | record class / private | テスト用データ | `Local`、`Bare`、`Start`、`Target`、`Repository`、`FixtureRoot` | [GitConfirmedPullTests.cs L28](../../YMMProjectManager.Tests/GitConfirmedPullTests.cs#L28) |
| `GitHttpsAcceptanceTests` | class / internal | Git HTTPS受入テスト | `RunAsync`、`WriteCertificateFixture`、`Check`、`RequireRemoteAsync`、`RequireTargetAsync`、`SnapshotAsync`、`Hash` ほか | [GitHttpsAcceptanceTests.cs L9](../../YMMProjectManager.Tests/GitHttpsAcceptanceTests.cs#L9) |
| `GitHttpsAcceptanceTests.Snapshot` | record class / private | スナップショット | `Head`、`IndexSha256`、`IndexEntries`、`ProjectSha256`、`IgnoredSha256`、`Status`、`PendingEvidence` | [GitHttpsAcceptanceTests.cs L288](../../YMMProjectManager.Tests/GitHttpsAcceptanceTests.cs#L288) |
| `GitHttpsAcceptanceTests.FixtureEnvironment` | class / private | テスト用データ環境 | `Set`、`Dispose` | [GitHttpsAcceptanceTests.cs L352](../../YMMProjectManager.Tests/GitHttpsAcceptanceTests.cs#L352) |
| `GitHttpsAcceptanceTests.HttpsServer` | class / private | HTTPSサーバー | `process`、`errors`、`mode`、`accesses`、`port`、`Endpoint`、`AccessLog` ほか | [GitHttpsAcceptanceTests.cs L378](../../YMMProjectManager.Tests/GitHttpsAcceptanceTests.cs#L378) |
| `GitHubBrowserLoginTests` | class / internal | GitHubブラウザーログインテスト | `RunAsync`、`VerifyProfileAsync`、`VerifyInstallationAclAsync`、`VerifyConnectRulesAsync`、`VerifyRelayAsync`、`VerifyExitAsync`、`VerifyApprovedRelayAsync` ほか | [GitHubBrowserLoginTests.cs L10](../../YMMProjectManager.Tests/GitHubBrowserLoginTests.cs#L10) |
| `GitNativeInterruptionTests` | class / internal | ネイティブGitの中断テスト | `RunAsync`、`VerifyRestartAsync`、`RunRestartProbeAsync`、`RestartArgument`、`FixtureDirectory`、`WorktreeName`、`UpdatesDirectory` ほか | [GitNativeInterruptionTests.cs L10](../../YMMProjectManager.Tests/GitNativeInterruptionTests.cs#L10) |
| `GitPublicationTests` | class / internal | Git確定反映テスト | `RunAsync`、`TestAsync`、`CommitFileAsync`、`CommandAsync` | [GitPublicationTests.cs L8](../../YMMProjectManager.Tests/GitPublicationTests.cs#L8) |
| `GitPullRecoveryTests` | class / internal | Gitプル復旧テスト | `RunAsync`、`CreatePullFixtureAsync`、`RunInterruptedChild`、`RunInterruptedAfterMergeAsync`、`RunChildProcessAsync`、`CreateFixture`、`VerifyFixture` ほか | [GitPullRecoveryTests.cs L9](../../YMMProjectManager.Tests/GitPullRecoveryTests.cs#L9) |
| `GitRemoteTests` | class / internal | Gitリモートテスト | `RunAsync` | [GitRemoteTests.cs L8](../../YMMProjectManager.Tests/GitRemoteTests.cs#L8) |
| `GitRepositoryTests` | class / internal | Gitリポジトリテスト | `RunAsync`、`VerifyGuardedIndexAsync`、`VerifyGitHubProfileAsync`、`VerifyGitHubAccessApprovalAsync`、`VerifyReviewedCommitAsync`、`VerifyCommitInterferenceAsync`、`VerifyInterruptedCommitAsync` ほか | [GitRepositoryTests.cs L6](../../YMMProjectManager.Tests/GitRepositoryTests.cs#L6) |
| `YMMProjectManager.Tests.LegacyGenerationExportTests` | class / internal | 旧世代PFの別名救済と改変、取消、出力競合の拒否を検証する | `RunAsync`、`CreateSnapshot`、`Check`、`RefuseAsync`、`AssertPreserved`、`Equal` | [LegacyGenerationExportTests.cs L8](../../YMMProjectManager.Tests/LegacyGenerationExportTests.cs#L8) |
| `YMMProjectManager.Tests.LegacyGenerationExportTests.Fixture` | record class / private | 合成した現在のPF、旧世代、目録、保存先のパスと証拠を保持する | `Root`、`Original`、`Snapshot`、`Metadata`、`Manifest`、`Output`、`MetadataText` | [LegacyGenerationExportTests.cs L146](../../YMMProjectManager.Tests/LegacyGenerationExportTests.cs#L146) |
| `LiveThumbnailSafetyTests` | class / internal | 編集中Sceneの所有確認、シーク・状態保全、取消と公開競合を合成検証する | `RunAsync`、`StageStrip`、`WithFixtureAsync`、`EnsurePreserved`、`Ensure` | [LiveThumbnailSafetyTests.cs L10](../../YMMProjectManager.Tests/LiveThumbnailSafetyTests.cs#L10) |
| `LiveThumbnailSafetyTests.Source` | class / private | 編集中モデルのフレーム、現在位置と素材対象を制御する合成描画源 | `Captures`、`Capture`、`Length`、`Frame`、`Position`、`TimeAtFrame`、`IsCurrent` ほか | [LiveThumbnailSafetyTests.cs L292](../../YMMProjectManager.Tests/LiveThumbnailSafetyTests.cs#L292) |
| `OfflineControlsTests` | class / internal | オフライン制御テスト | `RunAsync`、`RunGitFixtureAsync`、`TestProductPullAsync`、`TestPullPresentationAsync` | [OfflineControlsTests.cs L12](../../YMMProjectManager.Tests/OfflineControlsTests.cs#L12) |
| `PreviewComparisonTests` | class / internal | プレビュー比較テスト | `RunAsync`、`CaseAsync`、`TestProjectOffsets`、`TestTimingCopyAsync`、`TestAlignment`、`TestInvalidAlignment`、`TestMetrics` ほか | [PreviewComparisonTests.cs L5](../../YMMProjectManager.Tests/PreviewComparisonTests.cs#L5) |
| `Program` | class / internal | テスト実行プログラム | `RunNamedCaseAsync`、`RunTestsAsync`、`RunGroupAsync`、`CreateRoot`、`ReportConditionalSkip`、`Main`、`GetTestGroups` ほか | [Program.cs L22](../../YMMProjectManager.Tests/Program.cs#L22) |
| `Program.TestGroup` | record class / private | テストランナーのグループ名、実行処理、既定と別名の登録 | `Name`、`Run`、`Default`、`Alias` | [Program.cs L149](../../YMMProjectManager.Tests/Program.cs#L149) |
| `Program.TimelineContainer` | class / private | タイムラインの格納先 | `Timeline` | [Program.cs L1257](../../YMMProjectManager.Tests/Program.cs#L1257) |
| `Program.FakeTimeline` | class / private | テスト用タイムライン | `Length`、`CurrentFrame` | [Program.cs L1264](../../YMMProjectManager.Tests/Program.cs#L1264) |
| `Program.ThrowingTimeline` | class / private | 例外を送出するテスト用タイムライン | `CurrentFrame` | [Program.cs L1279](../../YMMProjectManager.Tests/Program.cs#L1279) |
| `Program.NonMovingTimeline` | class / private | 移動しないテスト用タイムライン | `CurrentFrame` | [Program.cs L1290](../../YMMProjectManager.Tests/Program.cs#L1290) |
| `Program.CommandOnlyTimeline` | class / private | コマンドのみのテスト用タイムライン | `CurrentFrame`、`MoveBy` | [Program.cs L1311](../../YMMProjectManager.Tests/Program.cs#L1311) |
| `Program.FakeTimelineSeekAdapter` | class / private | テスト用フレーム移動アダプター | `SeekAsync` | [Program.cs L1331](../../YMMProjectManager.Tests/Program.cs#L1331) |
| `Program.FakePreviewBitmapCaptureAdapter` | class / private | テスト用プレビュー画像取得アダプター | `TryCaptureAsync` | [Program.cs L1346](../../YMMProjectManager.Tests/Program.cs#L1346) |
| `Program.FakeFailingPreviewBitmapCaptureAdapter` | class / private | 取得に失敗するテスト用プレビュー画像アダプター | `TryCaptureAsync` | [Program.cs L1391](../../YMMProjectManager.Tests/Program.cs#L1391) |
| `Program.WpfTestHost` | class / internal | WPFテストホスト | `RunAsync`、`Shutdown`、`EnsureStarted` | [Program.cs L1407](../../YMMProjectManager.Tests/Program.cs#L1407) |
| `Program.AssertEx` | class / private | テスト検証ヘルパー | `True`、`Equal` | [Program.cs L1514](../../YMMProjectManager.Tests/Program.cs#L1514) |
| `ProjectComparisonWindowTests` | class / internal | 比較時刻、設定の再読込み、画像取得と取消を合成WPFで検証する | `RunAsync`、`RenderPairAsync`、`EditTimingAsync`、`PersistTimingAsync`、`CancelCaptureAsync`、`WithWindowAsync`、`AddSection` ほか | [ProjectComparisonWindowTests.cs L10](../../YMMProjectManager.Tests/ProjectComparisonWindowTests.cs#L10) |
| `ProjectComparisonWindowTests.SyntheticPreviewProvider` | class / private | 比較画面へ合成画像、待機、失敗を与え、要求と取消を観測する | `RenderCount`、`ResolveAsync`、`RenderAsync`、`LastAlignment`、`RequestToken`、`Pending`、`Failure` ほか | [ProjectComparisonWindowTests.cs L214](../../YMMProjectManager.Tests/ProjectComparisonWindowTests.cs#L214) |
| `ProjectListConcurrencyTests` | class / internal | プロジェクト一覧並行処理テスト | `RunAsync`、`RunWorkerAsync` | [ProjectListConcurrencyTests.cs L9](../../YMMProjectManager.Tests/ProjectListConcurrencyTests.cs#L9) |
| `ProjectListDraftTests` | class / internal | プロジェクト一覧下書きテスト | `RunAsync` | [ProjectListDraftTests.cs L8](../../YMMProjectManager.Tests/ProjectListDraftTests.cs#L8) |
| `ProjectListIdentityRecoveryTests` | class / internal | プロジェクト一覧識別情報復旧テスト | `RunAsync`、`RunPhysicalAsync`、`CreateHardLink`、`RunLegacyAsync`、`RunApplyAsync`、`ExpectInvalid` | [ProjectListIdentityRecoveryTests.cs L11](../../YMMProjectManager.Tests/ProjectListIdentityRecoveryTests.cs#L11) |
| `ProjectListPublicationTests` | class / internal | プロジェクト一覧確定反映テスト | `RunAsync`、`ReplacementFixtureAsync`、`RunReplacementChildAsync`、`RunGenerationPointAsync`、`RunPointAsync`、`Open`、`TestInterruptedReplacementAsync` ほか | [ProjectListPublicationTests.cs L11](../../YMMProjectManager.Tests/ProjectListPublicationTests.cs#L11) |
| `ProjectListRecoveryFilesTests` | class / internal | プロジェクト一覧復旧ファイル一覧テスト | `RunAsync` | [ProjectListRecoveryFilesTests.cs L7](../../YMMProjectManager.Tests/ProjectListRecoveryFilesTests.cs#L7) |
| `ProjectListRecoveryTests` | class / internal | プロジェクト一覧復旧テスト | `RunAsync`、`GetCurrentThreadId`、`EnumThreadWindows`、`GetClassName`、`PostMessage` | [ProjectListRecoveryTests.cs L9](../../YMMProjectManager.Tests/ProjectListRecoveryTests.cs#L9) |
| `ProjectListRecoveryTests.EnumWindowsProc` | delegate / private | WPF回復試験でウィンドウを列挙するWin32コールバック | `hwnd`、`param` | [ProjectListRecoveryTests.cs L16](../../YMMProjectManager.Tests/ProjectListRecoveryTests.cs#L16) |
| `ProjectListSaveTests` | class / internal | プロジェクト一覧保存テスト | `RunAsync` | [ProjectListSaveTests.cs L9](../../YMMProjectManager.Tests/ProjectListSaveTests.cs#L9) |
| `ProjectListWpfFixtureProcess` | class / internal | プロジェクト一覧WPFテスト用データプロセス | `RunAsync`、`VerifyTimeoutAsync`、`RunOwnedAsync`、`RunChildAsync`、`ChildArgument`、`StalledChildArgument` | [ProjectListWpfFixtureProcess.cs L6](../../YMMProjectManager.Tests/ProjectListWpfFixtureProcess.cs#L6) |
| `R30ReflectionTests` | class / internal | YmmpxLib反射API、実ライブラリ、導入パッケージ、不適切なDLLの扱いを検証する | `RunAsync`、`RunRealAsync`、`RunInstalledAsync`、`CreateMetadataFixture`、`WithoutLibraryOverride`、`Require` | [R30ReflectionTests.cs L12](../../YMMProjectManager.Tests/R30ReflectionTests.cs#L12) |
| `RealYmmpxR5Tests` | class / internal | 実際のYMMPXを使うR5テスト | `RunAsync` | [RealYmmpxR5Tests.cs L9](../../YMMProjectManager.Tests/RealYmmpxR5Tests.cs#L9) |
| `RealYmmpxR5Tests.InlineProgress` | class / private | インライン進捗 | `firstWrite`、`Report` | [RealYmmpxR5Tests.cs L186](../../YMMProjectManager.Tests/RealYmmpxR5Tests.cs#L186) |
| `RelinkAssociationTests` | class / internal | 再リンク関連付けテスト | `RunAsync` | [RelinkAssociationTests.cs L9](../../YMMProjectManager.Tests/RelinkAssociationTests.cs#L9) |
| `RelinkIntegrityTests` | class / internal | 再リンク整合性テスト | `RunAsync` | [RelinkIntegrityTests.cs L9](../../YMMProjectManager.Tests/RelinkIntegrityTests.cs#L9) |
| `SceneComparisonHandoffTests` | class / internal | シーン比較引き継ぎテスト | `RunAsync`、`CaseAsync`、`Snapshot`、`Plan`、`TestPreparation`、`ExpectPlanError`、`TestReceipt` ほか | [SceneComparisonHandoffTests.cs L5](../../YMMProjectManager.Tests/SceneComparisonHandoffTests.cs#L5) |
| `CancelOnSpanAccess` | class / internal | スパン参照時のキャンセル | `pixels`、`cancellation`、`AccessCount`、`Buffer`、`GetSpan`、`Pin`、`Unpin` ほか | [SceneComparisonHandoffTests.cs L264](../../YMMProjectManager.Tests/SceneComparisonHandoffTests.cs#L264) |
| `SceneRenderCoordinatorTests` | class / internal | シーン描画調整役テスト | `RunAsync`、`CaseAsync`、`Snapshot`、`Plan`、`TestSequence`、`TestStaleObservations`、`TestInvalidationWithinCapture` ほか | [SceneRenderCoordinatorTests.cs L4](../../YMMProjectManager.Tests/SceneRenderCoordinatorTests.cs#L4) |
| `SceneRenderCoordinatorTests.ManualClock` | class / private | 手動時計 | `TimestampFrequency`、`GetTimestamp`、`Advance` | [SceneRenderCoordinatorTests.cs L388](../../YMMProjectManager.Tests/SceneRenderCoordinatorTests.cs#L388) |
| `SceneRenderCoordinatorTests.Host` | class / private | ホスト | `Opened`、`Open`、`Events`、`Buffers`、`DisposedCount`、`EditObservation`、`EditResponse` ほか | [SceneRenderCoordinatorTests.cs L400](../../YMMProjectManager.Tests/SceneRenderCoordinatorTests.cs#L400) |
| `SceneRenderCoordinatorTests.Host.Source` | class / private | 元 | `host`、`request`、`Render`、`Dispose` | [SceneRenderCoordinatorTests.cs L433](../../YMMProjectManager.Tests/SceneRenderCoordinatorTests.cs#L433) |
| `SettingsAndDiagnosticsTests` | class / internal | 設定の安全な公開、キャッシュ容量・所有、ログの秘匿と回転を検証する | `RunAsync`、`ReadyEventName`、`CreateCapture`、`RunInterruptedSaveChild`、`InterruptedSaveArgument`、`FixtureFileName`、`InterruptSaveAsync` ほか | [SettingsAndDiagnosticsTests.cs L9](../../YMMProjectManager.Tests/SettingsAndDiagnosticsTests.cs#L9) |
| `SettingsAndDiagnosticsTests.DiagnosticTime` | class / private | ログの時刻とアーカイブの期限を制御する試験用時計 | `now`、`Now`、`GetUtcNow` | [SettingsAndDiagnosticsTests.cs L294](../../YMMProjectManager.Tests/SettingsAndDiagnosticsTests.cs#L294) |
| `StrictRelinkWorkflowTests` | class / internal | 厳格再リンク作業手順テスト | `RunAsync`、`Check`、`ExpectRefusedAsync` | [StrictRelinkWorkflowTests.cs L9](../../YMMProjectManager.Tests/StrictRelinkWorkflowTests.cs#L9) |
| `ThumbnailGalleryTests` | class / internal | 既存キャッシュ、64点表示、取得進捗、取消と旧結果の破棄を検証する | `RunAsync`、`Displayed`、`WithFixtureAsync`、`Ensure` | [ThumbnailGalleryTests.cs L10](../../YMMProjectManager.Tests/ThumbnailGalleryTests.cs#L10) |
| `ThumbnailGalleryTests.Source` | class / private | 画像取得と解放回数を観測する合成サムネイル描画源 | `version`、`length`、`CaptureAsync`、`ProjectVersion`、`Length`、`BeforeCapture`、`Frames` ほか | [ThumbnailGalleryTests.cs L228](../../YMMProjectManager.Tests/ThumbnailGalleryTests.cs#L228) |
| `YmmpxArchiveValidationTests` | class / internal | YMMPXアーカイブ妥当性検証テスト | `RunAsync`、`VerifyNestedCleanupAsync`、`VerifyReparseParentRejectedAsync`、`ReadHashAsync`、`WriteCurrentPackage` | [YmmpxArchiveValidationTests.cs L6](../../YMMProjectManager.Tests/YmmpxArchiveValidationTests.cs#L6) |
| `YmmpxArchiveValidationTests.InlineProgress` | class / private | インライン進捗 | `action`、`Report` | [YmmpxArchiveValidationTests.cs L227](../../YMMProjectManager.Tests/YmmpxArchiveValidationTests.cs#L227) |
| `YmmpxBundleTests` | class / internal | YMMPX一括パッケージテスト | `RunAsync`、`CreateInstallerGuide`、`CreateRoot`、`CollectFilePaths`、`TestInstallGuideMessageAsync`、`TestLegacyFolderDetectionAsync`、`TestVerifiedInstallerAsync` ほか | [YmmpxBundleTests.cs L10](../../YMMProjectManager.Tests/YmmpxBundleTests.cs#L10) |
| `YmmpxFailureTests` | class / internal | YMMPX失敗テスト | `RunAsync`、`IsPackage` | [YmmpxFailureTests.cs L7](../../YMMProjectManager.Tests/YmmpxFailureTests.cs#L7) |
| `YmmpxLib.YmmpxPackagingOptions` | class / public | YMMPXパッケージ化オプション | `IncludeProjectUiSettings` | [YmmpxFailureTests.cs L255](../../YMMProjectManager.Tests/YmmpxFailureTests.cs#L255) |
| `YmmpxLib.YmmpxPackagingProgress` | class / public | YMMPXパッケージ化進捗 | `Percentage` | [YmmpxFailureTests.cs L257](../../YMMProjectManager.Tests/YmmpxFailureTests.cs#L257) |
| `YmmpxLib.YmmpxPackagingResult` | record class / public | YmmpxLib互換試験の合成APIが返す出力パス、素材件数と格納マップ | `OutputPath`、`ResourceCount`、`FileMap` | [YmmpxFailureTests.cs L258](../../YMMProjectManager.Tests/YmmpxFailureTests.cs#L258) |
| `YmmpxLib.YmmpxPackageService` | class / public | YMMPXパッケージサービス | `CreatePackageAsync`、`Mode`、`ExternalPath`、`DuringPackaging`、`AfterPartialWrite`、`ExtractionCalls`、`GetAvailableDirectoryPath` ほか | [YmmpxFailureTests.cs L260](../../YMMProjectManager.Tests/YmmpxFailureTests.cs#L260) |
| `YmmpxReleasedFormatTests` | class / internal | 公開済みYMMPX形式のテスト | `RunAsync` | [YmmpxReleasedFormatTests.cs L7](../../YMMProjectManager.Tests/YmmpxReleasedFormatTests.cs#L7) |
| `YmmpxTransferTests` | class / internal | YMMPX受け渡しテスト | `RunAsync`、`WriteLegacyV2Async`、`CreateMaterial`、`WriteEntry`、`Check`、`RuleReservationChildArgument`、`ReservationFixture` ほか | [YmmpxTransferTests.cs L12](../../YMMProjectManager.Tests/YmmpxTransferTests.cs#L12) |
| `YmmpxTransferTests.InlineProgress` | class / private | インライン進捗 | `action`、`Report` | [YmmpxTransferTests.cs L828](../../YMMProjectManager.Tests/YmmpxTransferTests.cs#L828) |

## 開発・検証スクリプトの型

| 型 | 役割 | 宣言元 |
| --- | --- | --- |
| `Handler` | 隔離HTTPS試験で合成Gitリクエストを扱うHTTPハンドラ | [r20-https-git-fixture.py L17](../../scripts/r20-https-git-fixture.py#L17) |
| `FixtureServer` | 試験用の並行HTTPサーバーと隔離fixtureの状態を保持する | [r20-https-git-fixture.py L113](../../scripts/r20-https-git-fixture.py#L113) |
| `FixtureTests` | 受入fixtureの生成、同一性、名前、改変、非上書き、隔離のPython試験 | [test_r34_fixtures.py L11](../../scripts/test_r34_fixtures.py#L11) |

[YMMPX fixture生成のC#入口](../../scripts/r34_pack_fixture.cs) はトップレベルステートメントを使う。[fixture CLI](../../scripts/r34_fixtures.py) とPowerShell補助スクリプトは関数を使用する。

実行方法、テストの集計単位、合成試験と実受入は [ホスト・運用仕様](04-host-operations.md) を参照。
