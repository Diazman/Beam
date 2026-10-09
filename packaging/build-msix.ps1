<#
.SYNOPSIS
  Builds the Microsoft Store package(s) for Beam.

.DESCRIPTION
  Publishes Beam for each architecture, fills in packaging/msix/AppxManifest.xml from identity.json,
  indexes the logo assets (makepri), packs an .msix per architecture (makeappx) and bundles them into
  one .msixbundle — the file to upload to Partner Center (the Store signs it).

  With -CertificatePath the packages are also signed so they can be installed locally for testing
  (the certificate subject must equal the Publisher in identity.json).

.EXAMPLE
  pwsh packaging/build-msix.ps1                       # x64 + arm64 bundle
  pwsh packaging/build-msix.ps1 -Architectures x64
  pwsh packaging/build-msix.ps1 -CertificatePath test.pfx -CertificatePassword pass
#>
param(
    [ValidateSet("x64", "arm64")]
    [string[]]$Architectures = @("x64", "arm64"),
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

# ---- Windows SDK tools (installed Windows SDK, or downloaded from NuGet) ----
function Find-SdkTools {
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $kits) {
        $dir = Get-ChildItem $kits -Directory | Where-Object { Test-Path "$($_.FullName)\x64\makeappx.exe" } |
            Sort-Object Name -Descending | Select-Object -First 1
        if ($dir) { return "$($dir.FullName)\x64" }
    }
    $cache = Join-Path $root "artifacts/tools/sdk-buildtools"
    $existing = Get-ChildItem $cache -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue | Where-Object { $_.Directory.Name -eq "x64" } | Select-Object -First 1
    if ($existing) { return $existing.Directory.FullName }
    Write-Host "Downloading Windows SDK build tools from NuGet..."
    $version = "10.0.28000.2705"
    New-Item -ItemType Directory -Force $cache | Out-Null
    $zip = Join-Path $cache "buildtools.zip"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$version/microsoft.windows.sdk.buildtools.$version.nupkg" -OutFile $zip
    Expand-Archive $zip -DestinationPath $cache -Force
    return (Get-ChildItem $cache -Recurse -Filter makeappx.exe | Where-Object { $_.Directory.Name -eq "x64" } | Select-Object -First 1).Directory.FullName
}

