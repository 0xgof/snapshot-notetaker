<#
.SYNOPSIS
    Builds the downloadable release: a self-contained, single-file SnapshotNotetaker.exe per runtime, zipped with
    the license and a short readme. Users need nothing installed (the .NET runtime is inside the exe).

.EXAMPLE
    ./scripts/publish.ps1                                  # win-x64, version from Directory.Build.props
    ./scripts/publish.ps1 -Runtime win-x64,win-arm64 -Version 0.3.0
    ./scripts/publish.ps1 -SentryDsn https://key@o0.ingest.sentry.io/1 -SupportUrl https://github.com/me/repo/issues
#>
param(
    [string]$Version = "",
    [string[]]$Runtime = @("win-x64"),
    [string]$SentryDsn = "",
    [string]$SupportEmail = "",
    [string]$SupportUrl = "",
    [string]$Output = "artifacts"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# Use dotnet from PATH, or the per-user install from dotnet-install.ps1.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (Test-Path $userDotnet) { $sdks = & $userDotnet --list-sdks; if ($sdks -match '^10\.') { $dotnet = $userDotnet } }
if (-not $dotnet) { throw "The .NET 10 SDK was not found. Install it from https://dot.net." }

if (-not $Version) {
    $Version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $SupportUrl -and $env:GITHUB_REPOSITORY) { $SupportUrl = "https://github.com/$($env:GITHUB_REPOSITORY)/issues" }

$out = Join-Path $root $Output
New-Item -ItemType Directory -Force $out | Out-Null

foreach ($rid in $Runtime) {
    $dir = Join-Path $out "publish\$rid"
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    Write-Host "Publishing $Version for $rid..." -ForegroundColor Cyan

    & $dotnet publish "$root\src\SnapshotNotetaker\SnapshotNotetaker.csproj" `
        -c Release -r $rid --self-contained true -o $dir `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishReadyToRun=true `
        -p:DebugType=embedded `
        -p:Version=$Version `
        -p:SentryDsn=$SentryDsn `
        -p:SupportEmail=$SupportEmail `
        -p:SupportUrl=$SupportUrl `
        -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

    Copy-Item "$root\LICENSE" "$dir\LICENSE.txt"
    @"
Snapshot Notetaker $Version ($rid)

Run SnapshotNotetaker.exe. Nothing to install; no .NET runtime needed.
Windows may show "Windows protected your PC" the first time because the app is not code-signed:
click "More info", then "Run anyway".

Your snapshots are stored in %LOCALAPPDATA%\SnapshotNotetaker\Snapshots (changeable in Settings).
Help & support: question-mark button in the toolbar.
"@ | Set-Content -Encoding utf8 "$dir\README.txt"

    $zip = Join-Path $out "SnapshotNotetaker-$Version-$rid.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$dir\*" -DestinationPath $zip -CompressionLevel Optimal
    $size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "  -> $zip ($size MB)" -ForegroundColor Green
}

Get-ChildItem $out -Filter *.zip | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content -Encoding ascii (Join-Path $out "SHA256SUMS.txt")
