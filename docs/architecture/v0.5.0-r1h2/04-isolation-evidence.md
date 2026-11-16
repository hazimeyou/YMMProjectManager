# 隔離証跡 — BLOCKED

guest がないため、共有フォルダー、clipboard、drive / USB redirection、network、本番設定・plugin・一覧・Generation・素材・cache・logへのアクセス不可と guest の実効権限は未検証。本番データに対する書込み試行は行わない。VM の設定値だけで成立と判定しない。

再開時は、新規 guest の試験アカウントから guest 側に見える接続・共有・network と実効アクセスを確認し、ホスト本番データを実際に変更せず、試験専用の合成 sentinel を使用する。外部への credential 持込や共有経路がないことと、判明した各 YMM4 / plugin の書込み先が guest 内に収まることを証明する。前提条件や結果を記録し、未解明な経路が残れば限定起動を行わない。
