# YANMT installer (MSI)

WiX v7 project for the YANMT MSI (issue #40). Windows only, and not part of `Yanmt.slnx`.
User-facing install docs are in `docs/DEPLOYMENT.md`.

```powershell
dotnet publish src/Yanmt.Web -c Release -r win-x64 -p:Version=1.2.3 -o publish
dotnet build installer/Yanmt.Installer.wixproj -c Release -p:ProductVersion=1.2.3 -p:YanmtPublishDir=$PWD\publish
# -> installer\bin\x64\Release\yanmt-1.2.3.msi
```

| File | What it does |
| --- | --- |
| `Package.wxs` | Files, service (`NT SERVICE\YANMT`), registry settings, firewall rule, event source, upgrade rules, sequencing. |
| `Ui.wxs` | Dialog flow and the one settings dialog. |
| `CustomActions/` | C# (.NET Framework 4.8) actions: check settings, back up the database before an upgrade, replace a manual install's service, folder ACLs, certificate key grant, local groups, start the service. |
| `tests/Smoke.ps1` | CI smoke test: manual install, MSI install over it, upgrade, downgrade refused, uninstall. Only for a throwaway machine. |

WiX v7 needs the OSMF EULA accepted (`installer/Directory.Build.props`). The owner accepted it on
2026-11-11 because YANMT earns no revenue. The MSI is unsigned for now.
