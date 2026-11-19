# 保護と呼出経路

ViewModel の世代保存 → ProjectGenerationService.CreateGenerationAsync → ManifestRepository.LoadAsync → Storage.ReadManifestAsync。成功した場合のみ layout / snapshot / metadata を作り、保存直前に再読込・状態確認して manifest を書く。失敗時は日本語の既存 MessageBox 経路で通知する。元 manifest は自動修復しない。

Manifest を書くサービス経路は作成と削除。削除は読込を移動前に行い、破損時には snapshot を移動しない。移動後の保存失敗は移動済みの場合だけ従来の戻し処理を行う。既に削除先がある場合は既存データを削除せず中止。Repository の直接 SaveAsync も事前確認を行う。Storage の低レベル WriteManifestAsync は検証を行わないため、将来の呼出元は repository の保護を迂回しないこと。

既存 Storage は同一ディレクトリの固定 `.tmp` への JSON 書込、File.Replace / File.Move を使う。キャンセルと共有違反による失敗では元 manifest が保持された。固定 temp、並行操作、チェックから置換までの外部変更について完全な排他はない。File.Replace と電源断耐性を同一視しない。
