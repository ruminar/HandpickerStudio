# HandpickerStudio

複数のComfyUI環境で生成した画像を、Checkpointごとに閲覧・整理するWindowsアプリです。

## 起動

Windows 11 x64 と **.NET 10 Windows Desktop Runtime（x64）** が必要です。Microsoftの公式ページで「.NET Desktop Runtime」のWindows x64を選んでください。 [Microsoft公式の .NET 10 ダウンロード](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

配布物はFramework-dependentのポータブルZIPです。.NETランタイムは同梱されません。書き込み可能なフォルダにZIPを展開し、`HandpickerStudio.exe` を起動してください。アプリ本体は標準ユーザー権限で利用できます。

## 画像を閲覧する

1. **登録**からプロジェクト名とCheckpointフォルダーを指定します。既存の評価を表示する場合はHandpickerSuiteの `data/checkpoint_statuses.json` を指定します。
2. **参照ルート**で画像のOutputとArchiveを登録し、保存します。有効なルートのOutputに接続できると、指定したArchive配下にプロジェクト・ルートごとの保存先を作成します。ルートはカードから有効・無効を切り替えられます。
3. 評価別の一覧でCheckpointを選択します。画像は **一覧** と **タイル** を切り替えて表示できます。
4. 画像をクリックして個別ビューワを開きます。左右キー、またはホイールを上に回して前の画像、下に回して次の画像へ移動できます。所属を判定できない画像は **所属未確定** から確認できます。

## 画像を整理する

- 画像一覧の右クリックから、ごみ箱への移動、移動先プロジェクトの選択、代表サムネイルの設定ができます。`Ctrl` や `Shift` で複数選択できます。個別ビューワでは `Delete` キーで表示中の画像をごみ箱へ送れます。
- 所属未確定の画像は、移動先プロジェクト・参照ルート・Checkpointを指定してArchiveへ振り分けられます。代表サムネイルは選んだ元画像からJPEGを生成し、Checkpointと同じフォルダーに保存します。
- 評価の変更とCheckpoint廃棄を使うには、連携対応版のHandpickerSuiteを起動し、生成された評価JSONを指定します。Checkpointを評価グループへドラッグするか、一覧の右クリックから評価を変更できます。廃棄は `delete` 評価を付けてから右クリックで実行します。対象の画像生成を停止してから実行すると、画像をArchiveへ退避し、Checkpoint本体をごみ箱へ送ります。
- Suiteの連携対応版へ切り替える際は、旧版のSuiteと古い削除スクリプトを終了し、連携対応版が出力するスクリプトを使ってください。

## 配布ZIPのビルド

.NET 10 SDKを導入したWindows環境で、リポジトリのルートからPowerShellで実行します。

```powershell
.\build.ps1
```

`artifacts/HandpickerStudio-<version>-win-x64.zip` が生成されます。ビルドは実行ごとの作業フォルダで行い、検証済みのZIPを成果物ディレクトリに配置します。
