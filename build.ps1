[CmdletBinding()]
param(
    [ValidateSet('Local', 'Release', 'Test')]
    [string]$Distribution = 'Local',
    [string[]]$SigningCertificate,
    [string[]]$TrustedCertificate,
    [string]$TestRepository,
    [string]$TestVersion,
    [string]$TestPfx,
    [securestring]$TestPfxPassword
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'HandpickerStudio\HandpickerStudio\HandpickerStudio.csproj'
$launcher = Join-Path $PSScriptRoot 'HandpickerStudio\HandpickerStudio.Launcher\HandpickerStudio.Launcher.csproj'
$runtime = 'win-x64'
$version = dotnet msbuild $project -nologo '-getProperty:Version' '-p:Configuration=Release' "-p:RuntimeIdentifier=$runtime"
if ($LASTEXITCODE -ne 0 -or $version -isnot [string]) { throw 'Could not read the application version.' }

$repository = 'ruminar/HandpickerStudio'
$assetPrefix = 'HandpickerStudio'
if ($Distribution -eq 'Test') {
    if ($TestRepository -notmatch '\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z' -or $TestRepository -eq $repository) {
        throw 'Specify a separate GitHub repository for update testing.'
    }
    $repository = $TestRepository
    $assetPrefix = 'HandpickerStudio-UpdateTest'
    if ($TestVersion) { $version = $TestVersion }
}
elseif ($TestRepository -or $TestVersion -or $TestPfx -or $TestPfxPassword) {
    throw 'Test settings require -Distribution Test.'
}
if ($version -notmatch '\A(?:0|[1-9][0-9]{0,8})\.(?:0|[1-9][0-9]{0,8})\.(?:0|[1-9][0-9]{0,8})\z') {
    throw 'A stable three-part application version is required.'
}
if ($Distribution -ne 'Local' -and $PSVersionTable.PSVersion -lt [version]'7.6') {
    throw 'Run signed packaging with PowerShell 7.6 or newer.'
}

$artifactsPath = Join-Path $PSScriptRoot 'artifacts'
if (-not (Test-Path -LiteralPath $artifactsPath)) {
    New-Item -Path $artifactsPath -ItemType Directory -Force | Out-Null
}
$artifacts = Get-Item -LiteralPath $artifactsPath -Force
if (-not $artifacts.PSIsContainer -or ($artifacts.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The artifacts path must be a regular directory in the repository.'
}
$staging = New-Item -Path (Join-Path $artifacts.FullName ('.build-' + [guid]::NewGuid().ToString('N'))) -ItemType Directory
$signing = $null

try {
    $publishPath = Join-Path $staging.FullName 'publish'
    $appPublishPath = Join-Path $publishPath "app\versions\$version"
    $launcherPublishPath = Join-Path $staging.FullName 'launcher'
    $msbuildPath = Join-Path $staging.FullName 'msbuild'
    $stagedZip = Join-Path $staging.FullName 'package.zip'
    $zipName = "$assetPrefix-$version-$runtime.zip"
    $packagePath = Join-Path $artifacts.FullName $zipName
    $publishOptions = @('-c', 'Release', '-r', $runtime, '--self-contained', 'false', '--artifacts-path', $msbuildPath,
        "-p:Version=$version", '-p:DebugType=None', '-p:DebugSymbols=false')
    if ($Distribution -ne 'Local') {
        . (Join-Path $PSScriptRoot 'build-signing.ps1')
        $signingOptions = @{ Signers = $SigningCertificate; AdditionalKeys = $TrustedCertificate; Pfx = $TestPfx
            PfxPassword = $TestPfxPassword; Workspace = $PSScriptRoot }
        $signing = Get-UpdateSigning @signingOptions
        $profile = [ordered]@{
            repository = $repository
            assetPrefix = $assetPrefix
            test = $Distribution -eq 'Test'
            publicKeys = $signing.PublicKeys
        }
        $profilePath = Join-Path $staging.FullName 'update-profile.json'
        Write-UpdateJson $profilePath $profile
        $publishOptions += "-p:UpdateProfileFile=$profilePath"
    }

    dotnet publish $project @publishOptions -o $appPublishPath
    if ($LASTEXITCODE -ne 0) { throw "Application publish failed with exit code $LASTEXITCODE." }
    dotnet publish $launcher @publishOptions -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -o $launcherPublishPath
    if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed with exit code $LASTEXITCODE." }

    $launcherFiles = @(Get-ChildItem -LiteralPath $launcherPublishPath -Force)
    if ($launcherFiles.Count -ne 1 -or $launcherFiles[0].Name -ne 'HandpickerStudio.exe') {
        throw 'The launcher must be a single framework-dependent executable.'
    }
    Copy-Item -LiteralPath $launcherFiles[0].FullName -Destination $publishPath
    foreach ($name in 'README.md', 'LICENSE', 'ThirdPartyNotices.txt') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $publishPath
    }

    $runtimeOptions = (Get-Content -LiteralPath (Join-Path $appPublishPath 'HandpickerStudio.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions
    $desktopRuntime = $runtimeOptions.frameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' -and $_.version -like '10.*' }
    if (-not $desktopRuntime -or $runtimeOptions.includedFrameworks) {
        throw 'The published app must depend on .NET 10 Windows Desktop Runtime.'
    }
    $inventory = @(Get-ChildItem -LiteralPath $appPublishPath -File -Recurse | ForEach-Object {
        $_.FullName.Substring($appPublishPath.Length + 1).Replace('\', '/')
    })
    [IO.File]::WriteAllText((Join-Path $appPublishPath '.package-files.json'),
        (ConvertTo-Json -InputObject $inventory -Compress), [Text.UTF8Encoding]::new($false))

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($publishPath, $stagedZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $archive = [IO.Compression.ZipFile]::OpenRead($stagedZip)
    try {
        $requiredAppFiles = 'HandpickerStudio.exe', 'HandpickerStudio.dll', 'HandpickerStudio.deps.json',
            'HandpickerStudio.runtimeconfig.json', 'HandpickerStudio.Update.dll', 'YamlDotNet.dll', '.package-files.json'
        $requiredEntries = @('HandpickerStudio.exe', 'README.md', 'LICENSE', 'ThirdPartyNotices.txt') +
            @($requiredAppFiles | ForEach-Object { "app/versions/$version/$_" })
        foreach ($entry in $requiredEntries) {
            if ($null -eq $archive.GetEntry($entry)) { throw "The package is missing $entry." }
        }
        $runtimeBinaries = 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll'
        if ($archive.Entries.Name | Where-Object { $_ -in $runtimeBinaries }) {
            throw 'The package contains bundled .NET runtime binaries.'
        }
    }
    finally { $archive.Dispose() }

    if ($Distribution -ne 'Local') {
        $releaseParent = Join-Path $artifacts.FullName $Distribution.ToLowerInvariant()
        $releaseFolder = Join-Path $releaseParent "v$version"
        New-Item -ItemType Directory -Path $releaseParent -Force | Out-Null
        if ((Get-Item -LiteralPath $releaseParent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'The release output parent cannot be a link.'
        }
        $releaseStaging = (New-Item -ItemType Directory -Path (Join-Path $staging.FullName 'release')).FullName
        $releaseZip = Join-Path $releaseStaging $zipName
        [IO.File]::Move($stagedZip, $releaseZip)
        $releaseOptions = @{ Destination = $releaseStaging; Zip = $releaseZip; Version = $version
            ProfilePath = $profilePath; Signing = $signing; VerificationAssembly = (Join-Path $appPublishPath 'HandpickerStudio.Update.dll') }
        Write-SignedUpdate @releaseOptions
        [IO.Directory]::Move($releaseStaging, $releaseFolder)
        $packagePath = Join-Path $releaseFolder $zipName
    }
    elseif (Test-Path -LiteralPath $packagePath) {
        $existingPackage = Get-Item -LiteralPath $packagePath -Force
        if ($existingPackage.PSIsContainer -or ($existingPackage.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'The package destination must be a regular file.'
        }
        [IO.File]::Replace($stagedZip, $packagePath, [NullString]::Value)
    }
    else { [IO.File]::Move($stagedZip, $packagePath) }

    Write-Output "Package validated: $packagePath"
}
finally {
    if ($signing) {
        $signing.Keys | ForEach-Object { $_.Rsa.Dispose() }
        $signing.Certificates | ForEach-Object { $_.Dispose() }
    }
    $cleanup = Get-Item -LiteralPath $staging.FullName -Force -ErrorAction SilentlyContinue
    if ($cleanup -and $cleanup.Parent.FullName -eq $artifacts.FullName -and
        $cleanup.FullName -eq $staging.FullName -and -not ($cleanup.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $cleanup.FullName -Recurse -Force -ErrorAction Continue
    }
}
