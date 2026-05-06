# Third-Party Notes

## routersys/YMM4-Timeline

- Repository: [https://github.com/routersys/YMM4-Timeline](https://github.com/routersys/YMM4-Timeline)
- License: MIT License
- Copyright: `Copyright (c) 2026 routersys`

## Usage Policy in YMMProjectManager

- preview11 時点では「参考実装として調査」のみ
- ソースコードの同梱・再配布は未実施
- `FutureYmmTimelineAdapter` は独自 scaffold であり、直接コピーではない

## If Source Is Imported Later

- MIT ライセンス文の同梱が必要
- 由来（repo URL, commit/branch, 取り込み対象ファイル）を docs に記録する
- 変更を加える場合は差分と改変意図を追跡可能に残す

## Distribution Note

- YMMProjectManager 単体配布を維持するため、YMM4 依存は optional adapter 経由に限定する
