# YANMT deployment guide (Windows Server, PowerShell)

YANMT ships as one self-contained `yanmt.exe` that runs as a Windows service. Nothing else is installed on
the server: no .NET runtime, IIS, database server, Python or Node. Run every command below from an
elevated PowerShell prompt. Replace the example names (`CONTOSO\svc-yanmt$`, `YANMT-Admins`, the thumbprint)
with your own.

## 1. Build the package

On a build machine with the .NET SDK (not the server):

```powershell
dotnet publish src/Yanmt.Web -c Release -r win-x64 -o publish
```

The output is self-contained, single-file, win-x64 and untrimmed. Copy the `publish` folder to the server.
`appsettings.Development.json` is not included.

## 2. Install

### 2.1 Folders

```powershell
$app  = 'C:\Program Files\YANMT'      # binaries and appsettings.json
$data = 'C:\ProgramData\YANMT'        # database, backups, maps, logs, secrets.entropy

New-Item -ItemType Directory -Force -Path $app, $data | Out-Null
Copy-Item .\publish\* -Destination $app -Recurse -Force
```

The data folder defaults to `%ProgramData%\YANMT`. To move it, set `Yanmt:DataPath` in `appsettings.json`
(see section 4). YANMT creates `logs`, `backups` and `maps` inside it at startup.

### 2.2 Service account

Run YANMT as a dedicated low-privilege account, not LocalSystem. A gMSA is preferred. The account needs:

| Right | Why |
| --- | --- |
| Log on as a service | Start as a service. Grant it with Local Security Policy or a GPO (*Log on as a service*). |
| Modify on the data folder | Database, WAL files, backups, map images, logs, `secrets.entropy`. |
| Read and execute on the install folder | Run the exe. |
| Read on the certificate's private key | Kestrel loads the HTTPS certificate at startup. |

```powershell
$account = 'CONTOSO\svc-yanmt$'      # gMSA; use '.\svc-yanmt' for a local account

# Data folder: Modify for the service account and Full control for admins and SYSTEM, no inherited access for others.
icacls $data /inheritance:r /grant "${account}:(OI)(CI)M" 'BUILTIN\Administrators:(OI)(CI)F' 'NT AUTHORITY\SYSTEM:(OI)(CI)F'
icacls $app  /inheritance:r /grant "${account}:(OI)(CI)RX" 'BUILTIN\Administrators:(OI)(CI)F' 'NT AUTHORITY\SYSTEM:(OI)(CI)F'
```

Keep the data folder locked down. The database holds DPAPI-encrypted device credentials and
`secrets.entropy` is part of the key material, so anyone who can read both on this machine can
decrypt them.

Certificate private key access (certificate in `LocalMachine\My`):

```powershell
$thumb = 'A1B2C3D4E5F60718293A4B5C6D7E8F9012345678'
$cert  = Get-Item "Cert:\LocalMachine\My\$thumb"
$key   = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
$file  = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($key.Key.UniqueName)"   # CNG keys
if (-not (Test-Path $file)) { $file = Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$($key.Key.UniqueName)" }
icacls $file /grant "${account}:R"
```

(Or use the certificate MMC: *All Tasks > Manage Private Keys*.) The service must be able to read the key
or startup fails with a message naming the thumbprint.

### 2.3 Configuration

Edit `$app\appsettings.json` (section 4): set at least `Yanmt:Https:CertificateThumbprint` and
`Yanmt:Roles`. HTTPS only; there is no HTTP listener.

### 2.4 Register and start the service

Local account (prompts for the password):

```powershell
New-Service -Name YANMT -DisplayName 'YANMT network monitor' `
    -BinaryPathName "`"$app\yanmt.exe`"" -StartupType Automatic `
    -Credential (Get-Credential '.\svc-yanmt') `
    -Description 'Read-only SNMP/ICMP/SSH network monitor'
```

gMSA (empty password; install it first with `Install-ADServiceAccount`):

