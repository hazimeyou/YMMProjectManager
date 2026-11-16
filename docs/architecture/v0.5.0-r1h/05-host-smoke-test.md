# 限定ホスト起動

**BLOCKED / NOT_RUN**。plugin 用設定、cache、AppData、TEMP、host registry、外部 library の書込先と本番アクセス禁止が検証できておらず、専用 VM / test account と共有遮断も成立していない。YMM4 を起動せず、plugin を実ホストへ配置していない。したがって実ホストの plugin 読込、一覧表示、起動時ログ、正常終了は未確認。

再開条件: 検証可能な VM・guest アカウントと復旧地点、隔離 install・plugin placement を準備し、host 本番保存面に書き込めないことと共有 / 外部到達先を検証してから、合成データで限定起動・plugin 読込・一覧表示・正常終了を検証する。破壊的 E2E は今回の範囲外。
