# Tsuzuri

個人利用の Windows 用ランチャーです。[CORE_IMPLEMENTATION.md](../CORE_IMPLEMENTATION.md) に従い、起動前に Dropbox とセーブを同期し、ゲーム終了後に変更をアップロードします。C# / .NET 10 / WinForms を使い、実行時の外部 NuGet 依存はありません。`proto/` へのプロジェクト参照はありません。

## 起動

1.3.0 からアプリの表示名と実行ファイル名は `Tsuzuri` / `Tsuzuri.exe` です。既存のアプリを閉じてから起動してください。設定・接続・ゲーム登録は従来と同じ保存先を使用します。

左側でゲームを探し、右側の「ゲームを起動」で遊ぶ構成です。手動同期とバックアップは「セーブの操作」、ゲームフォルダ・セーブデータのフォルダを開く操作と、登録情報・登録解除は「その他」にあります。右上の点は Dropbox の状態です（緑: 接続中、赤: 接続できない、灰: 未接続）。オフラインのときは、起動時とゲーム終了後の同期でダイアログを出さず、赤い点と下の一行で知らせます。終了後のセーブは未送信として残り、つながった後の起動時・ゲーム起動時・`F5` で送られます。状態別の絞り込み・最近プレイした順の表示に対応しています。右上の ☾ / ☀ でダークモードとライトモードをその場で切り替えられます（この PC に記録。未設定なら Windows のアプリ モードに合わせます）。`Ctrl+N` で追加、`F5` でクラウドの一覧を更新し、一覧で `Enter` またはダブルクリックすると選択したゲームを起動できます。

Windows x64 用に発行した一式を置き、`Tsuzuri.exe` を起動します。下記の自己完結型の発行コマンドを使った場合は .NET の別途インストールは不要です。

1. 「接続する」でブラウザの Dropbox 認可を行います。試作とは別の資格情報を使うため、本体では初回に認可が必要です。
2. 「ゲームを追加」で実行ファイルかショートカットを選び、表示名と保存先を登録します。候補がなければ「フォルダを追加」で指定します。
3. 一覧でゲームを選び「ゲームを起動」を押します。同期後にゲームを起動し、終了後は書き込みが落ち着いてからアップロードします。
4. 2 台目でも接続すると共有ゲームが「この PC で未設定」と表示されます。「この PC で設定」で実行ファイルと保存先を対応付けます。

通信できない起動前には「再試行」「同期せずに起動」「キャンセル」を表示します。競合時の採用は自動です。手動ダウンロードには確認があり、未送信データもバックアップしてから最新を適用します。登録解除はこの PC の登録だけを外し、セーブ・共有ゲーム・履歴は残します。

## ビルドとテスト

Windows と .NET 10 SDK が必要です。以下は `app/` で実行します。

```powershell
dotnet restore VNsave.sln --locked-mode
dotnet build VNsave.sln -c Release --no-restore
dotnet test VNsave.sln -c Release --no-build --no-restore
dotnet publish src/VNsave.App/VNsave.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

この作業環境では `dotnet` の代わりに `./dotnet.ps1` を使えます。スクリプトは `app/.tools/`、既存の `proto/.tools/`、PATH の順に SDK を探します。後者は開発用 SDK の再利用だけで、アプリのソース参照や実行時依存ではありません。キャッシュと作業ファイルは `app/` 内に置きます。

## データの置き場所

| データ | 保存先 |
| --- | --- |
| 初期の App key・接続設定 | 実行ファイルに埋め込み（元は `src/VNsave.App/appsettings.json`、1.3.1 から） |
| 利用する接続設定 | `%LOCALAPPDATA%\VNsave\settings.json`（初回にコピー） |
| ゲーム登録・端末 ID・前回同期の記録 | 同フォルダの `games.json` |
| 画面の配色（`dark` / `light`） | 同フォルダの `theme.txt` |
| 固定済みの未送信データ | `pending\<game_id>\<snapshot_id>\` |
| 上書き前のバックアップ | Dropbox の `/vnsave/games/<game_id>/backups/<ID>.zip`（ローカルには残さない。アップロードできなければ上書きしない） |
| プレイ時間 | Dropbox の `/vnsave/games/<game_id>/playtime/`（1 回のプレイごとに 1 ファイル。名前に開始時刻と秒数を含み、一覧だけで 2 台分を合計する） |
| 未送信のプレイ時間 | `playtime\<game_id>\`（送信できたら消す） |
| 動作ログ | `logs\vnsave.log`（トークンは伏せ字） |
| refresh token | Windows 資格情報マネージャーの `VNsave/dropbox` |
| 共有データ | Dropbox App folder の `/vnsave/games/<game_id>/` |

`/app.json`、`/_proto/`、`VNsave.Proto/dropbox`、`%LOCALAPPDATA%\VNsave.Proto\` は使いません。ゲームのプロセスをジョブに入れて終了を待ちます。Tsuzuri を閉じてもゲームは終了させません。管理者権限が必要な場合は、昇格した直接のプロセスの終了を待ちます。

## 判断メモ

- 1.3.0 の改名では、PC 内の `%LOCALAPPDATA%\VNsave`、Dropbox の `/vnsave/...`、資格情報名 `VNsave/dropbox`、`appsettings.json` の名前と内容を維持します。名前空間・プロジェクト名・ソリューション名も従来どおりです。既存の登録・同期記録・ログイン状態を引き継ぐための内部名です。
- 内容ハッシュは、Windows のパス比較に合わせて小文字化した `<論理 ID>/<相対パス>` と SHA-256 の組をパス順に並べ、UTF-8 の JSON 配列にして SHA-256 を取ります。空は「セーブなし」で、新しい空世代は公開しません。
- 双方が変更され保存時刻も同じ場合はクラウドを採用します。世代同士で同時刻なら snapshot_id の辞書順で大きい方です。
- コピー中に変化した場合は全保存先を最大 3 回取り直します。リンク・ジャンクション・ZIP 内の外部パスは拒否します。
- PC 固有の手動保存先はクラウドで `Manual` とし、2 台目で再指定します。入れ子の候補は深い方だけを初期選択します。
- ローカル登録と未送信データはアカウントに結び付け、別アカウントへの誤送信を止めます。クラウド履歴の削除や別アカウントへの移行は実装しません。
- 終了後 30 秒でも書き込みが落ち着かない場合は、アップロードせずエラーにします。ゲーム終了後にアプリを自動終了する機能はありません。
- 一覧の適用・カーソル再取得は [Dropbox 公式スキーマ](https://github.com/dropbox/dropbox-api-spec/blob/main/files.stone)、既知フォルダとジョブ管理は [Microsoft の既知フォルダ一覧](https://learn.microsoft.com/en-us/windows/win32/shell/knownfolderid)・[ジョブへの割り当て](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-assignprocesstojobobject) に従います。

実アカウント・実ゲームでの同期確認は利用者と Claude が行います。実装時の確認は [VALIDATION.md](VALIDATION.md) に記録します。
