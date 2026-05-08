namespace YMMProjectManager.Presentation.Timeline;

public sealed class YmmTimelineViewGenerationAttemptResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public bool HostCreated { get; set; }
    public bool HostShownOrInitialized { get; set; }
    public bool ViewAttachedToHost { get; set; }
    public string TargetTypeName { get; set; } = string.Empty;
    public string ConstructorSignature { get; set; } = string.Empty;
    public string? FailureReason { get; set; }
    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }
    public bool VisualAttachAttempted { get; set; }
    public bool VisualAttachForbidden { get; set; }
    public bool VisualAttachSucceeded { get; set; }
    public bool DetachSucceeded { get; set; }
    public long AttachDurationMs { get; set; }
    public bool DataContextAssigned { get; set; }
    public bool LoadedEventObserved { get; set; }
    public bool InitializedEventObserved { get; set; }
    public bool DataContextChangedObserved { get; set; }
    public bool TemplateAppliedObserved { get; set; }
    public bool LayoutUpdatedObserved { get; set; }
    public bool RenderingObserved { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public string DesiredSize { get; set; } = string.Empty;
    public string RenderSize { get; set; } = string.Empty;
    public bool IsVisible { get; set; }
    public bool IsLoaded { get; set; }
    public bool PresentationSourceAvailable { get; set; }
    public bool DispatcherRenderPriorityReached { get; set; }
    public bool MinimalRenderObserved { get; set; }
    public bool DisposeAttempted { get; set; }
    public bool DisposeSucceeded { get; set; }
    public string? DisposeFailureReason { get; set; }
    public long GenerationAttemptMs { get; set; }
    public long DisposeMs { get; set; }
    public bool ExecutedOnStaThread { get; set; }
    public bool? WeakReferenceAliveAfterGc { get; set; }
    public bool BindingErrorObservationUnavailable { get; set; } = true;
    public IReadOnlyList<YmmTimelineDataContextBoundaryPatternResult> DataContextBoundaryPatterns { get; set; } = [];
    public YmmTimelinePassiveEventBoundaryResult? PassiveEventBoundary { get; set; }
    public YmmTimelineCommandRouteBoundaryResult? CommandRouteBoundary { get; set; }
    public YmmTimelineVisualTreeInventoryResult? VisualTreeInventory { get; set; }
    public YmmTimelineBindingSurfaceInventoryResult? BindingSurfaceInventory { get; set; }
    public YmmTimelineResourceInventoryResult? ResourceInventory { get; set; }
    public YmmTimelineLifecycleRepeatabilityResult? LifecycleRepeatability { get; set; }
    public YmmTimelineExpandedVisualTreeInventoryResult? ExpandedVisualTreeInventory { get; set; }
    public YmmTimelineLayoutSizeSweepResult? LayoutSizeSweep { get; set; }
    public YmmTimelineDispatcherPriorityBoundaryResult? DispatcherPriorityBoundary { get; set; }
    public YmmTimelineScrollContentInventoryResult? ScrollContentInventory { get; set; }
    public YmmTimelineViewModelSurfaceInventoryResult? ViewModelSurfaceInventory { get; set; }
    public YmmTimelineThemeResourceSmokeResult? ThemeResourceSmoke { get; set; }
    public YmmTimelineSizePropagationResult? SizePropagation { get; set; }
    public YmmTimelineMeasureArrangeBoundaryResult? MeasureArrangeBoundary { get; set; }
    public YmmTimelineParentContainerVariationResult? ParentContainerVariation { get; set; }
    public YmmTimelineLayoutConstraintDiagnosticsResult? LayoutConstraintDiagnostics { get; set; }
    public YmmTimelineSizePropagationSummaryResult? SizePropagationSummary { get; set; }
    public YmmTimelineVisualStateInventoryResult? VisualStateInventory { get; set; }
    public YmmTimelineAutomationInventoryResult? AutomationInventory { get; set; }
    public YmmTimelineRiskClassificationResult? RiskClassification { get; set; }
    public YmmTimelineViewModelSemanticMemberClassificationResult? ViewModelSemanticMemberClassification { get; set; }
    public YmmTimelineViewModelSafeGetterSnapshotResult? ViewModelSafeGetterSnapshot { get; set; }
    public YmmTimelineViewModelCollectionSurfaceInventoryResult? ViewModelCollectionSurfaceInventory { get; set; }
    public YmmTimelineViewModelCommandSurfaceInventoryResult? ViewModelCommandSurfaceInventory { get; set; }
    public YmmTimelineBindingToViewModelMapResult? BindingToViewModelMap { get; set; }
    public YmmTimelineVisualSemanticClassificationResult? VisualSemanticClassification { get; set; }
    public YmmTimelineViewModelEventSurfaceInventoryResult? ViewModelEventSurfaceInventory { get; set; }
    public YmmTimelineDataCandidateDiscoveryResult? DataCandidateDiscovery { get; set; }
    public YmmTimelineSelectedStateSnapshotResult? SelectedStateSnapshot { get; set; }
    public YmmTimelinePositionScaleSnapshotResult? PositionScaleSnapshot { get; set; }
    public YmmTimelineCandidateCollectionCountSmokeResult? CandidateCollectionCountSmoke { get; set; }
    public YmmTimelineCandidateCollectionSampleMetadataResult? CandidateCollectionSampleMetadata { get; set; }
    public YmmTimelineSemanticSurfaceSummaryResult? SemanticSurfaceSummary { get; set; }
    public YmmTimelineProjectDataMappingFeasibilityResult? ProjectDataMappingFeasibility { get; set; }
    public YmmTimelineInvestigationPhaseGateResult? InvestigationPhaseGate { get; set; }
    public YmmTimelineProjectRootDiscoveryResult? ProjectRootDiscovery { get; set; }
    public YmmTimelineLayerSurfaceInventoryResult? LayerSurfaceInventory { get; set; }
    public YmmTimelineItemSurfaceInventoryResult? ItemSurfaceInventory { get; set; }
    public YmmTimelineMediaPathCandidateInventoryResult? MediaPathCandidateInventory { get; set; }
    public YmmTimelineTemporalSurfaceInventoryResult? TemporalSurfaceInventory { get; set; }
    public YmmTimelineSelectionNavigationSurfaceResult? SelectionNavigationSurface { get; set; }
    public YmmTimelineHierarchyMappingResult? HierarchyMapping { get; set; }
    public YmmTimelineSafePrimitiveSnapshotResult? SafePrimitiveSnapshot { get; set; }
    public YmmTimelineObservableFlowInventoryResult? ObservableFlowInventory { get; set; }
    public YmmTimelineSemanticDiffCandidatesResult? SemanticDiffCandidates { get; set; }
    public YmmTimelineSerializationSurfaceInventoryResult? SerializationSurfaceInventory { get; set; }
    public YmmTimelineInternalTypeDependencyMapResult? InternalTypeDependencyMap { get; set; }
    public YmmTimelineReadOnlyRiskHotspotsResult? ReadOnlyRiskHotspots { get; set; }
    public YmmTimelineSemanticBridgeFeasibilityResult? SemanticBridgeFeasibility { get; set; }
    public YmmTimelineNextPhaseGateResult? NextPhaseGate { get; set; }
    public YmmTimelineInvestigationPolicyResolverResult? InvestigationPolicyResolver { get; set; }
    public YmmTimelineReadOnlyBridgeAllowedScopeResult? ReadOnlyBridgeAllowedScope { get; set; }
    public YmmTimelineControlledProjectSnapshotFeasibilityResult? ControlledProjectSnapshotFeasibility { get; set; }
    public YmmTimelineReadModelPrototypeResult? ReadModelPrototype { get; set; }
    public YmmTimelineReadModelValidationResult? ReadModelValidation { get; set; }
    public YmmTimelinePassiveSnapshotRepeatabilityResult? PassiveSnapshotRepeatability { get; set; }
    public YmmTimelineSnapshotPerformanceSmokeResult? SnapshotPerformanceSmoke { get; set; }
    public YmmTimelineBridgeFailureModeCatalogResult? BridgeFailureModeCatalog { get; set; }
    public YmmTimelineControlledSnapshotRiskReportResult? ControlledSnapshotRiskReport { get; set; }
    public YmmTimelinePassiveProjectBindingReadinessResult? PassiveProjectBindingReadiness { get; set; }
    public YmmTimelineSemanticDiffBridgePrototypeResult? SemanticDiffBridgePrototype { get; set; }
    public YmmTimelineTimelineDiffBridgePrototypeResult? TimelineDiffBridgePrototype { get; set; }
    public YmmTimelinePlaceholderReadModelAdapterFeasibilityResult? PlaceholderReadModelAdapterFeasibility { get; set; }
    public YmmTimelineInvestigationMilestoneSummaryResult? InvestigationMilestoneSummary { get; set; }
    public YmmTimelineSmallVisibleHostPreflightResult? SmallVisibleHostPreflight { get; set; }
    public YmmTimelineIntegrationBlocklistResult? IntegrationBlocklist { get; set; }
    public YmmTimelineNextSafePreviewPlannerResult? NextSafePreviewPlanner { get; set; }
    public YmmTimelineDiagnosticsIndexResult? DiagnosticsIndex { get; set; }
    public YmmTimelineCurrentBatchFinalGateResult? CurrentBatchFinalGate { get; set; }
    public YmmTimelineEmptySnapshotRootCauseClassifierResult? EmptySnapshotRootCauseClassifier { get; set; }
    public YmmTimelineRuntimeOwnerChainDiscoveryResult? RuntimeOwnerChainDiscovery { get; set; }
    public YmmTimelineActiveProjectContextCandidatesResult? ActiveProjectContextCandidates { get; set; }
    public YmmTimelineRuntimeInstanceDiscoveryResult? RuntimeInstanceDiscovery { get; set; }
    public YmmTimelineRuntimeVsGeneratedViewModelComparisonResult? RuntimeVsGeneratedViewModelComparison { get; set; }
    public YmmTimelineRuntimeSafeGetterSnapshotResult? RuntimeSafeGetterSnapshot { get; set; }
    public YmmTimelineRuntimeCollectionCountSmokeResult? RuntimeCollectionCountSmoke { get; set; }
    public YmmTimelineRuntimeProjectLayerItemMappingResult? RuntimeProjectLayerItemMapping { get; set; }
    public YmmTimelineRuntimeDataBridgeFeasibilityResult? RuntimeDataBridgeFeasibility { get; set; }
    public YmmTimelineOwnerChainPathsResult? OwnerChainPaths { get; set; }
    public YmmTimelineRuntimeSnapshotDryRunPolicyResult? RuntimeSnapshotDryRunPolicy { get; set; }
    public YmmTimelineRuntimeReadOnlySnapshotDryRunResult? RuntimeReadOnlySnapshotDryRun { get; set; }
    public YmmTimelineRuntimeSnapshotRepeatabilityResult? RuntimeSnapshotRepeatability { get; set; }
    public YmmTimelineRuntimeSnapshotPerformanceSmokeResult? RuntimeSnapshotPerformanceSmoke { get; set; }
    public YmmTimelineRuntimeSemanticDiffInputDryRunResult? RuntimeSemanticDiffInputDryRun { get; set; }
    public YmmTimelineRuntimeTimelineDiffInputDryRunResult? RuntimeTimelineDiffInputDryRun { get; set; }
    public YmmTimelineRuntimeBridgeRiskReportResult? RuntimeBridgeRiskReport { get; set; }
    public YmmTimelineRuntimeBridgeMilestoneSummaryResult? RuntimeBridgeMilestoneSummary { get; set; }
    public YmmTimelineRuntimeNextSafeStepPlannerResult? RuntimeNextSafeStepPlanner { get; set; }
    public YmmTimelineRuntimeBridgeFinalGateResult? RuntimeBridgeFinalGate { get; set; }
}

