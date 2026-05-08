# Runs the MSI interactively after clearing MOTW (blocked download) bit.
$ErrorActionPreference = "Stop"
$msi = Join-Path $PSScriptRoot "out\TaskbarResourceMonitor.msi"
if (-not (Test-Path $msi)) {
  Write-Error "MSI not found. Run .\build-installer.ps1 first. Expected: $msi"
}

Unblock-File -LiteralPath $msi -ErrorAction SilentlyContinue

# Full UI (/i with no switches) — per-user package should not prompt for admin.
$p = Start-Process -FilePath "msiexec.exe" -ArgumentList @("/i", "`"$msi`"") -PassThru -Wait
if ($p.ExitCode -ne 0) {
  Write-Error "msiexec exited $($p.ExitCode). Log with: msiexec /i `"$msi`" /L*V `%TEMP%\trm-msi.log"
}
