# History Preview Roadmap

## Positioning

`v0.2.9-history-preview` は正式仕様ではなく、設計検証のための preview 系列です。

## Priorities

1. 基盤安定化（最優先）
2. YMM意味差分の精度向上
3. 純正TL + DiffTL 構成の段階導入

## Stability Checklist

- 大規模 `.ymmp` での動作
- 長時間運用
- Snapshot肥大化
- 壊れたJSON耐性
- メモリ使用量
- Diff速度
- 大量差分時の挙動

## Semantic Diff Evolution

現行マッチングは `Text / FilePath / Frame / Layer / Length` ベースです。
同一テキスト複数、同一素材複数、移動、複製を安定追跡するため、Internal Item ID を検討します。

候補:

`hash(Type + Timeline + InitialPosition + Text + FilePath)`

## Timeline Policy

- 純正TL側: YMM4-Timeline の設計（SetTimelineToolInfo, Dispose, scene切替処理）を参考に再実装
- DiffTL側: Snapshot差分専用の自作読み取り専用Timeline

DiffTL初期要件:

- 横軸: Frame
- 縦軸: Layer
- 表示: Added / Removed / Modified / Moved
- クリックで差分詳細表示

## Release Plan

### v0.3.x

- 基盤安定化と計測
- 差分モデル改善
- UIは必要最小限の改善に留める

### v0.4.0

- `純正TL + DiffTL + DiffDetailPanel` 構成の本格導入
- DiffTLの可視化強化
- restore / branch / merge は対象外のまま
