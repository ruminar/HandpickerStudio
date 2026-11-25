# HandpickerStudio

複数のComfyUI環境で生成した画像を、Checkpointごとに閲覧・整理するWindowsアプリです。

## 起動

Windows 11 x64 と **.NET 10 Windows Desktop Runtime（x64）** が必要です。Microsoftの公式ページで「.NET Desktop Runtime」のWindows x64を選んでください。 [Microsoft公式の .NET 10 ダウンロード](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

配布物はFramework-dependentのポータブルZIPです。.NETランタイムは同梱されません。書き込み可能なフォルダにZIPを展開し、`HandpickerStudio.exe` を起動してください。アプリ本体は標準ユーザー権限で利用できます。

## 画像を閲覧する

1. **登録**からプロジェクト名とComfyUIフォルダーを指定します。ポータブル版の `ComfyUI_windows_portable` と、その中の `ComfyUI` 本体のどちらも選べます。標準フォルダーと `extra_model_paths.yaml` のCheckpoint参照先を確認し、検出したHandpickerSuiteの評価JSONと連携情報を表示します。手動で登録する場合は、**手動設定・キャッシュ保存先** を開きます。
2. **参照ルート**で画像のOutputとArchiveを登録し、保存します。有効なルートのOutputに接続できると、指定したArchive配下にプロジェクト・ルートごとの保存先を作成します。ルートはカードから有効・無効を切り替えられます。
3. 評価別の一覧でCheckpointを選択します。画像は **一覧**、**タイル**、画像だけの **タイル(簡易)** で表示できます。タイルでは `Ctrl`＋ホイールで画像の大きさを変えられます。
4. 画像をダブルクリックして個別ビューワを開きます。左右キー、またはホイールを上に回して前の画像、下に回して次の画像へ移動できます。所属を判定できない画像は **所属未確定** から確認できます。

Checkpointの評価別ペインは、見出しをクリックすると開閉できます。ペインごとにスクロールでき、展開したペイン間の境界をドラッグすると高さを調整できます。

**表示設定** の配色は **ライト**、**ダーク**、**システムに合わせる** から選べます。**画像キャッシュを作る** の初期値はオフです。オンにすると一覧用の縮小画像を保存します。オフに戻すと、このアプリの設定で作成した画像キャッシュを削除します。

## 画像を整理する

- 画像一覧の右クリックから、ごみ箱への移動、移動先プロジェクトの選択、代表サムネイルの設定ができます。`Ctrl` や `Shift` で複数選択できます。個別ビューワでは `Delete` キーで表示中の画像をごみ箱へ送れます。
- 所属未確定の画像は、移動先プロジェクト・参照ルート・Checkpointを指定してArchiveへ振り分けられます。代表サムネイルは選んだ元画像からJPEGを生成し、Checkpointと同じフォルダーに保存します。
- 評価の変更とCheckpoint廃棄を使うには、連携対応版のHandpickerSuiteを起動し、生成された評価JSONを指定します。Checkpointを目的の評価の見出しや一覧へドラッグするか、一覧の右クリックから評価を変更できます。折りたたんだ評価も見出しで受け付けます。廃棄は `delete` 評価を付けてから右クリックで実行します。対象の画像生成を停止してから実行すると、画像をArchiveへ退避し、Checkpoint本体をごみ箱へ送ります。
- Suiteの連携対応版へ切り替える際は、旧版のSuiteと古い削除スクリプトを終了し、連携対応版が出力するスクリプトを使ってください。

## 評価を別のPCへ移す

**評価の移行**から、選択中のプロジェクトの評価を `Anima_20261125_153000.json` のようなファイル名で書き出せます。移行先のStudioでは対象プロジェクトを選び、同じメニューのインポートからファイルを開きます。ファイル名とバイト数が一意に一致するCheckpointに適用し、既存評価を変更する場合は件数を確認します。Suiteでは **Checkpoint Tag Export / Import** の **Import File…** で同じJSONのファイル名を指定します。

## 配布ZIPのビルド

アプリアイコンを組み込む場合は、ビルド前に `HandpickerStudio/HandpickerStudio/Assets/HandpickerStudio.ico` にICOファイルを置きます。

.NET 10 SDKを導入したWindows環境で、リポジトリのルートからPowerShellで実行します。

```powershell
.\build.ps1
```

`artifacts/HandpickerStudio-<version>-win-x64.zip` が生成されます。ビルドは実行ごとの作業フォルダで行い、検証済みのZIPを成果物ディレクトリに配置します。
