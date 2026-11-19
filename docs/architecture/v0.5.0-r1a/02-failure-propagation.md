# 失敗伝播

ProjectStore は保存サービスではなく Projects / Folders を保持する容器。ViewModel → IProjectRepository.SaveAsync → JsonProjectRepository の Task は、キャンセルと通常の保存失敗を呼出元に伝える。ViewModel の既存 ExecuteWithBusyAsync はキャンセルをログに記録し、他の例外は既存 MessageBox で知らせる。展開後の登録で例外を握りつぶしていた catch は再送出する。

Load はファイル不在のみ空一覧を返し、破損・共有違反等は例外を伝播する。初期化が成功するまで追加と展開のコマンドを無効にし、SaveAsync 自体も初期化未完了を拒否する。既存 UI の実表示は未検証。追加・削除等は保存前にメモリを変更するため、保存失敗後はメモリとディスクが異なり得る。通知は未保存の可能性を伝えるが、ロールバックや明示的再試行 UI はない。再起動前に利用者が未保存の状態を把握する必要がある。
