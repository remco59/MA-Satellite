<#
.SYNOPSIS
  Builds and signs the MSIX locally (needs Visual Studio Build Tools with the MSIX packaging components).
#>
[CmdletBinding()]
param(
  [ValidateSet('x64', 'ARM64')][string]$Platform = 'x64',
  [string]$Configuration = 'Release',
  [Parameter(Mandatory)][string]$CertificatePfx,
  [Parameter(Mandatory)][securestring]$CertificatePassword
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$out = Join-Path $root 'artifacts\msix\'
$plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($CertificatePassword))

msbuild (Join-Path $root 'src\MaSatellite.App\MaSatellite.App.csproj') /restore `
  /p:Configuration=$Configuration /p:Platform=$Platform `
  /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly /p:GenerateAppxPackageOnBuild=true `
  /p:AppxPackageDir=$out `
  /p:AppxPackageSigningEnabled=true /p:PackageCertificateKeyFile=$CertificatePfx /p:PackageCertificatePassword=$plain
if ($LASTEXITCODE -ne 0) { throw "msbuild failed ($LASTEXITCODE)" }
Write-Host "MSIX written below $out"
