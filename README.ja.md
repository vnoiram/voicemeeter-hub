# voicemeeter-hub

English: [README.md](README.md)

マシン全体で **VB-Audio Voicemeeter の Remote API セッションを1つだけ**所有するローカルな
**WebSocket サービス**。複数のアプリが `VoicemeeterRemote64.dll` を奪い合うことなく、Voicemeeter の
状態取得・操作を行えるようにする。

## 背景

Voicemeeter の Remote API は実質「1マシンにログインセッション1つ」の共有リソース。複数プロセスが
それぞれ `VoicemeeterRemote64.dll` を読み込んで `VBVMR_Login`/`VBVMR_Logout` を呼ぶと、セッションを
奪い合い、各自が状態ポーリングを重複して回す。`voicemeeter-hub` はこれを一元化する:

- DLL を読み込みログインセッションを持つプロセスは **1つだけ**。
- **サーバ側の唯一のポーラー**がパラメータ変化を検知し、購読中の全クライアントへ状態を **push** する。
  クライアントは各自ポーリングしなくてよい。
- クライアントは native DLL をリンクせず、ループバック上の小さな **WebSocket + JSON** プロトコルで話す。

Stream Dock の Voicemeeter プラグインが最初のクライアント。スクリプトや他プラグインなど、WebSocket を
話せるものなら何でも接続できる。

## プロトコル

詳細は [`docs/protocol.md`](docs/protocol.md)。要点:

- `ws://127.0.0.1:50505/`（既定ポート。`VOICEMEETER_HUB_PORT` または `--port` で上書き可）。
- リクエスト/レスポンス: `{ "id", "op", "args" }` → `{ "type": "response", "id", "result" | "error" }`。
- 状態 push: `{ "op": "Subscribe" }` を送ると `{ "type": "event", "topic": "state", "data": { ...snapshot... } }` が届く。
- ディスカバリ: 起動中のサーバが `%LOCALAPPDATA%\voicemeeter-hub\endpoint.json` に実ポートを書き出す。

## 構成

- `src/VoicemeeterHub/` — サーバ実行ファイル本体。
  - `VoicemeeterClient.cs` — P/Invoke ラッパーとログインセッション管理（native DLL に触れる唯一のコード）。
  - `HubServer.cs` / `HubConnection.cs` / `WebSocketHandshake.cs` — ループバック WebSocket サーバ。
  - `HubStateService.cs` — サーバ側の唯一の状態ポーラー兼ブロードキャスタ。
  - `VoicemeeterOperations.cs` / `HubProtocol.cs` — operation ディスパッチとワイヤ契約。
- `tests/VoicemeeterHub.Tests/` — プロトコル・ディスパッチ・ハンドシェイク、および Remote API を
  フェイク化した WebSocket のエンドツーエンドテスト（どの OS でも実行可能）。
- `docs/protocol.md` — プロトコル仕様。

## ランタイムターゲット

exe は `net8.0-windows`（Windows 常駐プロセス）。プロジェクトは、プラットフォーム非依存の部分と
テストをどの OS でもビルド/実行できるようにするためだけに、素の `net8.0` もマルチターゲットする。
DLL・レジストリ経路は実行時 Windows 専用で、テストではフェイク化される。

## ビルドとテスト

このワークスペースでは `.NET` ビルドはホストではなく Docker で行う。

```bash
# Linux の .NET SDK コンテナで両ターゲットをコンパイルし、テストを実行。
bash scripts/test-in-linux-docker.sh
```

Windows 向けの自己完結 exe をローカルで発行:

```powershell
pwsh scripts/publish.ps1
# -> dist/hub/VoicemeeterHub.exe
```

per-user インストーラをローカルでビルド（Inno Setup / `iscc` が PATH に必要）:

```powershell
pwsh scripts/build-installer.ps1 -Version 0.2.0
# -> installer/Output/voicemeeter-hub-0.2.0-setup.exe
```

## インストール

推奨は per-user インストーラ（リリースの `voicemeeter-hub-<ver>-setup.exe`、またはローカルビルド）。特徴:

- `%LOCALAPPDATA%\voicemeeter-hub\` に導入 — **管理者権限不要**
- **環境変数も不要**: このパスは Stream Dock プラグインが既に探索する場所なので、プラグインが hub を自動発見・自動起動する
- サイレントインストール対応（`voicemeeter-hub-<ver>-setup.exe /VERYSILENT`）。「サインイン時に自動起動」タスクあり（トレイ常駐用に既定オン）

他アプリは `%LOCALAPPDATA%\voicemeeter-hub\endpoint.json` で稼働中の hub を発見できる。起動もさせたい場合は `VOICEMEETER_HUB_EXE` にインストール先パスを設定する。

## CI とリリース

- `.github/workflows/ci.yml` — `main` への push と PR ごとにテストと `net8.0-windows` ビルドを実行（Ubuntu ランナー、`EnableWindowsTargeting`）。
- `.github/workflows/release.yml` — `v*` タグの push で3ジョブ実行。(1) Ubuntu でテスト＋自己完結 single-file `win-x64` `VoicemeeterHub.exe` と zip を生成、(2) Windows でそのペイロードから per-user Inno Setup インストーラをビルド、(3) zip とインストーラを生成した GitHub Release に添付。タグ（先頭 `v` を除く）がアセンブリ／インストーラのバージョンになる。

```bash
git tag v0.2.0
git push origin v0.2.0   # -> ビルドしてリリースを発行
```

## 実行

`VoicemeeterHub.exe` を起動すると:

- 二重起動を拒否（グローバル mutex。最初のインスタンスが給仕を続ける）、
- ループバックで待ち受け、endpoint ファイルを書き出す、
- コンソール窓を出さず Windows 通知領域に常駐する、
- トレイメニューから状態表示、ログを開く、終了を実行できる。

サーバを止めたい場合はトレイメニューの「Exit」から終了する。クライアントは endpoint ファイルで稼働中の hub を発見して接続できる。
