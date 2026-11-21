# DSR Meteor Pilot

Dalamud API 15 / .NET 10向けの検証版プラグインです。絶竜詩P2の南北・時計回りメテオ処理を対象とし、移動には **vnavmesh** が必要です。

## カスタムリポジトリから導入

Dalamud設定 `/xlsettings` → Experimental（実験的機能）→ Custom Plugin Repositories に、次のURLを追加して有効化・保存してください。

```text
https://raw.githubusercontent.com/MMMMXIV/DalamudPlugins/main/repo.json
```

`/xlplugins` のインストール可能なプラグインから **DSR Meteor Pilot (Validation)** を検索してインストールします。vnavmeshは別途導入してください。

以前DLLを直接登録していた場合は、その開発用登録を無効化または解除してから導入してください。新旧を同時にロードしないでください。

## 操作

- `/dsrmeteor`: 設定・状態画面。必要プラグインの状態はメニューを開くときに確認。
- `/dsrmeteor on`: 自動移動を有効化。ギミック開始前に実行。
- `/dsrmeteor off`: 観測のみ。
- `/dsrmeteor stop`: そのプルの移動を停止。
- `/dsrmeteor record on|off`: ローカル診断記録。

初期設定は観測のみです。設定は保存されます。最初の担当塔への散開は手動で行います。v0.2.6は、衝突イベントのみでは中断せず、予定経路を続行します。死亡・手動停止・イベント欠落・未到達など別の停止条件は有効です。

## 制限

南北・時計回り・反対側の最終塔という処理用です。床範囲を検出して経路を再計算する機能はありません。ゲーム更新や通信遅延、他の移動制御との競合で動作が変わる場合があります。

## ビルド

.NET 10 SDKとDalamud API 15開発用ファイルを用意します。

```powershell
dotnet build ./DSRMeteorPilot.csproj -c Release
```

ローカルDalamudの標準参照先は `%APPDATA%/XIVLauncher/addon/Hooks/dev`。SDK方式では `DALAMUD_HOME` で変更できます。代替の `build-offline.ps1` は.NET 10上のPowerShellと、そのRoslyn・refフォルダを利用します。

```powershell
pwsh -File ./build-offline.ps1
pwsh -File ./tests/run.ps1
```

公開テストには合成タイミングデータを使用しています。プレイヤーの実ログや個人情報は含みません。テスト成功はゲーム内の動作保証ではありません。

## 配布構成

- `repo.json`: Dalamudが読み込む配布一覧。
- `dist/DSRMeteorPilot-0.2.6.0.zip`: DLLとマニフェストをZIP直下に格納。
- `src/`: 対応するソース。

更新時はアセンブリとマニフェストのバージョンを上げ、新しいバージョン名のZIPを作成して、repo.jsonのバージョン・ダウンロード先・LastUpdateを更新してください。以前のZIPを上書きしない運用を推奨します。


共通の配布一覧は [DalamudPlugins](https://github.com/MMMMXIV/DalamudPlugins) で管理します。新しい版を公開するときは、そちらの `repo.json` も更新してください。このリポジトリの旧 `repo.json` は既存利用者向けに残しています。新規導入には共通URLを使用してください。
