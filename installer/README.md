# TaskbarResourceMonitor Installer (MSI)

This folder contains a WiX v4 installer that packages the published app into an MSI.

## Prereqs
- Windows
- .NET SDK (already installed on this machine)
- WiX Toolset v4 (`wix` CLI)

## Build MSI

From repo root:

```powershell
pwsh -File .\installer\build-installer.ps1
```

Outputs:
- `installer\out\TaskbarResourceMonitor.msi`

