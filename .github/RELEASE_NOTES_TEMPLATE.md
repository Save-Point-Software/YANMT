<!--
  Release notes template (issue #49). Copy into the GitHub release body, replace every X.Y.Z with the version,
  and fill in "What's new". The release workflow attaches yanmt-X.Y.Z.msi and SHA256SUMS.txt.
-->
## What's new

- ...

## Install or upgrade

Download **`yanmt-X.Y.Z.msi`** and **`SHA256SUMS.txt`** (Assets, below) into one folder. In an elevated
PowerShell prompt in that folder, this finds the MSI, checks its hash, unblocks it and runs the installer
with a verbose log:

```powershell
$msi = @(Get-ChildItem .\yanmt-*.msi)
if ($msi.Count -ne 1) { throw "Expected one yanmt-X.Y.Z.msi in $PWD, found $($msi.Count)." }
$msi = $msi[0]
$hash = (Get-FileHash $msi.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not (Select-String .\SHA256SUMS.txt -SimpleMatch "$hash  $($msi.Name)" -Quiet)) { throw "$($msi.Name) doesn't match SHA256SUMS.txt." }
Unblock-File $msi.FullName
$log = Join-Path $PWD 'yanmt-install.log'
$p = Start-Process msiexec.exe -ArgumentList "/i `"$($msi.FullName)`" /l*v `"$log`"" -Wait -PassThru
"msiexec exit code $($p.ExitCode) (0 = installed); log: $log"
```

The MSI is **not code-signed** yet. If you open it from Explorer, SmartScreen may show "Windows protected
your PC": click **More info > Run anyway**.

Silent installs, upgrades and every property: [DEPLOYMENT.md, section 1](https://github.com/Save-Point-Software/YANMT/blob/vX.Y.Z/docs/DEPLOYMENT.md#1-install-with-the-msi).
