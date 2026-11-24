# HandpickerStudio

複数のComfyUI環境で生成した画像を、Checkpointごとに閲覧・整理するWindowsアプリです。

## 起動

Windows 11 x64 と **.NET 10 Windows Desktop Runtime（x64）** が必要です。Microsoftの公式ページで「.NET Desktop Runtime」のWindows x64を選んでください。 [Microsoft公式の .NET 10 ダウンロード](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

配布物はFramework-dependentのポータブルZIPです。.NETランタイムは同梱されません。書き込み可能なフォルダにZIPを展開し、`HandpickerStudio.exe` を起動してください。アプリ本体は標準ユーザー権限で利用できます。

## 配布ZIPのビルド

.NET 10 SDKを導入したWindows環境で、リポジトリのルートからPowerShellで実行します。

```powershell
.\build.ps1
```

`artifacts/HandpickerStudio-<version>-win-x64.zip` が生成されます。ビルドは実行ごとの作業フォルダで行い、検証済みのZIPを成果物ディレクトリに配置します。
