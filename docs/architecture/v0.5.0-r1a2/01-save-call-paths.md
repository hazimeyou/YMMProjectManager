# 一覧保存呼出経路

`ProjectListView.xaml` の追加 → `AddProjectsAsync`、削除 → `RemoveAsync`、開く → `OpenAsync`（外部起動後 LastAccess 更新）、展開 → `ExtractBundleAsync` → `RegisterExtractedProjectAsync`。各々 `ProjectListViewModel.SaveAsync` → `IProjectRepository.SaveAsync`。ProjectStore は DTO 的な容器で保存を行わない。UI は通常 token を渡さない。`ExecuteWithBusyAsync` がキャンセルをログに、その他例外をログと MessageBox に伝える。展開後登録の catch は再送出。ドラッグ&ドロップも AddProjectsAsync を使用。保存失敗後のメモリ変更は保持される。

本変更では SaveAsync の失敗・キャンセル時に未保存状態を立て、成功時に解除。保存のみを呼ぶ再試行コマンドを追加。再試行はプロジェクトの起動や YMMPX 展開を繰り返さない。開く操作・展開 UI の実操作は未検証。
