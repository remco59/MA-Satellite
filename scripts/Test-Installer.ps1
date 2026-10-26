<#
.SYNOPSIS
  Silent install -> start -> check -> uninstall of the .exe installer. Used by CI; also handy on a clean VM.
.DESCRIPTION
  Uses the current user's profile (settings, logs, Run key) and removes what it created. Do not run it on a machine
  where ma-satellite is already set up.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Installer,
  [int]$StartupSeconds = 15
)

$ErrorActionPreference = 'Stop'
$dir = Join-Path ($(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP })) 'ma-satellite-test'
$data = Join-Path $env:LOCALAPPDATA 'MaSatellite'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Fail($message) { Write-Host "::error::$message"; throw $message }

Write-Host "Installing to $dir"
$p = Start-Process -FilePath $Installer -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /DIR=`"$dir`" /LOG=`"$dir.install.log`"" -Wait -PassThru
if ($p.ExitCode -ne 0) { Fail "Installer exited with $($p.ExitCode)" }
$exe = Join-Path $dir 'MaSatellite.App.exe'
if (-not (Test-Path $exe)) { Fail 'MaSatellite.App.exe was not installed.' }
if (-not (Test-Path (Join-Path $dir 'unins000.exe'))) { Fail 'Uninstaller was not installed.' }

Write-Host 'Starting the installed app'
$app = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds $StartupSeconds
if ($app.HasExited) {
  $logs = Get-ChildItem (Join-Path $data 'logs') -Filter '*.log' -ErrorAction SilentlyContinue | Get-Content -ErrorAction SilentlyContinue
  Write-Host ($logs -join "`n")
  Fail "The app exited early with code $($app.ExitCode)."
}

$log = Get-ChildItem (Join-Path $data 'logs') -Filter 'masatellite-*.log' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $log) { Fail 'No log file was written.' }
Get-Content $log.FullName -Tail 20 | ForEach-Object { Write-Host "  log: $_" }
if (-not (Select-String -Path $log.FullName -Pattern 'ma-satellite .* starting' -Quiet)) { Fail 'Startup line missing from the log.' }

$value = (Get-ItemProperty -Path $runKey -Name 'MaSatellite' -ErrorAction SilentlyContinue).MaSatellite
if ($value -ne "`"$exe`"") { Fail "Autostart Run entry is '$value', expected the quoted exe path." }

Write-Host 'Stopping the app and uninstalling'
Stop-Process -Id $app.Id -Force
$u = Start-Process -FilePath (Join-Path $dir 'unins000.exe') -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
if ($u.ExitCode -ne 0) { Fail "Uninstaller exited with $($u.ExitCode)" }
Start-Sleep -Seconds 2
if (Test-Path $exe) { Fail 'MaSatellite.App.exe is still there after uninstall.' }
if ((Get-ItemProperty -Path $runKey -Name 'MaSatellite' -ErrorAction SilentlyContinue).MaSatellite) { Fail 'Autostart Run entry survived the uninstall.' }

Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Installer test passed.'
