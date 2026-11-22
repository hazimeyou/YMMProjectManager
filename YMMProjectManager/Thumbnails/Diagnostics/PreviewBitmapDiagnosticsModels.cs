namespace YMMProjectManager.Application.Diagnostics;

/// <summary>
/// プレビュー画面の探索、GetBitmap 呼び出し、画像保存までの診断結果です。
/// </summary>
//CSJ,class=PreviewBitmapDiagnosticsResult,プレビュービットマップ診断情報結果
public sealed class PreviewBitmapDiagnosticsResult
{
    //CSJ,property=DiscoverySucceeded,検出成功
    public bool DiscoverySucceeded { get; set; }
    //CSJ,property=DiscoveryLevelReached,到達した検出段階
    public int DiscoveryLevelReached { get; set; }
    //CSJ,property=WindowCount,ウィンドウ件数
    public int WindowCount { get; set; }
    //CSJ,property=VisualTreeElementCount,表示ツリー要素件数
    public int VisualTreeElementCount { get; set; }
    //CSJ,property=PreviewCandidateCount,プレビュー候補件数
    public int PreviewCandidateCount { get; set; }
    //CSJ,property=PreviewMethodCount,プレビューメソッド件数
    public int PreviewMethodCount { get; set; }
    //CSJ,property=MethodSignatureCount,メソッドシグネチャ件数
    public int MethodSignatureCount { get; set; }
    //CSJ,property=PreviewViewFound,プレビュービューを検出したか
    public bool PreviewViewFound { get; set; }
    //CSJ,property=PreviewViewModelFound,プレビュービューモデルを検出したか
    public bool PreviewViewModelFound { get; set; }
    //CSJ,property=ScenePreviewViewModelFound,シーンプレビュービューモデルを検出したか
    public bool ScenePreviewViewModelFound { get; set; }
    //CSJ,property=GetBitmapMethodFound,GetBitmapメソッドを検出したか
    public bool GetBitmapMethodFound { get; set; }
    //CSJ,property=GetBitmapSignatureCategory,取得ビットマップシグネチャ分類
    public string? GetBitmapSignatureCategory { get; set; }
    //CSJ,property=GetBitmapParameterCount,取得ビットマップ引数件数
    public int? GetBitmapParameterCount { get; set; }
    //CSJ,property=GetBitmapParameterTypes,取得ビットマップ引数型一覧
    public string[] GetBitmapParameterTypes { get; set; } = [];
    //CSJ,property=GetBitmapInvocationCandidates,GetBitmapの呼び出し候補
    public List<string> GetBitmapInvocationCandidates { get; set; } = [];
    //CSJ,property=NextRecommendedCall,次に推奨する呼び出し
    public string NextRecommendedCall { get; set; } = string.Empty;
    //CSJ,property=FalseInvocationSucceeded,GetBitmap(false)の呼び出しに成功したか
    public bool FalseInvocationSucceeded { get; set; }
    //CSJ,property=TrueInvocationSucceeded,GetBitmap(true)の呼び出しに成功したか
    public bool TrueInvocationSucceeded { get; set; }
    //CSJ,property=FalseCaptureSucceeded,GetBitmap(false)で画像を取得できしたか
    public bool FalseCaptureSucceeded { get; set; }
    //CSJ,property=TrueCaptureSucceeded,GetBitmap(true)で画像を取得できしたか
    public bool TrueCaptureSucceeded { get; set; }
    //CSJ,property=FalseBitmapSaveSucceeded,GetBitmap(false)で画像を保存できしたか
    public bool FalseBitmapSaveSucceeded { get; set; }
    //CSJ,property=TrueBitmapSaveSucceeded,GetBitmap(true)で画像を保存できしたか
    public bool TrueBitmapSaveSucceeded { get; set; }
    //CSJ,property=FalseFailureKind,GetBitmap(false)での失敗の種別
    public string? FalseFailureKind { get; set; }
    //CSJ,property=TrueFailureKind,GetBitmap(true)での失敗の種別
    public string? TrueFailureKind { get; set; }
    //CSJ,property=FalseHasAlpha,GetBitmap(false)でのアルファチャンネルの有無
    public bool? FalseHasAlpha { get; set; }
    //CSJ,property=TrueHasAlpha,GetBitmap(true)でのアルファチャンネルの有無
    public bool? TrueHasAlpha { get; set; }
    //CSJ,property=FalseWidth,GetBitmap(false)での画像の幅
    public int? FalseWidth { get; set; }
    //CSJ,property=FalseHeight,GetBitmap(false)での画像の高さ
    public int? FalseHeight { get; set; }
    //CSJ,property=FalsePixelFormat,GetBitmap(false)での画素形式
    public string? FalsePixelFormat { get; set; }
    //CSJ,property=FalseFileSize,GetBitmap(false)でのファイルサイズ
    public long? FalseFileSize { get; set; }
    //CSJ,property=FalseDurationMs,GetBitmap(false)での所要時間（ミリ秒）
    public double? FalseDurationMs { get; set; }
    //CSJ,property=FalseSavedFilePath,GetBitmap(false)での保存ファイルのパス
    public string? FalseSavedFilePath { get; set; }
    //CSJ,property=TrueWidth,GetBitmap(true)での画像の幅
    public int? TrueWidth { get; set; }
    //CSJ,property=TrueHeight,GetBitmap(true)での画像の高さ
    public int? TrueHeight { get; set; }
    //CSJ,property=TruePixelFormat,GetBitmap(true)での画素形式
    public string? TruePixelFormat { get; set; }
    //CSJ,property=TrueFileSize,GetBitmap(true)でのファイルサイズ
    public long? TrueFileSize { get; set; }
    //CSJ,property=TrueDurationMs,GetBitmap(true)での所要時間（ミリ秒）
    public double? TrueDurationMs { get; set; }
    //CSJ,property=TrueSavedFilePath,GetBitmap(true)での保存ファイルのパス
    public string? TrueSavedFilePath { get; set; }
    //CSJ,property=GetBitmapInvocationSucceeded,GetBitmapの呼び出しが成功したか
    public bool GetBitmapInvocationSucceeded { get; set; }
    //CSJ,property=CaptureSucceeded,キャプチャ成功
    public bool CaptureSucceeded { get; set; }
    //CSJ,property=PreviewViewModelTypeName,プレビュービューモデル型名前
    public string? PreviewViewModelTypeName { get; set; }
    //CSJ,property=GetBitmapReturnTypeName,GetBitmapの戻り値の型名
    public string? GetBitmapReturnTypeName { get; set; }
    //CSJ,property=BitmapWidth,ビットマップ幅
    public int? BitmapWidth { get; set; }
    //CSJ,property=BitmapHeight,ビットマップ高さ
    public int? BitmapHeight { get; set; }
    //CSJ,property=BitmapPixelFormat,ビットマップ画素形式
    public string? BitmapPixelFormat { get; set; }
    //CSJ,property=BitmapSaveSucceeded,ビットマップ保存成功
    public bool BitmapSaveSucceeded { get; set; }
    //CSJ,property=SavedFilePath,保存済みファイルパス
    public string? SavedFilePath { get; set; }
    //CSJ,property=FailureReason,失敗理由
    public string? FailureReason { get; set; }
    //CSJ,property=DiagnosticWindowsPath,診断Windowsパス
    public string? DiagnosticWindowsPath { get; set; }
    //CSJ,property=DiagnosticVisualTreePath,診断表示ツリーパス
    public string? DiagnosticVisualTreePath { get; set; }
    //CSJ,property=PreviewCandidatesPath,プレビュー候補一覧パス
    public string? PreviewCandidatesPath { get; set; }
    //CSJ,property=PreviewMethodsPath,プレビューメソッド一覧のパス
    public string? PreviewMethodsPath { get; set; }
    //CSJ,property=MethodSignaturesPath,メソッドシグネチャ一覧のパス
    public string? MethodSignaturesPath { get; set; }
    //CSJ,property=CaptureResultPath,キャプチャ結果パス
    public string? CaptureResultPath { get; set; }
    //CSJ,property=FalseCaptureResultPath,GetBitmap(false)での取得結果のパス
    public string? FalseCaptureResultPath { get; set; }
    //CSJ,property=TrueCaptureResultPath,GetBitmap(true)での取得結果のパス
    public string? TrueCaptureResultPath { get; set; }
    //CSJ,property=ComparisonPath,比較パス
    public string? ComparisonPath { get; set; }
    //CSJ,property=HistoryPath,履歴パス
    public string? HistoryPath { get; set; }
    //CSJ,property=Duration,長さ
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// PreviewViewModel から見つかった GetBitmap 系メソッドの署名情報です。
/// </summary>
//CSJ,class=PreviewBitmapMethodSignatureInfo,プレビュービットマップメソッドシグネチャ情報
public sealed class PreviewBitmapMethodSignatureInfo
{
    //CSJ,property=MethodName,メソッド名前
    public string MethodName { get; set; } = string.Empty;
    //CSJ,property=DeclaringType,宣言元の型
    public string? DeclaringType { get; set; }
    //CSJ,property=ReturnType,戻り値の型
    public string? ReturnType { get; set; }
    //CSJ,property=ParameterCount,引数件数
    public int ParameterCount { get; set; }
    //CSJ,property=Parameters,引数一覧
    public List<PreviewBitmapMethodSignatureParameterInfo> Parameters { get; set; } = [];
    //CSJ,property=IsPublic,公開されているか
    public bool IsPublic { get; set; }
    //CSJ,property=IsStatic,静的か
    public bool IsStatic { get; set; }
    //CSJ,property=MatchKeyword,キーワードに一致するか
    public string? MatchKeyword { get; set; }
    //CSJ,property=Category,分類
    public string? Category { get; set; }
    //CSJ,property=InvocationCandidates,呼び出し候補
    public List<string> InvocationCandidates { get; set; } = [];
}

/// <summary>
/// GetBitmap メソッドの個々の引数情報です。
/// </summary>
//CSJ,class=PreviewBitmapMethodSignatureParameterInfo,プレビュービットマップメソッドシグネチャ引数情報
public sealed class PreviewBitmapMethodSignatureParameterInfo
{
    //CSJ,property=Name,名前
    public string? Name { get; set; }
    //CSJ,property=Type,型
    public string? Type { get; set; }
    //CSJ,property=HasDefaultValue,既定値の有無
    public bool HasDefaultValue { get; set; }
    //CSJ,property=DefaultValue,既定値
    public object? DefaultValue { get; set; }
}

/// <summary>
/// GetBitmap を 1 回呼び出した結果と、保存できた画像のメタデータです。
/// </summary>
//CSJ,class=PreviewBitmapCaptureResult,プレビュービットマップキャプチャ結果
public sealed class PreviewBitmapCaptureResult
{
    //CSJ,property=InvocationSucceeded,呼び出しが成功したか
    public bool InvocationSucceeded { get; set; }
    //CSJ,property=ReturnType,戻り値の型
    public string? ReturnType { get; set; }
    //CSJ,property=BitmapType,ビットマップ型
    public string? BitmapType { get; set; }
    //CSJ,property=Width,幅
    public int? Width { get; set; }
    //CSJ,property=Height,高さ
    public int? Height { get; set; }
    //CSJ,property=PixelFormat,画素形式
    public string? PixelFormat { get; set; }
    //CSJ,property=SaveSucceeded,保存成功
    public bool SaveSucceeded { get; set; }
    //CSJ,property=FileSize,ファイルサイズ
    public long? FileSize { get; set; }
    //CSJ,property=SavedFilePath,保存済みファイルパス
    public string? SavedFilePath { get; set; }
    //CSJ,property=ExceptionType,例外型
    public string? ExceptionType { get; set; }
    //CSJ,property=ExceptionMessage,例外メッセージ
    public string? ExceptionMessage { get; set; }
    //CSJ,property=DurationMs,長さミリ秒
    public double DurationMs { get; set; }
    //CSJ,property=CaptureSucceeded,キャプチャ成功
    public bool CaptureSucceeded { get; set; }
    //CSJ,property=HasAlpha,アルファの有無
    public bool HasAlpha { get; set; }
    //CSJ,property=FailureKind,失敗種別
    public string? FailureKind { get; set; }
}

/// <summary>
/// GetBitmap(false) と GetBitmap(true) の試行結果を比較するための情報です。
/// </summary>
//CSJ,class=PreviewBitmapComparisonResult,プレビュービットマップ比較結果
public sealed class PreviewBitmapComparisonResult
{
    //CSJ,property=FalseSucceeded,GetBitmap(false)は成功したか
    public bool FalseSucceeded { get; set; }
    //CSJ,property=TrueSucceeded,GetBitmap(true)は成功したか
    public bool TrueSucceeded { get; set; }
    //CSJ,property=FalseInvocationSucceeded,GetBitmap(false)の呼び出しに成功したか
    public bool FalseInvocationSucceeded { get; set; }
    //CSJ,property=TrueInvocationSucceeded,GetBitmap(true)の呼び出しに成功したか
    public bool TrueInvocationSucceeded { get; set; }
    //CSJ,property=FalseCaptureSucceeded,GetBitmap(false)で画像を取得できしたか
    public bool FalseCaptureSucceeded { get; set; }
    //CSJ,property=TrueCaptureSucceeded,GetBitmap(true)で画像を取得できしたか
    public bool TrueCaptureSucceeded { get; set; }
    //CSJ,property=FalseWidth,GetBitmap(false)での画像の幅
    public int? FalseWidth { get; set; }
    //CSJ,property=FalseHeight,GetBitmap(false)での画像の高さ
    public int? FalseHeight { get; set; }
    //CSJ,property=FalsePixelFormat,GetBitmap(false)での画素形式
    public string? FalsePixelFormat { get; set; }
    //CSJ,property=FalseHasAlpha,GetBitmap(false)でのアルファチャンネルの有無
    public bool? FalseHasAlpha { get; set; }
    //CSJ,property=FalseFileSize,GetBitmap(false)でのファイルサイズ
    public long? FalseFileSize { get; set; }
    //CSJ,property=FalseDurationMs,GetBitmap(false)での所要時間（ミリ秒）
    public double? FalseDurationMs { get; set; }
    //CSJ,property=FalseFailureKind,GetBitmap(false)での失敗の種別
    public string? FalseFailureKind { get; set; }
    //CSJ,property=TrueWidth,GetBitmap(true)での画像の幅
    public int? TrueWidth { get; set; }
    //CSJ,property=TrueHeight,GetBitmap(true)での画像の高さ
    public int? TrueHeight { get; set; }
    //CSJ,property=TruePixelFormat,GetBitmap(true)での画素形式
    public string? TruePixelFormat { get; set; }
    //CSJ,property=TrueHasAlpha,GetBitmap(true)でのアルファチャンネルの有無
    public bool? TrueHasAlpha { get; set; }
    //CSJ,property=TrueFileSize,GetBitmap(true)でのファイルサイズ
    public long? TrueFileSize { get; set; }
    //CSJ,property=TrueDurationMs,GetBitmap(true)での所要時間（ミリ秒）
    public double? TrueDurationMs { get; set; }
    //CSJ,property=TrueFailureKind,GetBitmap(true)での失敗の種別
    public string? TrueFailureKind { get; set; }
    //CSJ,property=PreferredCall,優先する呼び出し
    public string? PreferredCall { get; set; }
    //CSJ,property=Reason,理由
    public string? Reason { get; set; }
}
