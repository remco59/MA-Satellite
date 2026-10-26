<#
.SYNOPSIS
  Creates the self-signed code-signing certificate used for local MSIX installs.
.DESCRIPTION
  The certificate subject must equal the Publisher in src/MaSatellite.App/Package.appxmanifest.
  Outputs a .pfx (private key, keep it local) and a .cer (public, import it on machines that install the MSIX).
#>
[CmdletBinding()]
param(
  [string]$Subject = 'CN=MaSatellite Dev',
  [string]$OutDir = (Join-Path $PSScriptRoot '..\artifacts\cert'),
  [Parameter(Mandatory)][securestring]$PfxPassword
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$cert = New-SelfSignedCertificate `
  -Type Custom `
  -Subject $Subject `
  -KeyUsage DigitalSignature `
  -FriendlyName 'ma-satellite dev signing' `
  -CertStoreLocation 'Cert:\CurrentUser\My' `
  -NotAfter (Get-Date).AddYears(3) `
  -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

$pfx = Join-Path $OutDir 'MaSatellite.Dev.pfx'
$cer = Join-Path $OutDir 'MaSatellite.Dev.cer'
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $PfxPassword | Out-Null
Export-Certificate -Cert $cert -FilePath $cer | Out-Null

# Remove the key from the personal store again; the pfx file is the source of truth.
Remove-Item -Path ("Cert:\CurrentUser\My\" + $cert.Thumbprint) -DeleteKey

Write-Host "Created $pfx and $cer"
