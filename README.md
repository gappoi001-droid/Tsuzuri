# Tsuzuri

ビジュアルノベルのセーブを、Dropbox の App フォルダ経由で 2 台の Windows PC の間で同期する個人用ツールです。ゲームの起動前と終了後に自動で同期し、上書きする前のセーブは Dropbox に退避します。

- `app/` — 本体（C# / .NET 10 WinForms）。使い方・データの置き場所・ビルド方法は [app/README.md](app/README.md) にあります。
- `assets/branding/` — アイコンの原画と生成プロンプト。

内部の名前（名前空間、`%LOCALAPPDATA%\VNsave`、Dropbox の `/vnsave/`）は、旧名 VNsave のままです。