public sealed class YmmTimelineInvestigationPolicyResolverResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public string LatestGateFile { get; set; } = string.Empty; public bool ContinueObservation { get; set; } public string SafeToTryReadOnlyBridge { get; set; } = "unknown"; public IReadOnlyList<string> AllowedScopes { get; set; } = []; public IReadOnlyList<string> BlockedScopes { get; set; } = []; public string Reasoning { get; set; } = string.Empty; }
public sealed class YmmTimelineReadOnlyBridgeAllowedScopeResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public bool AllowedByPolicy { get; set; } public int ReadOperationCount { get; set; } public int SucceededReadCount { get; set; } public int FailedReadCount { get; set; } public int SkippedReadCount { get; set; } public int ExceptionCount { get; set; } public int RiskRejectedCount { get; set; } public bool FallbackPreserved { get; set; } = true; }
public sealed class YmmTimelineControlledProjectSnapshotFeasibilityResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public bool ProjectRootAvailable { get; set; } public bool TimelineRootAvailable { get; set; } public bool LayerCandidatesAvailable { get; set; } public bool ItemCandidatesAvailable { get; set; } public bool MediaPathCandidatesAvailable { get; set; } public bool TemporalCandidatesAvailable { get; set; } public bool SelectionCandidatesAvailable { get; set; } public int SnapshotCompletenessScore { get; set; } }
public sealed class YmmTimelineReadModelPrototypeResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public int LayersCount { get; set; } public int ItemsCount { get; set; } public bool HasViewport { get; set; } public bool HasSelection { get; set; } public int ReadModelCompletenessScore { get; set; } public int UnmappedCandidateCount { get; set; } public int ExceptionCount { get; set; } public bool FallbackPreserved { get; set; } = true; }
public sealed class YmmTimelineReadModelValidationResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public bool HasStableTypeNames { get; set; } public bool HasLayerLikeData { get; set; } public bool HasItemLikeData { get; set; } public bool HasTemporalData { get; set; } public bool HasMediaPathData { get; set; } public bool HasSelectionData { get; set; } public bool HasEnoughForSemanticDiff { get; set; } public bool HasEnoughForTimelineDiff { get; set; } }
public sealed class YmmTimelinePassiveSnapshotRepeatabilityResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public int IterationCount { get; set; } public int SucceededCount { get; set; } public int FailedCount { get; set; } public bool SnapshotHashStable { get; set; } public bool StructuralCountsStable { get; set; } public int ExceptionCount { get; set; } }
public sealed class YmmTimelineSnapshotPerformanceSmokeResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public long ElapsedMilliseconds { get; set; } public int ReadOperationCount { get; set; } public int CollectionCountReads { get; set; } public int PrimitiveReads { get; set; } public int MetadataReads { get; set; } public int SlowOperationCount { get; set; } public int ExceptionCount { get; set; } }
public sealed class YmmTimelineBridgeFailureModeCatalogResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyDictionary<string, int> Modes { get; set; } = new Dictionary<string, int>(); }
public sealed class YmmTimelineControlledSnapshotRiskReportResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public string Classification { get; set; } = "needsMoreObservation"; public IReadOnlyList<string> Reasons { get; set; } = []; }
public sealed class YmmTimelinePassiveProjectBindingReadinessResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public bool CanBindToInternalReadModel { get; set; } public bool CanBindToDiffTimelineStandalone { get; set; } public bool CanBindToSemanticDiffEngine { get; set; } public bool CanBindToProjectDiffWindow { get; set; } public bool UserFacingBindingAllowed { get; set; } }
public sealed class YmmTimelineSemanticDiffBridgePrototypeResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public int CandidateCount { get; set; } public int MappedFieldCount { get; set; } public int UnmappedFieldCount { get; set; } public bool HasLayerInfo { get; set; } public bool HasItemInfo { get; set; } public bool HasTemporalInfo { get; set; } public bool HasMediaInfo { get; set; } public bool DiffExecutionSkipped { get; set; } = true; }
public sealed class YmmTimelineTimelineDiffBridgePrototypeResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public int TimelineNodeCount { get; set; } public int LayerNodeCount { get; set; } public int ItemNodeCount { get; set; } public int TemporalNodeCount { get; set; } public int LayoutNodeCount { get; set; } public bool DiffExecutionSkipped { get; set; } = true; }
public sealed class YmmTimelinePlaceholderReadModelAdapterFeasibilityResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public bool PlaceholderPathPreserved { get; set; } public bool ReadModelCanExistAlongsidePlaceholder { get; set; } public bool FallbackAdapterStillUsable { get; set; } public bool AdapterConflictDetected { get; set; } }
public sealed class YmmTimelineInvestigationMilestoneSummaryResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public string InfrastructureFeasibilityStatus { get; set; } = string.Empty; public string SemanticSurfaceMappingStatus { get; set; } = string.Empty; public string ReadOnlyBridgeStatus { get; set; } = string.Empty; public string ControlledSnapshotStatus { get; set; } = string.Empty; public string DiffBridgePrototypeStatus { get; set; } = string.Empty; public bool IntegrationReadiness { get; set; } }
public sealed class YmmTimelineSmallVisibleHostPreflightResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public bool OffscreenStable { get; set; } public bool SizePropagationKnownIssue { get; set; } public bool ReadOnlyBridgeAvailable { get; set; } public bool SnapshotRepeatabilityKnown { get; set; } public bool FallbackPreserved { get; set; } public string SafeToAttemptSmallVisibleHost { get; set; } = "unknown"; public bool AttemptedVisibleHost { get; set; } }
public sealed class YmmTimelineIntegrationBlocklistResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public bool ProjectDiffWindowEmbeddingBlocked { get; set; } = true; public bool UserFacingIntegrationBlocked { get; set; } = true; public bool CommandExecutionBlocked { get; set; } = true; public bool InputInjectionBlocked { get; set; } = true; public bool TimelineReplacementBlocked { get; set; } = true; public IReadOnlyList<string> Reasons { get; set; } = []; }
public sealed class YmmTimelineNextSafePreviewPlannerResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<string> Candidates { get; set; } = []; }
public sealed class YmmTimelineDiagnosticsIndexResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public int FileCount { get; set; } public IReadOnlyList<string> FileNames { get; set; } = []; }
public sealed class YmmTimelineCurrentBatchFinalGateResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public bool ContinueObservation { get; set; } public string ReadOnlyBridgeFeasible { get; set; } = "unknown"; public string ControlledSnapshotFeasible { get; set; } = "unknown"; public string DiffBridgePrototypeFeasible { get; set; } = "unknown"; public bool SmallVisibleHostRequiresManualApproval { get; set; } = true; public bool ProjectDiffWindowEmbeddingAllowed { get; set; } public bool UserFacingIntegrationAllowed { get; set; } public bool CommandExecutionAllowed { get; set; } public bool InputInjectionAllowed { get; set; } }
public sealed class YmmTimelineEmptySnapshotRootCauseClassifierResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public bool StandaloneViewModelLikely { get; set; } public bool RuntimeOwnerMissingLikely { get; set; } public bool ActiveProjectContextMissingLikely { get; set; } public bool TimelineServiceMissingLikely { get; set; } public bool LazyLoadOrTimingLikely { get; set; } public bool UnsupportedReadSurfaceLikely { get; set; } public bool PolicyBlockedLikely { get; set; } public bool Unknown { get; set; } public string Confidence { get; set; } = "medium"; public IReadOnlyList<string> Evidence { get; set; } = []; public string RecommendedNextProbe { get; set; } = string.Empty; public bool IntegrationReadiness { get; set; } }
public sealed class YmmTimelineRuntimeOwnerChainDiscoveryResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public bool ApplicationExists { get; set; } public int WindowCount { get; set; } public IReadOnlyList<string> WindowTypes { get; set; } = []; public IReadOnlyList<string> WindowTitles { get; set; } = []; public string ActiveWindowCandidate { get; set; } = string.Empty; public string MainWindowType { get; set; } = string.Empty; public string MainWindowDataContextType { get; set; } = string.Empty; public IReadOnlyList<string> TimelineRelatedWindowCandidates { get; set; } = []; public IReadOnlyList<string> ProjectRelatedDataContextCandidates { get; set; } = []; public IReadOnlyList<string> EditorRelatedDataContextCandidates { get; set; } = []; }
public sealed class YmmTimelineActiveProjectContextCandidatesResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<YmmTimelineRuntimeCandidateEntry> Candidates { get; set; } = []; }
public sealed class YmmTimelineRuntimeInstanceDiscoveryResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public bool RuntimeTimelineViewFound { get; set; } public bool RuntimeTimelineViewModelFound { get; set; } public int CandidateCount { get; set; } public IReadOnlyList<YmmTimelineRuntimeCandidateEntry> Candidates { get; set; } = []; }
public sealed class YmmTimelineRuntimeVsGeneratedViewModelComparisonResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public string GeneratedTypeName { get; set; } = string.Empty; public string RuntimeTypeName { get; set; } = string.Empty; public int GeneratedPropertyCount { get; set; } public int RuntimePropertyCount { get; set; } public int GeneratedNonNullSafePrimitiveCount { get; set; } public int RuntimeNonNullSafePrimitiveCount { get; set; } public int GeneratedCollectionCandidateCount { get; set; } public int RuntimeCollectionCandidateCount { get; set; } public int GeneratedNonEmptyCollectionCount { get; set; } public int RuntimeNonEmptyCollectionCount { get; set; } public int GeneratedCommandLikeCount { get; set; } public int RuntimeCommandLikeCount { get; set; } public string DataContextOwnerPath { get; set; } = string.Empty; }
public sealed class YmmTimelineRuntimeSafeGetterSnapshotResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public string SkippedReason { get; set; } = string.Empty; public IReadOnlyList<YmmTimelineSafeGetterEntry> Entries { get; set; } = []; }
public sealed class YmmTimelineRuntimeCollectionCountSmokeResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public string SkippedReason { get; set; } = string.Empty; public IReadOnlyList<YmmTimelineCollectionCountEntry> Entries { get; set; } = []; }
public sealed class YmmTimelineRuntimeProjectLayerItemMappingResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public int ProjectCandidateCount { get; set; } public int LayerCandidateCount { get; set; } public int ItemCandidateCount { get; set; } public int NonEmptyLayerCollectionCount { get; set; } public int NonEmptyItemCollectionCount { get; set; } public string MappingConfidence { get; set; } = "low"; }
public sealed class YmmTimelineRuntimeDataBridgeFeasibilityResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public bool RuntimeTimelineViewFound { get; set; } public bool RuntimeTimelineViewModelFound { get; set; } public bool ActiveProjectContextFound { get; set; } public bool NonEmptyTimelineDataFound { get; set; } public string ReadOnlyRuntimeBridgeFeasible { get; set; } = "unknown"; public string Confidence { get; set; } = "low"; public IReadOnlyList<string> BlockingReasons { get; set; } = []; }
public sealed class YmmTimelineOwnerChainPathsResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<YmmTimelineOwnerPathEntry> Paths { get; set; } = []; }
public sealed class YmmTimelineRuntimeSnapshotDryRunPolicyResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public bool CanAttemptRuntimeSnapshotDryRun { get; set; } public string Reason { get; set; } = string.Empty; public IReadOnlyList<string> RequiredGuards { get; set; } = []; public int MaxCollectionSample { get; set; } public int MaxDepth { get; set; } public bool AllowPrimitiveRead { get; set; } public bool AllowCountRead { get; set; } public bool AllowSampleMetadata { get; set; } public bool AllowDeepEnumeration { get; set; } public bool AllowMutation { get; set; } }
public sealed class YmmTimelineRuntimeReadOnlySnapshotDryRunResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public string SkippedReason { get; set; } = string.Empty; public string SourcePath { get; set; } = string.Empty; public int LayersCount { get; set; } public int ItemsCount { get; set; } public int TemporalFieldsCount { get; set; } public int MediaPathFieldsCount { get; set; } public int SelectionFieldsCount { get; set; } public int SnapshotCompletenessScore { get; set; } public int ExceptionCount { get; set; } public bool FallbackPreserved { get; set; } = true; }
public sealed class YmmTimelineRuntimeSnapshotRepeatabilityResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public int IterationCount { get; set; } public int SucceededCount { get; set; } public int FailedCount { get; set; } public bool CountsStable { get; set; } public bool HashStable { get; set; } public int ExceptionCount { get; set; } }
public sealed class YmmTimelineRuntimeSnapshotPerformanceSmokeResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public long ElapsedMilliseconds { get; set; } public int ExceptionCount { get; set; } }
public sealed class YmmTimelineRuntimeSemanticDiffInputDryRunResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public int CandidateCount { get; set; } public bool ExecutionSkipped { get; set; } = true; }
public sealed class YmmTimelineRuntimeTimelineDiffInputDryRunResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public int NodeCount { get; set; } public bool ExecutionSkipped { get; set; } = true; }
public sealed class YmmTimelineRuntimeBridgeRiskReportResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public string Classification { get; set; } = "needsMoreObservation"; public IReadOnlyList<string> Reasons { get; set; } = []; }
public sealed class YmmTimelineRuntimeBridgeMilestoneSummaryResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public string Status { get; set; } = string.Empty; }
public sealed class YmmTimelineRuntimeNextSafeStepPlannerResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<string> Candidates { get; set; } = []; }
public sealed class YmmTimelineRuntimeBridgeFinalGateResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public bool ContinueObservation { get; set; } public string RuntimeReadOnlyBridgeFeasible { get; set; } = "unknown"; public string RuntimeSnapshotDryRunFeasible { get; set; } = "unknown"; public string SemanticDiffInputDryRunFeasible { get; set; } = "unknown"; public string TimelineDiffInputDryRunFeasible { get; set; } = "unknown"; public bool SmallVisibleHostRequiresManualApproval { get; set; } = true; public bool ProjectDiffWindowEmbeddingAllowed { get; set; } public bool UserFacingIntegrationAllowed { get; set; } public bool CommandExecutionAllowed { get; set; } public bool InputInjectionAllowed { get; set; } public bool TimelineReplacementAllowed { get; set; } }
public sealed class YmmTimelineRuntimeCandidateEntry { public string CandidatePath { get; set; } = string.Empty; public string CandidateTypeName { get; set; } = string.Empty; public string CandidateKind { get; set; } = string.Empty; public IReadOnlyList<string> MatchedKeywords { get; set; } = []; public string ReadSafety { get; set; } = "safe"; public string NullState { get; set; } = "unknown"; public int SemanticScore { get; set; } }
public sealed class YmmTimelineOwnerPathEntry { public string PathId { get; set; } = string.Empty; public string PathText { get; set; } = string.Empty; public string RootKind { get; set; } = string.Empty; public string TargetTypeName { get; set; } = string.Empty; public string SemanticKind { get; set; } = string.Empty; public string Confidence { get; set; } = "low"; public bool SafeReadOnly { get; set; } = true; }

