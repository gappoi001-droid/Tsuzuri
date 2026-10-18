# V1.1.1 Astrafix

2026-10-17。V1.1.1 の WinForms UI を改修しました。配布先は `../Release/V1.1.1-Astrafix/` です。

## 変更

- 左にライブラリ、右に選択したゲームの詳細と大きな起動ボタンを配置。淡い背景と紫のアクセントを採用。
- ゲーム名の検索、「すべて / この PC / 要確認」の絞り込み、最近プレイした順の表示を追加。
- プレイ時間・最終プレイ日・最終同期・保存先を表示。長いゲーム名は詳細側で折り返し、一覧では省略とツールチップに対応。
- 起動ボタンは未設定ゲームでは「この PC で設定」に切り替え。実行ファイル不在時の起動と手動同期は無効。
- 手動同期とクラウドのバックアップを「セーブの操作」、登録情報と登録解除を「その他」に整理。両メニューのボタンは詳細欄の下端に固定。
- 処理段階を下部に表示。ゲーム起動中は待機表示になり、終了後の同期へ移る。処理中に閉じる場合は、中断と自動アップロード停止の確認を表示。
- キャンセルを成功扱いしないように修正。絞り込み中に状態が「同期中」へ変わっても、操作対象のゲームを保持。
- 登録画面を保存先・検出理由・ファイル数・更新日の表に変更。空や未作成の候補を区別し、選択数を表示。ファイルがある候補と入れ子の初期選択ルールは維持。
- Ctrl+F / Ctrl+N / F5 と、一覧での Enter / ダブルクリックに対応。

## 確認

| 項目 | 結果 |
| --- | --- |
| Release ビルド | 警告 0、エラー 0 |
| Core の回帰テスト | 122 件成功、失敗・スキップ 0。`TestResults/astrafix-core.trx` |
| UI の動作チェック | 16 項目成功。`.qa/ui-checks.txt` |
| 実画面 | 通常表示、最小ウィンドウ、最大化、長いゲーム名、未登録、実行ファイル不在、未設定、登録候補のチェック、手動操作メニュー、接続エラーの案内を確認 |
| 配布形式 | Windows x64、自己完結型、圧縮した単一 EXE + appsettings.json + README.txt |
| Core のソース | 改修前の SHA-256 と一致 |
| V1.0.0 / V1.1.0 / V1.1.1 の配布物 | 改修前の SHA-256 と一致 |

UI の動作チェックは、検索一致と一致なし、最近プレイした順、プレイ時間表示、状態フィルター、未設定・実行ファイル不在時のボタン制限、絞り込み中の同期への遷移、処理の重複防止、最小サイズでのメニュー表示、入れ子候補の初期選択、表示名・保存先の入力条件を含みます。

画面確認は `.qa/` 内の隔離したテストアプリで行いました。設定と登録はテスト用フォルダに限定し、資格情報の読み書き・HTTP・実ゲームの起動をテスト用実装に差し替えています。実アカウントでの同期、実ゲームでのプレイ、別 PC と高 DPI 環境での実機確認は今回行っていません。

## 再ビルド

`app/` で実行します。SDK と依存パッケージの準備後は以下で発行できます。

```powershell
.\dotnet.ps1 publish src/VNsave.App/VNsave.App.csproj -c Release -r win-x64 --self-contained true --no-restore --output ../Release/V1.1.1-Astrafix '/p:PublishSingleFile=true' '/p:EnableCompressionInSingleFile=true' '/p:IncludeNativeLibrariesForSelfExtract=true' '/p:DebugType=None' '/p:DebugSymbols=false' --disable-build-servers '/m:1'
```

編集した実装は `src/VNsave.App/` の `MainForm.cs`、`MainForm.Layout.cs`、`MainForm.Workflows.cs`、`RegistrationForm.cs`、`UiTheme.cs`、`VNsave.App.csproj` です。旧 UI のソースは `.qa/baseline/` に保管しています。
