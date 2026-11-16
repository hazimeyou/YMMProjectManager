# 特殊パス

現基準には PSD / 連番の専用対応がなく、未統合 branch `fix/v0.5.0-relink-psd-layer-path` / `fix/v0.5.0-relink-png-sequence` に関連調査・修正が存在する。PSD の EnableLayersFilePath と VideoItem PNG 連番の意味を今回実ホストで確認できないため、PSD と VideoItem の PNG の更新はファイル・Timeline の双方で事前拒否する。正常な特殊素材再リンクの互換性は未達。未統合作業や本番データは変更していない。

合成文字列では画像・音声、動画の絶対パス、日本語、Unicode、相対パス、JSON エスケープ、300文字の候補を検証。長いパスは JSON 文字列だけの検証であり、Windows の実ファイルへのアクセスを保証しない。元パスの相対解決や同名候補検索・PNG の実シーケンス検証は今回未実施。