public sealed class YmmTimelineDataContextBoundaryPatternResult
{
    public string Name { get; set; } = string.Empty;
    public bool Attempted { get; set; }
    public string SkippedReason { get; set; } = string.Empty;
    public string DataContextType { get; set; } = string.Empty;
    public bool GeneratedViewModelAvailable { get; set; }
    public string GeneratedViewModelTypeName { get; set; } = string.Empty;
    public bool AttachSucceeded { get; set; }
    public bool PresentationSourceAvailable { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsVisible { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public string DesiredSize { get; set; } = string.Empty;
    public string RenderSize { get; set; } = string.Empty;
    public bool DispatcherRenderPriorityReached { get; set; }
    public bool RenderingObserved { get; set; }
    public bool TemplateAppliedObserved { get; set; }
    public int BindingErrorCount { get; set; }
    public int ExceptionCount { get; set; }
    public IReadOnlyList<string> ExceptionTypes { get; set; } = [];
    public bool MinimalRenderObserved { get; set; }
    public bool DetachSucceeded { get; set; }
    public bool DisposeSucceeded { get; set; }
    public bool PatternDisposeManagedByOuterScope { get; set; }
}

public sealed class YmmTimelinePassiveEventBoundaryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public string SkippedReason { get; set; } = string.Empty;
    public bool HostCreated { get; set; }
    public bool ViewAttachedToHost { get; set; }
    public bool GeneratedViewModelAvailable { get; set; }
    public string GeneratedViewModelTypeName { get; set; } = string.Empty;
    public bool PresentationSourceAvailable { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsVisible { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public string DesiredSize { get; set; } = string.Empty;
    public string RenderSize { get; set; } = string.Empty;
    public bool DispatcherLoadedPriorityReached { get; set; }
    public bool DispatcherRenderPriorityReached { get; set; }
    public bool RenderingObserved { get; set; }
    public bool TemplateAppliedObserved { get; set; }
    public IReadOnlyList<string> ObservedEvents { get; set; } = [];
    public IReadOnlyDictionary<string, int> EventCounts { get; set; } = new Dictionary<string, int>();
    public string FirstEventName { get; set; } = string.Empty;
    public string LastEventName { get; set; } = string.Empty;
    public int ExceptionCount { get; set; }
    public IReadOnlyList<string> ExceptionTypes { get; set; } = [];
    public bool DetachSucceeded { get; set; }
    public bool DisposeSucceeded { get; set; }
    public bool FallbackPreserved { get; set; } = true;
}

public sealed class YmmTimelineCommandRouteBoundaryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public string SkippedReason { get; set; } = string.Empty;
    public bool HostCreated { get; set; }
    public bool ViewAttachedToHost { get; set; }
    public bool GeneratedViewModelAvailable { get; set; }
    public bool PresentationSourceAvailable { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsVisible { get; set; }
    public bool CommandInfrastructureObserved { get; set; }
    public int InputBindingCount { get; set; }
    public int CommandBindingCount { get; set; }
    public int RoutedCommandCount { get; set; }
    public int CommandSourceCount { get; set; }
    public bool Focusable { get; set; }
    public bool IsKeyboardFocusWithin { get; set; }
    public string FocusScopeType { get; set; } = string.Empty;
    public bool TraversalRequestAvailable { get; set; }
    public bool KeyboardNavigationObserved { get; set; }
    public bool ContextMenuPresent { get; set; }
    public bool ToolTipPresent { get; set; }
    public int ExceptionCount { get; set; }
    public IReadOnlyList<string> ExceptionTypes { get; set; } = [];
    public bool DetachSucceeded { get; set; }
    public bool DisposeSucceeded { get; set; }
    public bool FallbackPreserved { get; set; } = true;
}

public sealed class YmmTimelineVisualTreeInventoryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public bool HostCreated { get; set; }
    public bool ViewAttachedToHost { get; set; }
    public bool GeneratedViewModelAvailable { get; set; }
    public bool PresentationSourceAvailable { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsVisible { get; set; }
    public int VisualTreeNodeCount { get; set; }
    public int MaxDepth { get; set; }
    public int CommandSourceCount { get; set; }
    public int ExceptionCount { get; set; }
    public IReadOnlyList<string> ExceptionTypes { get; set; } = [];
    public bool DetachSucceeded { get; set; }
    public bool DisposeSucceeded { get; set; }
    public bool FallbackPreserved { get; set; } = true;
    public IReadOnlyList<YmmTimelineVisualNodeInfo> Nodes { get; set; } = [];
}
public sealed class YmmTimelineVisualNodeInfo
{
    public int Depth { get; set; }
    public string TypeName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string AutomationId { get; set; } = string.Empty;
    public bool IsVisible { get; set; }
    public bool IsEnabled { get; set; }
    public bool Focusable { get; set; }
    public bool IsKeyboardFocusWithin { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public string CommandSourceTypeName { get; set; } = string.Empty;
    public string CommandTypeName { get; set; } = string.Empty;
    public string CommandName { get; set; } = string.Empty;
    public string CommandParameterTypeName { get; set; } = string.Empty;
    public string CommandTargetTypeName { get; set; } = string.Empty;
    public bool CommandCanExecuteObservationSkipped { get; set; } = true;
}

public sealed class YmmTimelineBindingSurfaceInventoryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public bool BindingObservationAvailable { get; set; } = true;
    public int BindingExpressionCount { get; set; }
    public int UnresolvedBindingCount { get; set; }
    public int BindingErrorCount { get; set; }
    public int DependencyPropertySampleCount { get; set; }
    public int ExceptionCount { get; set; }
    public IReadOnlyList<string> ExceptionTypes { get; set; } = [];
    public bool DetachSucceeded { get; set; }
    public bool DisposeSucceeded { get; set; }
    public bool FallbackPreserved { get; set; } = true;
}

public sealed class YmmTimelineResourceInventoryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public int ResourceDictionaryCount { get; set; }
    public bool ApplicationResourceAvailable { get; set; }
    public int HostResourceCount { get; set; }
    public int ViewResourceCount { get; set; }
    public int StyleObservedCount { get; set; }
    public int ControlTemplateObservedCount { get; set; }
    public int DataTemplateObservedCount { get; set; }
    public int MissingResourceSignsCount { get; set; }
    public int ExceptionCount { get; set; }
    public IReadOnlyList<string> ExceptionTypes { get; set; } = [];
    public bool DetachSucceeded { get; set; }
    public bool DisposeSucceeded { get; set; }
    public bool FallbackPreserved { get; set; } = true;
}

public sealed class YmmTimelineLifecycleRepeatabilityResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public int IterationCount { get; set; }
    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }
    public int TotalExceptionCount { get; set; }
    public bool FallbackPreserved { get; set; } = true;
    public bool FinalDisposeSucceeded { get; set; }
    public IReadOnlyList<YmmTimelineLifecycleIterationResult> Iterations { get; set; } = [];
}
public sealed class YmmTimelineLifecycleIterationResult
{
    public int Index { get; set; }
    public bool HostCreated { get; set; }
    public bool ViewAttachedToHost { get; set; }
    public bool PresentationSourceAvailable { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsVisible { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public bool RenderingObserved { get; set; }
    public bool TemplateAppliedObserved { get; set; }
    public bool DetachSucceeded { get; set; }
    public bool DisposeSucceeded { get; set; }
    public int ExceptionCount { get; set; }
    public IReadOnlyList<string> ExceptionTypes { get; set; } = [];
    public bool? WeakReferenceAliveBeforeGc { get; set; }
    public bool? WeakReferenceAliveAfterGc { get; set; }
    public bool GcAttempted { get; set; }
}

public sealed class YmmTimelineExpandedVisualTreeInventoryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public int MaxVisualNodes { get; set; } = 1000;
    public int VisualTreeNodeCount { get; set; }
    public int MaxDepth { get; set; }
    public IReadOnlyDictionary<string, int> TypeHistogram { get; set; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<int, int> DepthHistogram { get; set; } = new Dictionary<int, int>();
    public IReadOnlyDictionary<string, int> CommandSourceTypeHistogram { get; set; } = new Dictionary<string, int>();
    public int ControlTypeCount { get; set; }
    public int PanelTypeCount { get; set; }
    public int ItemsControlCount { get; set; }
    public int ScrollViewerCount { get; set; }
    public int TextBlockCount { get; set; }
    public int ButtonLikeCount { get; set; }
}

public sealed class YmmTimelineLayoutSizeSweepResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public IReadOnlyList<YmmTimelineLayoutSizeSweepEntry> Entries { get; set; } = [];
}
public sealed class YmmTimelineLayoutSizeSweepEntry
{
    public string Size { get; set; } = string.Empty;
    public bool PresentationSourceAvailable { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsVisible { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public string DesiredSize { get; set; } = string.Empty;
    public string RenderSize { get; set; } = string.Empty;
    public int VisualTreeNodeCount { get; set; }
    public int BindingErrorCount { get; set; }
    public int ExceptionCount { get; set; }
    public bool RenderingObserved { get; set; }
    public bool TemplateAppliedObserved { get; set; }
    public bool DetachSucceeded { get; set; }
    public bool DisposeSucceeded { get; set; }
}

public sealed class YmmTimelineDispatcherPriorityBoundaryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public IReadOnlyList<YmmTimelineDispatcherPriorityEntry> Entries { get; set; } = [];
}
public sealed class YmmTimelineDispatcherPriorityEntry
{
    public string PriorityName { get; set; } = string.Empty;
    public bool Reached { get; set; }
    public bool PresentationSourceAvailable { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsVisible { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public string DesiredSize { get; set; } = string.Empty;
    public string RenderSize { get; set; } = string.Empty;
    public int BindingExpressionCount { get; set; }
    public int BindingErrorCount { get; set; }
    public int ObservedEventCount { get; set; }
    public int ExceptionCount { get; set; }
}

public sealed class YmmTimelineScrollContentInventoryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public IReadOnlyDictionary<string, int> TypeCounts { get; set; } = new Dictionary<string, int>();
}

public sealed class YmmTimelineViewModelSurfaceInventoryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public string ViewModelTypeName { get; set; } = string.Empty;
    public int PublicPropertyCount { get; set; }
    public int PublicMethodCount { get; set; }
    public int PublicCommandLikePropertyCount { get; set; }
    public int CollectionLikePropertyCount { get; set; }
    public int ObservablePropertyCount { get; set; }
    public IReadOnlyDictionary<string, int> PropertyTypeHistogram { get; set; } = new Dictionary<string, int>();
    public IReadOnlyList<string> ICommandPropertyNames { get; set; } = [];
    public IReadOnlyList<string> CollectionPropertyNames { get; set; } = [];
    public bool ImplementsINotifyPropertyChanged { get; set; }
    public bool ImplementsIDisposable { get; set; }
}

public sealed class YmmTimelineThemeResourceSmokeResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public bool ApplicationCurrentExists { get; set; }
    public int MergedDictionariesCount { get; set; }
    public IReadOnlyList<string> ApplicationResourceKeys { get; set; } = [];
    public IReadOnlyList<string> ViewResourceKeys { get; set; } = [];
    public IReadOnlyList<string> HostResourceKeys { get; set; } = [];
    public int MissingResourceSignsCount { get; set; }
}

public sealed class YmmTimelineSizePropagationResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public IReadOnlyList<YmmTimelineSizePropagationPatternEntry> Patterns { get; set; } = [];
}