```powershell
sc.exe create YANMT binPath= "`"$app\yanmt.exe`"" start= auto obj= $account password= '""' DisplayName= 'YANMT network monitor'
sc.exe description YANMT 'Read-only SNMP/ICMP/SSH network monitor'
sc.exe failure YANMT reset= 86400 actions= restart/60000/restart/60000/restart/60000
Start-Service YANMT
```

The service name must be `YANMT`. Startup problems are written to the Application event log and to
`$data\logs`.

### 2.5 Certificate

Use a certificate from your internal CA (or a public one) with the server's DNS name in the subject
alternative names, installed in `LocalMachine\My`. YANMT binds it itself through Kestrel: there is no
`netsh http add sslcert` step. Put the thumbprint (no spaces) in `Yanmt:Https:CertificateThumbprint`.

### 2.6 Firewall

```powershell
New-NetFirewallRule -DisplayName 'YANMT web UI' -Direction Inbound -Protocol TCP -LocalPort 8443 `
    -Action Allow -Profile Domain -RemoteAddress 192.0.2.0/24
```

Use your own management subnet and the port from `Yanmt:Https:Port`. YANMT's outbound traffic needs
ICMP echo, UDP 161 (SNMP) and TCP 22 (SSH) to the devices.

### 2.7 Windows groups and first sign-in

Authentication is Windows Integrated (Negotiate). Create two groups (AD or local), add them under
`Yanmt:Roles`, and add your people:

```powershell
New-LocalGroup -Name 'YANMT-Admins'  -Description 'YANMT administrators'
New-LocalGroup -Name 'YANMT-Viewers' -Description 'YANMT read-only users'
Add-LocalGroupMember -Group 'YANMT-Admins' -Member 'CONTOSO\alice'
```

Browse to `https://<server-dns-name>:8443/`. Use the DNS name that matches the certificate and that your
browser treats as intranet, otherwise it will prompt for credentials instead of signing in silently.
A user in neither group gets "Access denied"; after changing group membership they must sign out of
Windows and back in.

### 2.8 Antivirus

If Defender (or another AV) scans the data folder, the SQLite database and its `-wal` / `-shm` files can
be slowed or locked. Add exclusions:

```powershell
Add-MpPreference -ExclusionPath $data
Add-MpPreference -ExclusionProcess "$app\yanmt.exe"
```

Apply the same in your enterprise AV policy if it is not Defender.

## 3. Upgrade and uninstall

### Upgrade

Database migrations apply automatically at startup.

```powershell
Stop-Service YANMT
# Back up first (section 5). Then swap the binaries; keep your edited appsettings.json.
Copy-Item "$app\appsettings.json" "$env:TEMP\appsettings.json.bak"
Copy-Item .\publish\* -Destination $app -Recurse -Force -Exclude appsettings.json
Start-Service YANMT
Get-Content "$data\logs\*" -Tail 30
```

Compare the new release's `appsettings.json` with yours for added keys. Missing keys fall back to defaults.
Migrations are one-way: to roll back, restore the pre-upgrade backup together with the old binaries.

### Uninstall

```powershell
Stop-Service YANMT
sc.exe delete YANMT
Remove-NetFirewallRule -DisplayName 'YANMT web UI'
Remove-Item $app -Recurse
# Only when you really want to discard everything (database, backups, credentials):
# Remove-Item $data -Recurse
```

## 4. Configuration reference

Settings live in `appsettings.json` next to the exe. **It holds no secrets.** Device credentials are
stored encrypted in the database. Any key can also be set with an environment variable using `__` as
the separator (for example `Yanmt__Https__Port=9443`). Restart the service after changes.

| Key | Default | Meaning |
| --- | --- | --- |
| `Yanmt:DataPath` | empty (`%ProgramData%\YANMT`) | Data folder. Environment variables are expanded; a relative path is relative to the install folder. |
| `Yanmt:Https:Port` | `8443` | HTTPS port. |
| `Yanmt:Https:ListenAddress` | empty (all addresses) | A single IP address to bind to. |
| `Yanmt:Https:CertificateThumbprint` | empty | Certificate to serve. Required outside development. |
| `Yanmt:Https:StoreName` / `StoreLocation` | `My` / `LocalMachine` | Where to find the certificate. |
| `Yanmt:Roles:AdminGroups` | empty | Groups with full access: `CONTOSO\YANMT-Admins`, `YANMT-Admins` or `.\YANMT-Admins` (local), or a SID. |
| `Yanmt:Roles:ViewerGroups` | empty | Groups with read-only access. |
| `Yanmt:Logging:MaxFileSizeBytes` | `10485760` | Log file size before rolling. |
| `Yanmt:Logging:RetainedFileCount` | `30` | Log files kept. |
| `Yanmt:Polling:IcmpConcurrency` | `32` | Parallel ICMP polls. |
| `Yanmt:Polling:SnmpConcurrency` | `16` | Parallel SNMP polls. |
| `Yanmt:Polling:ReloadSeconds` | `300` | Fallback inventory reload interval (saves trigger an immediate reload). |
| `Logging:*` | Information; ASP.NET Core and EF Core at Warning | Standard .NET log levels. Errors also go to the Windows Event Log. |

