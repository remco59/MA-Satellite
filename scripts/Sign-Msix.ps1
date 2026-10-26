<#
.SYNOPSIS
  Signs an MSIX with the dev certificate using signtool (more reliable than letting msbuild import the pfx).
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Package,
  [Parameter(Mandatory)][string]$CertificatePfx,
  [Parameter(Mandatory)][string]$CertificatePassword
)

$ErrorActionPreference = 'Stop'
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
  Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe not found; install the Windows SDK.' }

& $signtool.FullName sign /fd SHA256 /f $CertificatePfx /p $CertificatePassword $Package
if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
Write-Host "Signed $Package"
