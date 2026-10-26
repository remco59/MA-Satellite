<#
.SYNOPSIS
  Publishes the unpackaged, self-contained app and wraps it in an Inno Setup .exe installer.
.DESCRIPTION
  Needs the .NET 8 SDK, the Windows application development workload (WinUI) and Inno Setup 6
  (https://jrsoftware.org/isdl.php, or `choco install innosetup`). Output: artifacts\dist\ma-satellite-setup-<version>.exe
#>
[CmdletBinding()]
param(
  [string]$Configuration = 'Release',
  [string]$Version,
  [string]$Iscc
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$publish = Join-Path $root 'artifacts\publish\win-x64'

if (-not $Version) {
  $props = [xml](Get-Content (Join-Path $root 'Directory.Build.props'))
  $Version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}

if (-not $Iscc) {
  $candidates = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
  )
  $Iscc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $Iscc) { throw 'Inno Setup 6 (ISCC.exe) not found. Install it or pass -Iscc.' }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

# Separate intermediate/output folders so this build cannot mix with the MSIX build's obj\bin.
dotnet publish (Join-Path $root 'src\MaSatellite.App\MaSatellite.App.csproj') `
  -c $Configuration -r win-x64 --self-contained `
  -p:Platform=x64 -p:Unpackaged=true `
  '-p:IntermediateOutputPath=obj\unpackaged\' '-p:OutDir=bin\unpackaged\' `
  -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
if (-not (Test-Path (Join-Path $publish 'MaSatellite.App.exe'))) { throw 'MaSatellite.App.exe missing from the publish output.' }

& $Iscc "/DAppVersion=$Version" "/DSourceDir=$publish" (Join-Path $root 'installer\MaSatellite.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

Get-ChildItem (Join-Path $root 'artifacts\dist') -Filter 'ma-satellite-setup-*.exe' | ForEach-Object { Write-Host "Installer: $($_.FullName)" }