function Invoke-Tool([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$(Split-Path $exe -Leaf) failed with exit code $LASTEXITCODE" }
}

if (-not $IsWindows) { throw "MSIX packaging needs Windows (makeappx/makepri). The GitHub release workflow runs it on a Windows runner." }

$tools = Find-SdkTools
$makeappx = Join-Path $tools "makeappx.exe"
$makepri = Join-Path $tools "makepri.exe"
$signtool = Join-Path $tools "signtool.exe"

# ---- Identity and version ----
$identity = Get-Content packaging/msix/identity.json -Raw | ConvertFrom-Json
$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
$parts = $version.Split('.')
while ($parts.Count -lt 3) { $parts += "0" }
$packageVersion = "$($parts[0]).$($parts[1]).$($parts[2]).0"   # the Store requires the 4th part to be 0
Write-Host "Packaging $($identity.displayName) $packageVersion as $($identity.identityName) / $($identity.publisher)"

$outDir = Join-Path $root "artifacts/msix"
New-Item -ItemType Directory -Force $outDir | Out-Null
$bundleInput = Join-Path $outDir "bundle-input"
if (Test-Path $bundleInput) { Remove-Item $bundleInput -Recurse -Force }
New-Item -ItemType Directory -Force $bundleInput | Out-Null

foreach ($arch in $Architectures) {
    $layout = Join-Path $root "artifacts/msix-layout/win-$arch"
    if (-not $SkipPublish) {
        Step "Publishing win-$arch"
        if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
        # Start from clean intermediate files: the release builds the single-file installer exe first in the same
        # folders, and reusing its outputs produced Store packages that crashed on start (1.2.0-1.2.2).
        foreach ($dir in @("src/Beam.App/obj/Release/net8.0-windows10.0.19041.0/win-$arch", "src/Beam.App/bin/Release/net8.0-windows10.0.19041.0/win-$arch")) {
            if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
        }
        dotnet publish src/Beam.App -f net8.0-windows10.0.19041.0 -p:PublishProfile=msix-$arch
        if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
    }

    Step "Building the Windows 11 context-menu handler ($arch)"
    # native/BeamContextMenu: "Send with Beam" at the top of the Windows 11 right-click menu (needs MSVC + CMake).
    $nativeBuild = Join-Path $root "artifacts/native/$arch"
    $cmakeArch = if ($arch -eq "arm64") { "ARM64" } else { "x64" }
    cmake -S native/BeamContextMenu -B $nativeBuild -A $cmakeArch
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed" }
    cmake --build $nativeBuild --config Release
    if ($LASTEXITCODE -ne 0) { throw "Context-menu handler build failed" }
    Copy-Item (Join-Path $nativeBuild "Release/BeamContextMenu.dll") $layout -Force

    Step "Preparing package layout ($arch)"
    Copy-Item packaging/msix/Assets -Destination $layout -Recurse -Force
    $manifest = (Get-Content packaging/msix/AppxManifest.xml -Raw).
        Replace("{{IdentityName}}", $identity.identityName).
        Replace("{{Publisher}}", [System.Security.SecurityElement]::Escape($identity.publisher)).
        Replace("{{PublisherDisplayName}}", [System.Security.SecurityElement]::Escape($identity.publisherDisplayName)).
        Replace("{{DisplayName}}", [System.Security.SecurityElement]::Escape($identity.displayName)).
        Replace("{{Version}}", $packageVersion).
        Replace("{{Architecture}}", $arch)
    Set-Content (Join-Path $layout "AppxManifest.xml") $manifest -Encoding utf8

    Step "Indexing resources ($arch)"
    # Index only the logos (not the app's DLLs) so Windows picks the right size for each scale/theme.
    $priRoot = Join-Path $outDir "pri-$arch"
    if (Test-Path $priRoot) { Remove-Item $priRoot -Recurse -Force }
    New-Item -ItemType Directory -Force $priRoot | Out-Null
    Copy-Item packaging/msix/Assets -Destination $priRoot -Recurse
    Copy-Item (Join-Path $layout "AppxManifest.xml") $priRoot
    Invoke-Tool $makepri @("new", "/pr", $priRoot, "/cf", (Resolve-Path packaging/msix/priconfig.xml).Path, "/mn", (Join-Path $priRoot "AppxManifest.xml"), "/of", (Join-Path $layout "resources.pri"), "/o")
    Remove-Item $priRoot -Recurse -Force

    Step "Packing ($arch)"
    $msix = Join-Path $bundleInput "Beam_${packageVersion}_$arch.msix"
    Invoke-Tool $makeappx @("pack", "/d", $layout, "/p", $msix, "/o")
    if ($CertificatePath) {
        Invoke-Tool $signtool @("sign", "/fd", "SHA256", "/f", $CertificatePath, "/p", $CertificatePassword, $msix)
    }
    Copy-Item $msix $outDir -Force
}

Step "Bundling"
$bundle = Join-Path $outDir "Beam_$packageVersion.msixbundle"
Invoke-Tool $makeappx @("bundle", "/d", $bundleInput, "/p", $bundle, "/bv", $packageVersion, "/o")
if ($CertificatePath) {
    Invoke-Tool $signtool @("sign", "/fd", "SHA256", "/f", $CertificatePath, "/p", $CertificatePassword, $bundle)
}
Remove-Item $bundleInput -Recurse -Force

Get-ChildItem $outDir -Filter "*.msix*" | ForEach-Object { Write-Host ("Built {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) }
Write-Host "`nUpload $bundle to Partner Center (Packages section)."
