# 実装時の確認

最新の V1.1.1 UI 改修と 122 件の回帰テストの結果は [ASTRAFIX.md](ASTRAFIX.md) を参照してください。以下は最初の本体実装時の記録です。

- 日付: 2026-10-17（日本時間）
- 対象: `CORE_IMPLEMENTATION.md` に従う個人用 WinForms アプリ。
- 環境: Windows x64、.NET SDK 10.0.402、.NET / Windows Desktop 10.0.13。

| 確認 | 結果 |
| --- | --- |
| Release ビルド | 成功、警告 0、エラー 0 |
| xUnit | 99 件成功、失敗 0、SKIP 0 |
| Windows x64 自己完結型 publish | 成功、`artifacts/win-x64/`、222 ファイル、114,330,101 bytes |
| WinForms のメイン画面 | 隔離したハーネスで表示確認。ゲーム一覧・全ボタン・状態・結果の 1 行を確認 |
| 実行ファイル不在の行 | 「インストール先エラー」。起動・アップロード・ダウンロードは無効、登録解除は可能 |
| ジョブオブジェクト | 一時フォルダへコピーした Windows のコマンドプロセッサーで、一時停止での作成・ジョブ割り当て・再開・終了待ちをテスト |
| `proto/` | 作業開始時に記録したソース・設定・文書・発行物等 239 ファイルの SHA-256 がすべて一致 |
| 実行時の外部 NuGet | 追加なし。テスト用パッケージだけを使用 |

テスト結果は [core-tests.trx](TestResults/core-tests.trx) です。

```powershell
.\dotnet.ps1 build VNsave.sln -c Release --no-restore --disable-build-servers '/m:1' --verbosity minimal
.\dotnet.ps1 test VNsave.sln -c Release --no-build --no-restore '/m:1' --logger 'trx;LogFileName=core-tests.trx' --results-directory TestResults --verbosity minimal
.\dotnet.ps1 publish src/VNsave.App/VNsave.App.csproj -c Release -r win-x64 --self-contained true --no-restore --output artifacts/win-x64 --disable-build-servers '/m:1'
```

テストは判断表の各行、内容ハッシュの並び順と空データ、複数保存先の ZIP 往復、ZIP のパス逸脱・ハッシュ不一致・余分なファイルの拒否、適用前の退避・不要ファイルの除去・失敗時の ZIP からの復元、各エンジンの保存先候補、重複・入れ子・ゲーム本体・1 GB 超の拒否、未送信データの固定と再送を含みます。偽の Dropbox を共有する 2 つのローカル保存先で、初回の取り込みと次の世代の更新、同じ内容を手動アップロードした際の省略、手動ダウンロード時の pending の退避も確認しています。

試作から、ハッシュ・PKCE・OAuth のループバック受信・HTTP 再試行・セッション転送・トークン失敗・秘匿処理に関係するテストをコピーしました。CLI 用の案内文は本体のボタン名に合わせています。HTTP と資格情報はテストダブルであり、本物の資格情報マネージャーへの保存は行っていません。

画面確認用の `.qa/` は配布物に含めません。画面にはテスト用の登録だけを与え、保存先を `.qa/data/` に限定し、HTTP を拒否するハンドラーと空のメモリ資格情報を使用しました。認証画面の操作はしていません。

## 発行物

`artifacts/win-x64/` 一式を使います。`runtimeconfig.json` の `includedFrameworks` に .NET と Windows Desktop の 10.0.13 が入り、必要なランタイム DLL が同梱されています。

| ファイル | bytes | SHA-256 |
| --- | --- | --- |
| `VNsave.App.exe` | 162304 | `1b1c854e66db73218f5be6fd8bf323d4eac0369afdfab9e7161c5fdfdba8eedf` |
| `VNsave.App.dll` | 46080 | `77e06b30e1d04051b0fa5f130bef4e147bb83d0ef3568e4966f8c58b096ff68b` |
| `VNsave.Core.dll` | 194560 | `f0cb67ade04cc8eb9697410c2b58089a1bc91b1b51a3ccf557d14018f6b7f470` |

## 未実施の確認

実アカウントでの認可・送受信、実ゲームでの起動・子プロセス追跡・昇格・終了後の同期、別 PC での使用は未実施です。これらを確認済みとは扱いません。利用者と Claude による実機確認が必要です。
