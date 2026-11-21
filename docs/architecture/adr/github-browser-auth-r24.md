# ADR — GitHubブラウザ認証の信頼境界（R24）

対象を照合済みのGCM 3.0.2 Windows x64に限定してブラウザ認証を採用する。既存の保存済みGCM経路は引き続き利用できる。

| 境界 | R24で確認した契約 |
|---|---|
| 製品Git通信 | リテラルのGitHub HTTPS URL、承認した操作、対象と物理リポジトリの照合、TLS検証、リダイレクト拒否。Clone・Fetch・Pull・Pushの承認は個別に維持する |
| GCM認証通信 | 標準ブラウザ認証コマンドを第一候補とする。Git側で設定した `http.followRedirects=false` は、ヘルパーのOAuth用HttpClientの制約にならない。実行ファイル、ランタイムとHTTP境界は別に確認する |
| ブラウザ | 既定ブラウザはGitHubの認証画面を開く役割。製品に認証文字列やトークンを戻す方式は採用しない |
| 資格情報 | 保存済みのWindows Credential Managerを既存GCMが再利用。製品は新しいトークン保存を行わない。初回・再ログインはGCMの終了と制限プロキシの結果を確認し、不確定なら明示する |
| コールバック | ヘルパーのループバック、OAuth state、PKCEの契約を実行ファイルの版に結び付けて確認する。ブラウザの実挙動と本物の資格情報の保存は実環境受入で確認する |

## 比較と実測

標準GCMの `github login --browser` と `--force` は、ブラウザ起動、再認証、ヘルパー所有の資格情報保存を備えている。旧版・新版にGCM自身のHTTPリダイレクトを拒否するブラウザ認証設定は確認できなかった。インストール済み2.9.1は署名有効なMicrosoft版で .NET Framework 4.7.2 を対象とする。同じCLRの `WebProxy.IsBypassed` は、明示プロキシかつ `BypassProxyOnLocal=false` でも、127.0.0.1、localhost、::1に対して真だった。github.comに対しては偽。同じ構成で2.9.1本体にも、ストア無効・合成文字列・2つの隔離ループバックリスナーでAPI接続を試した。プロキシへの接続は0、別の宛先への直接接続は1、ヘルパーは非0終了だった。したがって、この環境の2.9.1と制限プロキシの組み合わせは宛先制約の根拠にならない。

公式ポータブル3.0.2のZIPは公開SHA-256 `9D2BEF6E42A37654EA7EF33172BBCF1B6A1DF6191FCF6A57DFBF39E1CBC27A0F` と一致し、実行ファイルはMicrosoft署名が有効で、SHA-256は `A139DBB2DB5238266B53D6596FA02880A25CDB27D709DA547147C28159D40CF0`。インストールせずテンポラリで調査した。資格情報ストアを無効にし、合成文字列だけを使うAPIテストでは、Gitのコマンドスコープで設定した明示プロキシへループバック宛てのCONNECTが届いた。宛先の別リスナーは接続を受けず、プロキシが拒否し、子プロセスは失敗した。さらに公式v3.0.2のソースからビルドしたGitHubOAuth2Clientで、同じHttpClientFactoryを使う合成OAuthコード交換を検証した。通常交換が成功し、307・308は拒否側のCONNECTへ到達した。転送先TLSサーバーへの到達はいずれも0。署名済み子プロセスの実ブラウザ受入とWindowsストアへの本物の保存は別に必要。

OSのシステム全体のプロキシ・ファイアウォールを変更する方式、アプリ独自のOAuth・資格情報ストア、隠した端末で待機する方式は採用しない。製品はProgram Files配下の管理者管理領域から、Microsoft署名済み公式配布物と同じSHA-256・サイズのGCM 3.0.2 x64だけを認識する。親ディレクトリを保持し、GCMとGit本体を読み取りで保持して起動までの置換を防ぐ。一般利用者の書込許可や不明なパスは拒否する。PATHやリポジトリ設定に由来する認証ヘルパーを選ばず、固定Gitパスと分離したGit設定を与える。

ブローカーは排他的に確保したIPv4ループバック上で、github.com:443とapi.github.com:443へのCONNECTだけを許可する。HTTP、他の宛先、プロキシ認証は拒否し、TLSはGCMと承認した宛先の間で保持する。Windows既定資格情報をプロキシに提供しないようセッション限りの合成プロキシ資格情報を設定する。製品は認証ヘルパーの標準入出力をOSのnullデバイスに接続する。

GCM本体は起動時からWindows Jobに属する。認証を中止すると転送を停止し、GCM本体の終了を確認してからリスナーを解放する。ブラウザやGit設定取得用の子プロセスの寿命は別で、OAuthの認証通信は照合済みGCM本体が行う。結果確認前に終了した場合、Windows資格情報ストアの更新可能性を表示して、Gitの承認や自動再試行を行わない。複数アカウントが保存済みの場合のアカウント指定UIは今回含まない。

[GCM 2.9.1のプロキシ実装](https://github.com/git-ecosystem/git-credential-manager/blob/v2.9.1/src/shared/Core/HttpClientFactory.cs) · [GCM 3.0.2のプロキシ実装](https://github.com/git-ecosystem/git-credential-manager/blob/v3.0.2/src/Core/HttpClientFactory.cs) · [公式配布物とチェックサム](https://github.com/git-ecosystem/git-credential-manager/releases/expanded_assets/v3.0.2) · [Windows資格情報ストア](https://github.com/git-ecosystem/git-credential-manager/blob/v3.0.2/docs/credstores.md)

Windows上の既定ブラウザ、証明書ストア、OSのプログラム配置は、このC#製品から独立した利用者・管理者の信頼境界になる。認証用プロキシはTLSを終端せず、平文の資格情報を受領・保存しない。
