# YANMT installer (MSI)

WiX v7 project for the YANMT MSI (issue #40). Windows only, and not part of `Yanmt.slnx`.
User-facing install docs are in `docs/DEPLOYMENT.md`.

```powershell
dotnet publish src/Yanmt.Web -c Release -r win-x64 -p:Version=1.2.3 -o publish
dotnet build installer/Yanmt.Installer.wixproj -c Release -p:ProductVersion=1.2.3 -p:YanmtPublishDir=$PWD\publish
# -> installer\bin\x64\Release\yanmt-1.2.3.msi   (use -t:Rebuild when only the version changed)
```

| File | What it does |
| --- | --- |
| `Package.wxs` | Files, service (`NT SERVICE\YANMT`), registry settings, firewall rule, event source, upgrade rules, sequencing. |
| `Ui.wxs` | Dialog flow and the one settings dialog. |
| `CustomActions/` | C# (.NET Framework 4.8) actions: check settings, back up the database before an upgrade, replace a manual install's service, folder ACLs, certificate key grant, local groups, start the service. |
| `tests/Smoke.ps1` | CI smoke test: manual install, MSI install over it, upgrade, downgrade refused, uninstall. Only for a throwaway machine. |

## Releases

`.github/workflows/release.yml` runs when a GitHub Release is **published** (a pre-release counts too).
Create the release in the GitHub UI with a new tag `vX.Y.Z` (X and Y at most 255, Z at most 65535, no
suffix) on the commit to ship. The workflow builds and tests that tag, publishes with `-p:Version=X.Y.Z`,
builds `yanmt-X.Y.Z.msi` with the same version, and attaches it plus `SHA256SUMS.txt` to the release.
A bad tag fails the run and attaches nothing. Re-running a failed run replaces both files.

Check a download: `Get-FileHash yanmt-X.Y.Z.msi -Algorithm SHA256` must match the line in `SHA256SUMS.txt`.

WiX v7 needs the OSMF EULA accepted (`installer/Directory.Build.props`). The owner accepted it on
2026-11-11 because YANMT earns no revenue. The MSI is unsigned for now.