public sealed class YmmTimelineSizePropagationPatternEntry
{
    public string PatternName { get; set; } = string.Empty;
    public double HostWidth { get; set; }
    public double HostHeight { get; set; }
    public double HostActualWidth { get; set; }
    public double HostActualHeight { get; set; }
    public string RootActualSize { get; set; } = string.Empty;
    public string RootDesiredSize { get; set; } = string.Empty;
    public string RootRenderSize { get; set; } = string.Empty;
    public string ViewActualSize { get; set; } = string.Empty;
    public string ViewDesiredSize { get; set; } = string.Empty;
    public string ViewRenderSize { get; set; } = string.Empty;
    public double ViewWidth { get; set; }
    public double ViewHeight { get; set; }
    public double ViewMinWidth { get; set; }
    public double ViewMinHeight { get; set; }
    public double ViewMaxWidth { get; set; }
    public double ViewMaxHeight { get; set; }
    public string HorizontalAlignment { get; set; } = string.Empty;
    public string VerticalAlignment { get; set; } = string.Empty;
    public bool SizeToContentEnabled { get; set; }
    public string WindowState { get; set; } = string.Empty;
    public bool PresentationSourceAvailable { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsVisible { get; set; }
    public bool LayoutUpdatedObserved { get; set; }
    public bool RenderingObserved { get; set; }
    public int ExceptionCount { get; set; }
    public IReadOnlyList<string> ExceptionTypes { get; set; } = [];
}

public sealed class YmmTimelineMeasureArrangeBoundaryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public bool FallbackPreserved { get; set; } = true;
    public IReadOnlyList<YmmTimelineMeasureArrangePatternEntry> Patterns { get; set; } = [];
}

