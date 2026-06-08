# Thumbnail Fast Generation Investigation RC1

## 目的

YMM4 内部のプレビュー更新とシークを使って、既存のサムネイル生成より軽い経路を調査する。
今回の段階は本実装ではなく、実験モード付きの試作と調査記録に留める。

## 調査結果

### YMMKeyboard 側

- `Timeline.CurrentFrame` を `Dispatcher` 上で変更する方針は有望
- `Window.CommandBindings` から `ScrollToFrame` などを探して実行する代替案もある
- `SendInput` による自動操作は採用しない
- `UI スレッド外で CurrentFrame を触る` 方式は採用しない

### YMMMultiPreview 側

- `PreviewViewModel.GetBitmap()` を reflection で呼ぶ方向を優先
- `SaveImageToClipboard()` は fallback 候補に留める
- `RenderTargetBitmap` を本命にはしない
- 画面キャプチャ常用とクリップボード常時上書きは避ける

## 採用候補

- シーク: `Timeline.CurrentFrame` を `Dispatcher` 上で設定
- 画像取得: `PreviewViewModel.GetBitmap()` を reflection で呼び出し
- 保存: 取得 bitmap を 64 点サンプリングの各フレームに対して PNG 保存

## 実装方針

- 新方式は `ExperimentalFastThumbnailGenerationEnabled` が `true` の時だけ動かす
- 失敗時は既存の `FastClipboardThumbnailGenerator` に fallback する
- `AllowClipboardFallback` と `AllowScreenCaptureFallback` は RC1 では既定無効
- `GetBitmap` が見つからない場合や preview VM が見つからない場合は例外にせず失敗結果を返す

## 既知の制約

- YMM4 実機の preview VM 検索は、対象アセンブリが既にロードされている前提
- singleton 取得に `Current` / `Instance` / `Default` を仮定している
- `BitmapSource` と `System.Drawing.Bitmap` 以外の戻り値は現時点では保守的に失敗扱い
- `AllowClipboardFallback` と `AllowScreenCaptureFallback` は今後の拡張余地として保持

## 手動検証項目

1. 通常サムネイル生成が壊れていない
2. Fast mode disabled で既存挙動のまま
3. Fast mode enabled で seek が動作する
4. preview bitmap を取得できる
5. 64 点サンプリングできる
6. 失敗時に既存方式へ fallback する
7. 連続実行で落ちない
8. YMM 最小化時の挙動
9. YMM 非アクティブ時の挙動

## 次フェーズ条件

- preview VM の発見率が実運用で十分
- `GetBitmap` の戻り値型が安定
- 実機で sample/capture/retry の計測結果が安定
- 既存サムネイル生成と比較して劣化がない
