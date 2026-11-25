$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'HandpickerStudio\HandpickerStudio\HandpickerStudio.csproj'
$configuration = 'Release'
$runtime = 'win-x64'

$version = dotnet msbuild $projectPath -nologo '-getProperty:Version' "-p:Configuration=$configuration" "-p:RuntimeIdentifier=$runtime"
if ($LASTEXITCODE -ne 0 -or $version -isnot [string] -or $version -notmatch '\A\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\z') {
    throw 'Could not read a valid version from the project.'
}

$artifactsPath = Join-Path $PSScriptRoot 'artifacts'
if (-not (Test-Path -LiteralPath $artifactsPath)) {
    New-Item -Path $artifactsPath -ItemType Directory -Force | Out-Null
}
$artifacts = Get-Item -LiteralPath $artifactsPath -Force
if (-not $artifacts.PSIsContainer -or ($artifacts.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The artifacts path must be a regular directory in the repository.'
}

$stagingPath = Join-Path $artifacts.FullName ('.build-' + [guid]::NewGuid().ToString('N'))
$staging = New-Item -Path $stagingPath -ItemType Directory

try {
    $publishPath = Join-Path $staging.FullName 'publish'
    $msbuildPath = Join-Path $staging.FullName 'msbuild'
    $stagedZip = Join-Path $staging.FullName 'package.zip'
    $packagePath = Join-Path $artifacts.FullName "HandpickerStudio-$version-$runtime.zip"

    dotnet publish $projectPath `
        -c $configuration `
        -r $runtime `
        --self-contained false `
        --artifacts-path $msbuildPath `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $publishPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $publishPath
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $publishPath
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ThirdPartyNotices.txt') -Destination $publishPath

    $runtimeConfigPath = Join-Path $publishPath 'HandpickerStudio.runtimeconfig.json'
    $runtimeOptions = (Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json).runtimeOptions
    $desktopRuntime = $runtimeOptions.frameworks | Where-Object {
        $_.name -eq 'Microsoft.WindowsDesktop.App' -and $_.version -like '10.*'
    }
    if (-not $desktopRuntime -or $runtimeOptions.includedFrameworks) {
        throw 'The published app must depend on .NET 10 Windows Desktop Runtime.'
    }

    Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath $stagedZip -CompressionLevel Optimal
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($stagedZip)
    try {
        $requiredEntries = 'HandpickerStudio.exe', 'HandpickerStudio.dll', 'HandpickerStudio.deps.json',
            'HandpickerStudio.runtimeconfig.json', 'YamlDotNet.dll', 'README.md', 'LICENSE', 'ThirdPartyNotices.txt'
        foreach ($entry in $requiredEntries) {
            if ($null -eq $archive.GetEntry($entry)) {
                throw "The package is missing $entry."
            }
        }

        $runtimeBinaries = 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll'
        if ($archive.Entries.Name | Where-Object { $_ -in $runtimeBinaries }) {
            throw 'The package contains bundled .NET runtime binaries.'
        }
    }
    finally {
        $archive.Dispose()
    }

    if (Test-Path -LiteralPath $packagePath) {
        $existingPackage = Get-Item -LiteralPath $packagePath -Force
        if ($existingPackage.PSIsContainer -or ($existingPackage.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'The package destination must be a regular file.'
        }
        [IO.File]::Replace($stagedZip, $packagePath, [NullString]::Value)
    }
    else {
        [IO.File]::Move($stagedZip, $packagePath)
    }

    Write-Output "Package validated: $packagePath"
}
finally {
    $cleanup = Get-Item -LiteralPath $staging.FullName -Force -ErrorAction SilentlyContinue
    if ($cleanup -and $cleanup.Parent.FullName -eq $artifacts.FullName -and
        $cleanup.FullName -eq $staging.FullName -and -not ($cleanup.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $cleanup.FullName -Recurse -Force -ErrorAction Continue
    }
}