public sealed class YmmTimelineMeasureArrangePatternEntry
{
    public string PatternName { get; set; } = string.Empty;
    public double TargetWidth { get; set; }
    public double TargetHeight { get; set; }
    public bool MeasureCalled { get; set; }
    public bool ArrangeCalled { get; set; }
    public bool UpdateLayoutCalled { get; set; }
    public string DesiredSizeAfterMeasure { get; set; } = string.Empty;
    public string RenderSizeAfterArrange { get; set; } = string.Empty;
    public string ActualSizeAfterUpdateLayout { get; set; } = string.Empty;
    public string RootActualSize { get; set; } = string.Empty;
    public string ViewActualSize { get; set; } = string.Empty;
    public string ViewRenderSize { get; set; } = string.Empty;
    public bool PresentationSourceAvailable { get; set; }
    public bool IsMeasureValid { get; set; }
    public bool IsArrangeValid { get; set; }
    public bool RenderingObserved { get; set; }
    public bool TemplateAppliedObserved { get; set; }
    public int BindingErrorCount { get; set; }
    public int ExceptionCount { get; set; }
    public IReadOnlyList<string> ExceptionTypes { get; set; } = [];
}

public sealed class YmmTimelineParentContainerVariationResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public bool FallbackPreserved { get; set; } = true;
    public IReadOnlyList<YmmTimelineParentContainerEntry> Entries { get; set; } = [];
}

