# 保存経路

作成: ProjectGenerationService.CreateGenerationAsync → ManifestRepository.LoadAsync → Snapshot / metadata 作成 → SaveValidatedAsync → Storage.WriteManifestIfUnchangedAsync。削除: LoadAsync → 旧世代ディレクトリを deleted に移動 → SaveValidatedAsync、失敗時に元へ戻す。Repository の公開 SaveAsync は自ら load してから同じ経路を使う。R1-B の破損・欠落検査を維持。

R1-B では WriteManifestAsync の `manifest.json.tmp` は File.Create で切り詰められ finally で削除され、無関係な既存 temp を失った。再読込は Valid / Missing の状態のみ比較し、異なる有効 JSON を識別しなかった。修正後は元データを解析した同じバイト列の hash を保持し、一意な CreateNew temp への書込み・FlushAsync / Flush(true) 後、Manifest パスごとの semaphore 内で現ファイルの hash を比較し、一致時だけ File.Replace / 新規 File.Move する。呼出元へ例外を伝える。直接の低レベル Storage.WriteManifestAsync は同じ書込みロックを使うが、読込からの古いデータを識別する revision を受け取らない。製品コードの作成・削除は Repository の比較経路を通る。
