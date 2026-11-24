# HandpickerStudio

複数のComfyUI環境で生成した画像を、Checkpointごとに閲覧・整理するWindowsアプリです。

## 起動

Windows 11 x64 と **.NET 10 Windows Desktop Runtime（x64）** が必要です。Microsoftの公式ページで「.NET Desktop Runtime」のWindows x64を選んでください。 [Microsoft公式の .NET 10 ダウンロード](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

配布物はFramework-dependentのポータブルZIPです。.NETランタイムは同梱されません。書き込み可能なフォルダにZIPを展開し、`HandpickerStudio.exe` を起動してください。アプリ本体は標準ユーザー権限で利用できます。

## 画像を閲覧する

1. **登録**からプロジェクト名とCheckpointフォルダーを指定します。既存の評価を表示する場合はHandpickerSuiteの `data/checkpoint_statuses.json` を指定します。
2. **参照ルート**で画像のOutputとArchiveを登録し、保存します。登録したルートはカードから有効・無効を切り替えられます。
3. 評価別の一覧でCheckpointを選択し、画像をクリックして開きます。個別ビューワは左右キーで前後に移動できます。名前が重複するなどして所属を判定できない画像は **所属未確定** から確認できます。

## 配布ZIPのビルド

.NET 10 SDKを導入したWindows環境で、リポジトリのルートからPowerShellで実行します。

```powershell
.\build.ps1
```

`artifacts/HandpickerStudio-<version>-win-x64.zip` が生成されます。ビルドは実行ごとの作業フォルダで行い、検証済みのZIPを成果物ディレクトリに配置します。