public sealed class YmmTimelineParentContainerEntry
{
    public string ContainerType { get; set; } = string.Empty;
    public string ContainerActualSize { get; set; } = string.Empty;
    public string ContainerDesiredSize { get; set; } = string.Empty;
    public string ContainerRenderSize { get; set; } = string.Empty;
    public string ViewActualSize { get; set; } = string.Empty;
    public string ViewDesiredSize { get; set; } = string.Empty;
    public string ViewRenderSize { get; set; } = string.Empty;
    public bool PresentationSourceAvailable { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsVisible { get; set; }
    public int VisualTreeNodeCount { get; set; }
    public int BindingErrorCount { get; set; }
    public int ExceptionCount { get; set; }
    public IReadOnlyList<string> ExceptionTypes { get; set; } = [];
    public bool DetachSucceeded { get; set; }
    public bool DisposeSucceeded { get; set; }
    public bool FallbackPreserved { get; set; } = true;
}

public sealed class YmmTimelineLayoutConstraintDiagnosticsResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public IReadOnlyList<YmmTimelineLayoutConstraintNode> Nodes { get; set; } = [];
}

public sealed class YmmTimelineLayoutConstraintNode
{
    public int Depth { get; set; }
    public string TypeName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double Width { get; set; }
    public double Height { get; set; }
    public double MinWidth { get; set; }
    public double MinHeight { get; set; }
    public double MaxWidth { get; set; }
    public double MaxHeight { get; set; }
    public double ActualWidth { get; set; }
    public double ActualHeight { get; set; }
    public string DesiredSize { get; set; } = string.Empty;
    public string RenderSize { get; set; } = string.Empty;
    public string Margin { get; set; } = string.Empty;
    public string Padding { get; set; } = string.Empty;
    public string HorizontalAlignment { get; set; } = string.Empty;
    public string VerticalAlignment { get; set; } = string.Empty;
    public string LayoutTransformType { get; set; } = string.Empty;
    public string RenderTransformType { get; set; } = string.Empty;
    public bool ClipToBounds { get; set; }
    public bool UseLayoutRounding { get; set; }
    public bool SnapsToDevicePixels { get; set; }
    public string Visibility { get; set; } = string.Empty;
    public bool IsMeasureValid { get; set; }
    public bool IsArrangeValid { get; set; }
}

