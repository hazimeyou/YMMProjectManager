namespace YMMProjectManager.Application.Thumbnails;

/// <summary>
/// 現在のプレビュー画面からサムネイル用のビットマップを取得する抽象化です。
/// </summary>
public interface IPreviewBitmapCaptureAdapter
{
    /// <summary>
    /// プレビュー取得を試み、失敗時も例外ではなく理由付きの結果を返します。
    /// </summary>
    Task<PreviewCaptureResult> TryCaptureAsync(CancellationToken cancellationToken);
}
