# 保存設計

旧実装は File.Create で既存 JSON を直接切り詰め、例外を吸収していた。新実装はキャンセル確認 → 同じ DTO / JsonSerializerOptions で UTF-8 シリアライズ → 保存先と同じディレクトリの一意な CreateNew 一時ファイル → 非同期書込みと FlushAsync、Flush(true) → 直前キャンセル確認 → 既存先は File.Replace、新規は File.Move とする。JSON はシリアライザー出力であり別の parse は行わない。失敗・キャンセル時に自分が作成した temp のみ削除し、削除失敗はログに記録し元の例外を保つ。置換後に cancellation を再確認しない。置換不能時に削除後移動へフォールバックしない。

複数 repository を含む同一プロセスの保存は static SemaphoreSlim で直列化する。これは別プロセスや外部編集の保護ではなく、最後に置換した内容が残る。並行要求の順序は保証しない。File.Replace / Move による同一ファイルシステム内の可視性と、Flush による電源断耐性は別であり後者の完全保証はしない。新たなバックアップは追加していない。

検証境界は internal の書込み delegate と置換直前 action に限定し、決定的な部分書込み・失敗・キャンセルのテストに使用。本番は通常の FileStream と置換を使う。ライブラリ追加なし。