public sealed class YmmTimelineSizePropagationSummaryResult
{
    public bool AnyPatternReachedNon2x2 { get; set; }
    public string BestPatternName { get; set; } = string.Empty;
    public string LargestObservedActualSize { get; set; } = string.Empty;
    public string LargestObservedRenderSize { get; set; } = string.Empty;
    public string LargestObservedDesiredSize { get; set; } = string.Empty;
    public int PatternCount { get; set; }
    public int SucceededPatternCount { get; set; }
    public int FailedPatternCount { get; set; }
    public string LikelyBottleneck { get; set; } = string.Empty;
    public IReadOnlyList<string> Evidence { get; set; } = [];
    public string RecommendedNextPreview { get; set; } = string.Empty;
    public bool IntegrationReadiness { get; set; }
}

public sealed class YmmTimelineVisualStateInventoryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public int VisualStateGroupCount { get; set; }
    public int VisualStateCount { get; set; }
    public IReadOnlyList<string> StateGroupNames { get; set; } = [];
    public IReadOnlyList<string> StateNames { get; set; } = [];
    public int ControlsWithVisualStatesCount { get; set; }
    public IReadOnlyList<YmmTimelineVisualStateControlEntry> Controls { get; set; } = [];
}

public sealed class YmmTimelineVisualStateControlEntry
{
    public string ControlTypeName { get; set; } = string.Empty;
    public int Depth { get; set; }
    public string CurrentState { get; set; } = string.Empty;
}

public sealed class YmmTimelineAutomationInventoryResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public IReadOnlyList<YmmTimelineAutomationNodeEntry> Nodes { get; set; } = [];
}

public sealed class YmmTimelineAutomationNodeEntry
{
    public string TypeName { get; set; } = string.Empty;
    public int Depth { get; set; }
    public string AutomationId { get; set; } = string.Empty;
    public string AutomationName { get; set; } = string.Empty;
    public string HelpText { get; set; } = string.Empty;
    public string ControlType { get; set; } = string.Empty;
    public string IsOffscreen { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsKeyboardFocusable { get; set; }
    public string LabeledBy { get; set; } = string.Empty;
}

public sealed class YmmTimelineRiskClassificationResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public IReadOnlyList<string> SafeObserved { get; set; } = [];
    public IReadOnlyList<string> PartiallyObserved { get; set; } = [];
    public IReadOnlyList<string> BlockedOrUnknown { get; set; } = [];
    public IReadOnlyList<string> RiskyIfIntegrated { get; set; } = [];
    public IReadOnlyList<string> RequiresManualReview { get; set; } = [];
    public bool IntegrationReadiness { get; set; }
}

