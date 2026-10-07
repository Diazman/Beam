<#
.SYNOPSIS
  Builds, tests and packages Beam for Windows.

.EXAMPLE
  ./build.ps1                      # test + publish win-x64
  ./build.ps1 -Installer           # ...and build the setup .exe (needs Inno Setup 6)
  ./build.ps1 -Runtime win-arm64   # Windows on ARM
  ./build.ps1 -SkipTests
#>
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [switch]$SkipTests,
    [switch]$Installer
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

Step "Checking .NET SDK"
$sdk = $null
try { $sdk = (dotnet --version) 2>$null } catch { }
if (-not $sdk -or [int]($sdk.Split('.')[0]) -lt 8) {
    throw ".NET SDK 8 or newer is required: https://dotnet.microsoft.com/download"
}
Write-Host "Using .NET SDK $sdk"

if (-not $SkipTests) {
    Step "Running tests"
    dotnet test Beam.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

Step "Publishing $Runtime"
dotnet publish src/Beam.App -f net8.0-windows10.0.19041.0 -p:PublishProfile=$Runtime
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
$exe = "artifacts/publish/$Runtime/Beam.exe"
Write-Host ("Built {0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))

if ($Installer) {
    Step "Building installer"
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 not found. Install it from https://jrsoftware.org/isinfo.php (or: winget install JRSoftware.InnoSetup)" }
    $version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
    $arch = $Runtime.Substring(4)
    & $iscc "/DArch=$arch" "/DAppVersion=$version" installer/Beam.iss
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
    Get-ChildItem artifacts/installer | ForEach-Object { Write-Host "Built $($_.FullName)" }
}