Data retention (raw samples, rollups, events, config versions) and the other runtime settings are
changed in the web UI under *Settings*, not in `appsettings.json`.

Example:

```json
{
  "Yanmt": {
    "Https": { "Port": 8443, "CertificateThumbprint": "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678" },
    "Roles": { "AdminGroups": [ "CONTOSO\\YANMT-Admins" ], "ViewerGroups": [ "CONTOSO\\YANMT-Viewers" ] }
  }
}
```

## 5. Backup and restore

### What to back up

Everything is in the data folder (`$data`):

| Item | Notes |
| --- | --- |
| `yanmt.db` (+ `yanmt.db-wal`, `yanmt.db-shm`) | Inventory, maps, monitors, settings, history, audit log, encrypted credentials. |
| `secrets.entropy` | Extra DPAPI entropy for the stored credentials. **Without it the credentials can't be decrypted.** Back it up with the database. |
| `backups\` | Device configuration backups. |
| `maps\` | Map background images. |
| `appsettings.json` | In the install folder. Contains no secrets, but you need it. |

`logs\` is optional.

The simplest consistent backup is to stop the service, copy the folder, and start it again:

```powershell
Stop-Service YANMT
Copy-Item $data "\\backup-host\yanmt\$(Get-Date -Format yyyyMMdd)" -Recurse
Start-Service YANMT
```

A copy while the service runs must include the `-wal` file together with the database; it is not a
guaranteed-consistent snapshot, so prefer the stop-and-copy method. Protect the backup like the server
itself: it holds the encrypted credentials and `secrets.entropy`.

### Restore on the same server

Stop the service, restore the whole data folder (database, `secrets.entropy`, `backups`, `maps`), check the
ACLs from section 2.2, start the service.

### Restore on a different server: the DPAPI caveat

Credentials are encrypted with DPAPI at **machine scope**. A different machine has a different DPAPI key, so
**credentials from a copied database will not decrypt there**, even if you also copy `secrets.entropy`.
YANMT starts, but at startup it logs an error if credential profiles exist that can't be read, and polling
and backups that need them fail until the credentials are replaced. You have two ways to move:

1. **Passphrase export and import (recommended).** Before decommissioning the old server, an admin opens
   *Credentials > Export / import* and exports the profiles. On the new server, restore the database and
   data folder (or start fresh), then import the file. Imported passwords are re-encrypted with the new
   machine's key.
2. **Manual re-entry.** Open each credential profile on the new server and re-enter its passwords. Use this
   if you no longer have an export, or the old server is already gone.

#### Treat the export file like a password vault

The export file contains every device password. The passphrase is the only thing protecting it.

- **Use a passphrase of 4 or more random words** (for example from a dice-based word list), not a
  phrase you made up or a quote. Longer is better. Never reuse a password you use elsewhere.
- Store the passphrase in your password manager, not next to the file and never in the same ticket,
  email or chat thread.
- Keep the file on encrypted or access-controlled storage while it exists, and **delete it as soon as the
  import is verified**, including from the Downloads folder, backups and sync folders.
- Export only when you need to move or restore, and only as an admin. Each export and import is written
  to the audit log (counts only, never passwords or the passphrase).
- If the file or passphrase may have leaked, treat every device password in it as compromised and change them.

Import behaviour: a profile whose name already exists is kept or overwritten, as you choose. A name that
differs only in letter case from an existing one (for example `Core-SSH` and `core-ssh`) is never merged
or overwritten; it is skipped and listed in the result. Rejected entries are listed too. Only profiles
are exported; per-node settings are not.

Other notes for a new server:

- The ICMP, SNMP and SSH paths from the new server must be allowed by device ACLs and the management firewall.
- SSH host keys are pinned per node (trust on first use) and stay in the database, so devices keep matching.
  If a device's key legitimately changed, re-pin it from the node's settings.
- Repeat section 2 (service account, certificate, groups, firewall) on the new server.