public sealed class YmmTimelineViewModelSemanticMemberClassificationResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public int PropertyCount { get; set; } public int MethodCount { get; set; } public int EventCount { get; set; } public int FieldCount { get; set; } public int CommandLikePropertyCount { get; set; } public int CollectionLikePropertyCount { get; set; } public int SelectionLikePropertyCount { get; set; } public int LayerLikePropertyCount { get; set; } public int ItemLikePropertyCount { get; set; } public int FrameTimeLikePropertyCount { get; set; } public int ZoomScaleLikePropertyCount { get; set; } public int RulerLikePropertyCount { get; set; } public int ScrollViewportLikePropertyCount { get; set; } }
public sealed class YmmTimelineViewModelSafeGetterSnapshotResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<YmmTimelineSafeGetterEntry> Entries { get; set; } = []; }
public sealed class YmmTimelineSafeGetterEntry { public string PropertyName { get; set; } = string.Empty; public string PropertyTypeName { get; set; } = string.Empty; public bool ReadAttempted { get; set; } public bool ReadSucceeded { get; set; } public string ValuePreview { get; set; } = string.Empty; public string ExceptionType { get; set; } = string.Empty; public string ExceptionMessage { get; set; } = string.Empty; }
public sealed class YmmTimelineViewModelCollectionSurfaceInventoryResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<YmmTimelineCollectionSurfaceEntry> Entries { get; set; } = []; }
public sealed class YmmTimelineCollectionSurfaceEntry { public string PropertyName { get; set; } = string.Empty; public string PropertyTypeName { get; set; } = string.Empty; public bool ImplementsIEnumerable { get; set; } public bool ImplementsIList { get; set; } public bool ImplementsICollection { get; set; } public bool ImplementsINotifyCollectionChanged { get; set; } public string GenericArgumentType { get; set; } = string.Empty; public bool SafeCountAvailable { get; set; } public int? SafeCount { get; set; } public string EnumerationSkippedReason { get; set; } = string.Empty; }
public sealed class YmmTimelineViewModelCommandSurfaceInventoryResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<YmmTimelineCommandSurfaceEntry> Entries { get; set; } = []; }
public sealed class YmmTimelineCommandSurfaceEntry { public string PropertyName { get; set; } = string.Empty; public string PropertyTypeName { get; set; } = string.Empty; public string CommandTypeName { get; set; } = string.Empty; public bool CanExecuteMethodPresent { get; set; } public bool ExecuteMethodPresent { get; set; } public string CommandParameterHints { get; set; } = string.Empty; public bool CanExecuteObservationSkipped { get; set; } = true; }
public sealed class YmmTimelineBindingToViewModelMapResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public int BindingCount { get; set; } }
public sealed class YmmTimelineVisualSemanticClassificationResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<YmmTimelineVisualSemanticEntry> Entries { get; set; } = []; }
public sealed class YmmTimelineVisualSemanticEntry { public int Depth { get; set; } public string TypeName { get; set; } = string.Empty; public string Name { get; set; } = string.Empty; public string AutomationId { get; set; } = string.Empty; public string AutomationName { get; set; } = string.Empty; public string ActualSize { get; set; } = string.Empty; public string CommandName { get; set; } = string.Empty; public string SemanticCategory { get; set; } = string.Empty; }
public sealed class YmmTimelineViewModelEventSurfaceInventoryResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public bool ImplementsINotifyPropertyChanged { get; set; } public bool ImplementsINotifyDataErrorInfo { get; set; } public bool ImplementsIDataErrorInfo { get; set; } public int PublicEvents { get; set; } public int NonPublicEvents { get; set; } }
public sealed class YmmTimelineDataCandidateDiscoveryResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<YmmTimelineDataCandidateEntry> Candidates { get; set; } = []; }
public sealed class YmmTimelineDataCandidateEntry { public string MemberName { get; set; } = string.Empty; public string MemberType { get; set; } = string.Empty; public string DeclaringType { get; set; } = string.Empty; public int SemanticScore { get; set; } public IReadOnlyList<string> MatchedKeywords { get; set; } = []; public string ReadSafety { get; set; } = string.Empty; public string RecommendedNextAction { get; set; } = string.Empty; }
public sealed class YmmTimelineSelectedStateSnapshotResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<YmmTimelineSafeGetterEntry> Entries { get; set; } = []; }
public sealed class YmmTimelinePositionScaleSnapshotResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<YmmTimelineSafeGetterEntry> Entries { get; set; } = []; }
public sealed class YmmTimelineCandidateCollectionCountSmokeResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<YmmTimelineCollectionCountEntry> Entries { get; set; } = []; }
public sealed class YmmTimelineCollectionCountEntry { public string PropertyName { get; set; } = string.Empty; public string PropertyTypeName { get; set; } = string.Empty; public bool CountAvailable { get; set; } public int? Count { get; set; } public int ExceptionCount { get; set; } public IReadOnlyList<string> ExceptionTypes { get; set; } = []; }
public sealed class YmmTimelineCandidateCollectionSampleMetadataResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<YmmTimelineCollectionSampleEntry> Entries { get; set; } = []; }
public sealed class YmmTimelineCollectionSampleEntry { public string CollectionPropertyName { get; set; } = string.Empty; public int SampleIndex { get; set; } public string ItemTypeName { get; set; } = string.Empty; public int ItemPublicPropertyCount { get; set; } public int ItemCommandLikePropertyCount { get; set; } public int ItemCollectionLikePropertyCount { get; set; } public IReadOnlyDictionary<string,string> ItemPrimitivePreviewProperties { get; set; } = new Dictionary<string,string>(); }
public sealed class YmmTimelineSemanticSurfaceSummaryResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public int DiscoveredTimelineDataCandidates { get; set; } public int DiscoveredSelectionCandidates { get; set; } public int DiscoveredFrameTimeCandidates { get; set; } public int DiscoveredZoomViewportCandidates { get; set; } public int DiscoveredCollectionCandidates { get; set; } public int DiscoveredCommandCandidates { get; set; } public int SafeReadCandidates { get; set; } public int UnsafeOrUnknownCandidates { get; set; } public string RecommendedNextPreview { get; set; } = string.Empty; public bool IntegrationReadiness { get; set; } }
public sealed class YmmTimelineProjectDataMappingFeasibilityResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<string> ProjectLikeMembers { get; set; } = []; public IReadOnlyList<string> TimelineLikeMembers { get; set; } = []; public IReadOnlyList<string> LayerLikeMembers { get; set; } = []; public IReadOnlyList<string> ItemLikeMembers { get; set; } = []; public IReadOnlyList<string> FilePathLikeMembers { get; set; } = []; }
public sealed class YmmTimelineInvestigationPhaseGateResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public bool ContinueObservation { get; set; } public string SafeToTrySmallVisibleHost { get; set; } = "unknown"; public string SafeToTryReadOnlyDataBridge { get; set; } = "unknown"; public bool SafeToTryProjectDiffWindowEmbedding { get; set; } public bool SafeToTryCommandObservation { get; set; } public bool SafeToTryUserFacingIntegration { get; set; } }
public sealed class YmmTimelineProjectRootDiscoveryResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<YmmTimelineDataCandidateEntry> Entries { get; set; } = []; }
public sealed class YmmTimelineLayerSurfaceInventoryResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public int LayerCandidateCount { get; set; } public IReadOnlyList<string> LayerTypeNames { get; set; } = []; }
public sealed class YmmTimelineItemSurfaceInventoryResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public int ItemCandidateCount { get; set; } public IReadOnlyList<string> ItemTypeNames { get; set; } = []; }
public sealed class YmmTimelineMediaPathCandidateInventoryResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<YmmTimelineDataCandidateEntry> Entries { get; set; } = []; }
public sealed class YmmTimelineTemporalSurfaceInventoryResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<YmmTimelineDataCandidateEntry> Entries { get; set; } = []; }
public sealed class YmmTimelineSelectionNavigationSurfaceResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<YmmTimelineDataCandidateEntry> Entries { get; set; } = []; }
public sealed class YmmTimelineHierarchyMappingResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<string> NodeTypes { get; set; } = []; public IReadOnlyList<string> EdgeTypes { get; set; } = []; }
public sealed class YmmTimelineSafePrimitiveSnapshotResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<YmmTimelineSafeGetterEntry> Entries { get; set; } = []; }
public sealed class YmmTimelineObservableFlowInventoryResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public int ObservableCollections { get; set; } public int EventCount { get; set; } }
public sealed class YmmTimelineSemanticDiffCandidatesResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<YmmTimelineDataCandidateEntry> Entries { get; set; } = []; }
public sealed class YmmTimelineSerializationSurfaceInventoryResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<string> MethodNames { get; set; } = []; }
public sealed class YmmTimelineInternalTypeDependencyMapResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyDictionary<string,int> NamespaceHistogram { get; set; } = new Dictionary<string,int>(); }
public sealed class YmmTimelineReadOnlyRiskHotspotsResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public IReadOnlyList<string> RiskFlags { get; set; } = []; }
public sealed class YmmTimelineSemanticBridgeFeasibilityResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public string SafePrimitiveBridge { get; set; } = "unknown"; public string SafeMetadataBridge { get; set; } = "unknown"; public string SafeHierarchyBridge { get; set; } = "unknown"; public string SafeCollectionCountBridge { get; set; } = "unknown"; public string SafeSelectionSnapshotBridge { get; set; } = "unknown"; public string SafeTimelinePositionBridge { get; set; } = "unknown"; public string RiskyMediaBridge { get; set; } = "unknown"; public string RiskyCommandBridge { get; set; } = "unknown"; public string RiskyMutationBridge { get; set; } = "true"; }
public sealed class YmmTimelineNextPhaseGateResult { public bool Attempted { get; set; } public bool Succeeded { get; set; } public bool ContinueObservation { get; set; } public string SafeToTryReadOnlyBridge { get; set; } = "unknown"; public string SafeToTrySmallVisibleHost { get; set; } = "unknown"; public string SafeToTryPassiveProjectBinding { get; set; } = "unknown"; public string SafeToTryControlledProjectSnapshot { get; set; } = "unknown"; public bool SafeToTryProjectDiffWindowEmbedding { get; set; } public bool SafeToTryUserFacingIntegration { get; set; } }
