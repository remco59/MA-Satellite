<#
.SYNOPSIS
  Trusts the dev certificate and installs (or updates) the ma-satellite MSIX for the current user.
.EXAMPLE
  .\scripts\Install-Dev.ps1 -Package .\artifacts\msix\MaSatellite.App_0.1.0.0_x64.msix -Certificate .\artifacts\cert\MaSatellite.Dev.cer
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Package,
  [Parameter(Mandatory)][string]$Certificate
)

$ErrorActionPreference = 'Stop'

# Trusting a certificate for MSIX needs an elevated prompt once.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw 'Run this script from an elevated PowerShell once so the certificate can be trusted (Cert:\LocalMachine\TrustedPeople).'
}

Import-Certificate -FilePath $Certificate -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
Add-AppxPackage -Path $Package -ForceUpdateFromAnyVersion
Write-Host 'Installed. Look for the ma-satellite icon in the notification area.'
