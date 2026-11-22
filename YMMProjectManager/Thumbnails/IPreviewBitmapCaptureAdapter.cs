namespace YMMProjectManager.Application.Thumbnails;

/// <summary>
/// 現在のプレビュー画面からサムネイル用のビットマップを取得する抽象化です。
/// </summary>
//CSJ,interface=IPreviewBitmapCaptureAdapter,プレビュービットマップキャプチャアダプターインターフェース
public interface IPreviewBitmapCaptureAdapter
{
    /// <summary>
    /// プレビュー取得を試み、失敗時も例外ではなく理由付きの結果を返します。
    /// </summary>
    //CSJ,method=TryCaptureAsync,キャプチャを試みる（非同期）
    Task<PreviewCaptureResult> TryCaptureAsync(CancellationToken cancellationToken);
}
