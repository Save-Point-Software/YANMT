# YANMT deployment guide (Windows Server, PowerShell)

YANMT ships as one self-contained `yanmt.exe` that runs as a Windows service. Nothing else is installed on
the server: no .NET runtime, IIS, database server, Python or Node. Run every command below from the same
elevated PowerShell prompt; later steps reuse variables (`$app`, `$data`, `$cert`, `$account`) set by
earlier ones. Replace the example names (`svc-yanmt`, `YANMT-Admins`, `CONTOSO`, the management subnet)
with your own.

The steps are in the order they must run: the certificate before its private-key grant, and the account,
groups and configuration before the service's first start.

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

### 2.2 Certificate

YANMT serves HTTPS only and binds the certificate itself through Kestrel: there is no
`netsh http add sslcert` step. The certificate must be in `LocalMachine\My` before you grant the service
account access to its private key (section 2.3).

**Production:** use a certificate from your internal CA (or a public one) with the server's DNS name in the
subject alternative names. Import it into `LocalMachine\My`, then load it:

```powershell
$cert = Get-Item 'Cert:\LocalMachine\My\A1B2C3D4E5F60718293A4B5C6D7E8F9012345678'   # your thumbprint
```

**Lab only:** a self-signed certificate for the computer name. Browsers will warn about it unless you trust it
on the client.

```powershell
$cert = New-SelfSignedCertificate -DnsName $env:COMPUTERNAME -CertStoreLocation Cert:\LocalMachine\My
```

Either way, note the thumbprint for `Yanmt:Https:CertificateThumbprint` (section 2.5):

```powershell
$cert.Thumbprint
```

### 2.3 Service account

Run YANMT as a dedicated low-privilege account, not LocalSystem. On a domain a gMSA is preferred; on a
workgroup server use a local account. The account needs:

| Right | Why |
| --- | --- |
| Log on as a service | Start as a service. See the note at the end of section 2.6. |
| Modify on the data folder | Database, WAL files, backups, map images, logs, `secrets.entropy`. |
| Read and execute on the install folder | Run the exe. |
| Read on the certificate's private key | Kestrel loads the HTTPS certificate at startup. |

#### Create the account

**Workgroup (local account):**

```powershell
New-LocalUser svc-yanmt -Password (Read-Host -AsSecureString 'svc-yanmt password') -PasswordNeverExpires `
    -Description 'YANMT service account'
$account = "$env:COMPUTERNAME\svc-yanmt"
```

Use the `COMPUTERNAME\name` form. `icacls` does not resolve `.\svc-yanmt`, and a domain-style name that
doesn't exist (such as the `CONTOSO` placeholder) fails with "No mapping between account names and security
IDs".

**Domain (gMSA):** create the gMSA in AD, install it on the server with `Install-ADServiceAccount`, then:

```powershell
$account = 'CONTOSO\svc-yanmt$'
```

#### Folder permissions

```powershell
# Data folder: Modify for the service account and Full control for admins and SYSTEM, no inherited access for others.
icacls $data /inheritance:r /grant "${account}:(OI)(CI)M" 'BUILTIN\Administrators:(OI)(CI)F' 'NT AUTHORITY\SYSTEM:(OI)(CI)F' /T
icacls $app  /inheritance:r /grant "${account}:(OI)(CI)RX" 'BUILTIN\Administrators:(OI)(CI)F' 'NT AUTHORITY\SYSTEM:(OI)(CI)F' /T
```

`/T` applies the grant to files that already exist in the folders as well, not only to new ones.

Keep the data folder locked down. The database holds DPAPI-encrypted device credentials and
`secrets.entropy` is part of the key material, so anyone who can read both on this machine can
decrypt them.

#### Certificate private key

The service must be able to read the certificate's private key. If it can't, startup fails with
"Keyset does not exist" or a message naming the thumbprint.

**Certificate console (recommended):**

1. Run `certlm.msc`.
2. Open *Personal > Certificates* and right-click the YANMT certificate.
3. *All Tasks > Manage Private Keys*.
4. *Add* the service account (for a local account, set *From this location* to the server itself),
   give it **Read** only (clear *Full control*), and click *OK*.

**Script (alternative):** run this only after the certificate exists (`$cert` from section 2.2), then run
the check below it. In the v1 shakedown a scripted grant didn't take effect, so don't skip the check.

```powershell
if (-not $cert) { throw 'Set $cert first (section 2.2).' }
if (-not $cert.HasPrivateKey) { throw "Certificate $($cert.Thumbprint) has no private key." }

# RSA or ECDSA; anything else stops here.
$key = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
if (-not $key) { $key = [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($cert) }

$name = $null
if ($key -is [System.Security.Cryptography.RSACng] -or $key -is [System.Security.Cryptography.ECDsaCng]) {
    $name = $key.Key.UniqueName                                    # CNG key (RSA or ECDSA)
} elseif ($key -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
    $name = $key.CspKeyContainerInfo.UniqueKeyContainerName        # legacy CSP key (RSA only)
}
if ([string]::IsNullOrWhiteSpace($name)) {
    throw "Unsupported or unreadable private key on $($cert.Thumbprint). Use certlm.msc instead."
}

$file = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$name"
if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
    $file = Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$name"
}
if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
    throw "Private key file not found for $($cert.Thumbprint). Use certlm.msc instead."
}
icacls $file /grant "${account}:R"
```

Check that the grant took effect (with either method):

```powershell
icacls $file          # after the script; the service account must be listed with (R)
```

After the console method, reopen *Manage Private Keys* and confirm the account is listed with *Read*.
If it isn't there, the service will fail with "Keyset does not exist".

### 2.4 Windows groups

Authentication is Windows Integrated (Negotiate). Create the groups **before** the configuration and the
service's first start; they are referenced in `appsettings.json` (section 2.5).

**Workgroup (local groups):**

```powershell
New-LocalGroup -Name 'YANMT-Admins'  -Description 'YANMT administrators'
New-LocalGroup -Name 'YANMT-Viewers' -Description 'YANMT read-only users'
Add-LocalGroupMember -Group 'YANMT-Admins' -Member "$env:COMPUTERNAME\alice"
```

**Domain:** create `YANMT-Admins` and `YANMT-Viewers` in AD (or use local groups containing domain users)
and add your people.

### 2.5 Configuration

Edit `$app\appsettings.json` (full reference in section 4). Set at least the certificate thumbprint from
section 2.2 and the groups from section 2.4. HTTPS only; there is no HTTP listener.

Workgroup, local groups:

```json
{
  "Yanmt": {
    "Https": { "Port": 8443, "CertificateThumbprint": "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678" },
    "Roles": { "AdminGroups": [ ".\\YANMT-Admins" ], "ViewerGroups": [ ".\\YANMT-Viewers" ] }
  }
}
```

Domain groups:

```json
    "Roles": { "AdminGroups": [ "CONTOSO\\YANMT-Admins" ], "ViewerGroups": [ "CONTOSO\\YANMT-Viewers" ] }
```

- JSON needs the backslash doubled: `".\\YANMT-Admins"` in the file is the group `.\YANMT-Admins`.
  A single backslash is invalid JSON or silently changes the name.
- `.\` means a local group on this server.
- On a domain use `DOMAIN\\Group` (the NetBIOS domain name, backslash doubled).
- The thumbprint has no spaces.

### 2.6 Register and start the service

> **Don't run `yanmt.exe` by hand as an administrator before the service has started once.** The first run
> creates `$data\secrets.entropy` with an ACL that only the account running it (plus SYSTEM and
> Administrators) can read. If that is your admin account, the service then fails with
> "Cannot read or create ...secrets.entropy". See section 6 for the fix.

**Local account:** build the credential from the full `COMPUTERNAME\name` (the Server 2025 credential
dialog rejects `.\svc-yanmt` in `Get-Credential`):

```powershell
$cred = New-Object System.Management.Automation.PSCredential("$env:COMPUTERNAME\svc-yanmt", (Read-Host -AsSecureString 'svc-yanmt password'))

New-Service -Name YANMT -DisplayName 'YANMT network monitor' `
    -BinaryPathName "`"$app\yanmt.exe`"" -StartupType Automatic `
    -Credential $cred `
    -Description 'Read-only SNMP/ICMP/SSH network monitor'
sc.exe failure YANMT reset= 86400 actions= restart/60000/restart/60000/restart/60000
Start-Service YANMT
```

**gMSA** (empty password; install it first with `Install-ADServiceAccount`):

```powershell
sc.exe create YANMT binPath= "`"$app\yanmt.exe`"" start= auto obj= $account password= '""' DisplayName= 'YANMT network monitor'
sc.exe description YANMT 'Read-only SNMP/ICMP/SSH network monitor'
sc.exe failure YANMT reset= 86400 actions= restart/60000/restart/60000/restart/60000
Start-Service YANMT
```

The service name must be `YANMT`. Check it is running with `Get-Service YANMT`. If it doesn't start, see
section 6.

**Log on as a service (error 1069):** the first `Start-Service` can fail with a logon error (1069, "The
service did not start due to a logon failure") if the account lacks *Log on as a service*. Fix: open
`services.msc` > *YANMT* > *Properties* > *Log On*, re-type the account's password in both boxes, click *OK*
(Windows grants the right when you do this), then start the service again. You can also grant the right in
Local Security Policy or a GPO (*User Rights Assignment > Log on as a service*).

### 2.7 Firewall

```powershell
$mgmtSubnet = '192.0.2.0/24'   # REPLACE with your management subnet (this is a documentation example)

New-NetFirewallRule -DisplayName 'YANMT web UI' -Direction Inbound -Protocol TCP -LocalPort 8443 `
    -Action Allow -Profile Domain,Private -RemoteAddress $mgmtSubnet
```

`-Profile Domain,Private` matters on servers that aren't domain-joined: their network is usually classified
as Private (or Public), so a rule limited to `Domain` never matches and the UI is unreachable from other
machines. Check the profile with `Get-NetConnectionProfile`. Don't add `Public`.

Use the port from `Yanmt:Https:Port`. YANMT's outbound traffic needs ICMP echo, UDP 161 (SNMP) and TCP 22
(SSH) to the devices.

### 2.8 First sign-in

Browse to `https://<server-dns-name>:8443/`. Use the DNS name that matches the certificate and that your
browser treats as intranet, otherwise it will prompt for credentials instead of signing in silently.
A user in neither group gets "Access denied"; after changing group membership they must sign out of
Windows and back in.

### 2.9 Antivirus

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
| `Yanmt:Roles:AdminGroups` | empty | Groups with full access: `CONTOSO\YANMT-Admins`, `YANMT-Admins` or `.\YANMT-Admins` (local), or a SID. Double each backslash in JSON (section 2.5). |
| `Yanmt:Roles:ViewerGroups` | empty | Groups with read-only access. |
| `Yanmt:Logging:MaxFileSizeBytes` | `10485760` | Log file size before rolling. |
| `Yanmt:Logging:RetainedFileCount` | `30` | Log files kept. |
| `Yanmt:Polling:IcmpConcurrency` | `32` | Parallel ICMP polls. |
| `Yanmt:Polling:SnmpConcurrency` | `16` | Parallel SNMP polls. |
| `Yanmt:Polling:ReloadSeconds` | `300` | Fallback inventory reload interval (saves trigger an immediate reload). |
| `Logging:*` | Information; ASP.NET Core and EF Core at Warning | Standard .NET log levels. Errors also go to the Windows Event Log. |

Data retention (raw samples, rollups, events, config versions) and the other runtime settings are
changed in the web UI under *Settings*, not in `appsettings.json`.

Example (domain groups; see section 2.5 for local groups):

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

```powershell
$bk = '\\backup-host\yanmt\20261110'   # the backup folder to restore

Stop-Service YANMT
Rename-Item $data "$data.before-restore"
Copy-Item $bk $data -Recurse
# Re-apply the data folder ACL: a copied folder inherits the parent's permissions, not section 2.3's.
icacls $data /inheritance:r /grant "${account}:(OI)(CI)M" 'BUILTIN\Administrators:(OI)(CI)F' 'NT AUTHORITY\SYSTEM:(OI)(CI)F' /T
Start-Service YANMT
```

Verify:

1. `Get-Service YANMT` shows *Running*.
2. *Poll now* on a node works. This proves the restored credentials decrypt.
3. *Back up now* on a node works. This proves SSH and the pinned host key.

Then remove the old copy: `Remove-Item "$data.before-restore" -Recurse`.

The backup folder holds the encrypted credentials and `secrets.entropy`. Protect it and any leftover
`.before-restore` copy like the server itself.

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
  If a device's key legitimately changed, use "Forget" next to the pinned host key in the node's connection tests, then reconnect to pin the new one.
- Repeat section 2 (certificate, service account, groups, firewall) on the new server.

## 6. Troubleshooting

### The service won't start

Windows only reports a generic error: 1053 ("did not respond to the start or control request in a timely
fashion") or event 7009 (timeout). The real reason is in YANMT's own logs. Read **both**:

- `$data\logs\startup-failure.log`: failures before logging is up (for example a missing or unreadable
  certificate).
- `$data\logs\yanmt-YYYYMMDD.log` (today's date): everything after logging is up.

```powershell
Get-Content "$data\logs\startup-failure.log" -Tail 30 -ErrorAction SilentlyContinue
Get-Content "$data\logs\yanmt-$(Get-Date -Format yyyyMMdd).log" -Tail 50
```

| Message | Cause and fix |
| --- | --- |
| Error 1069, logon failure | The account lacks *Log on as a service*, or the password is wrong. See the end of section 2.6. |
| "Keyset does not exist" | The service account can't read the certificate's private key. Grant Read (section 2.3, *Certificate private key*) and verify. |
| "Cannot read or create ...secrets.entropy" | See below. |
| "Yanmt:Https:CertificateThumbprint is not set" | The thumbprint is missing from `$app\appsettings.json`, or (on a hand run) you started the exe from another folder. See below. |

### Running yanmt.exe by hand

A hand run shows errors on the console, which can be quicker than reading logs. Stop the service first (both
can't listen on the same port), and **`cd` to the install folder first**:

```powershell
Stop-Service YANMT
cd $app
.\yanmt.exe
```

Run from any other folder, the exe reads the `appsettings.json` there (or none) instead of the one in
`$app`, and reports a misleading "thumbprint is not set".

A hand run uses **your** (admin) rights, not the service account's. Permission problems (private key, data
folder, `secrets.entropy`) won't show up; when a hand run works and the service doesn't, suspect permissions.

### "Cannot read or create secrets.entropy"

Usually caused by running `yanmt.exe` by hand as an admin before the service's first start (section 2.6):
the file was created with an ACL only that admin can read.

- **No device credentials saved yet** (fresh install): delete the file and start the service; it recreates
  it with the right ACL.

  ```powershell
  Remove-Item "$data\secrets.entropy"
  Start-Service YANMT
  ```

- **Credentials already saved:** do **not** delete it. Without this file the stored credentials can't be
  decrypted and must be re-entered. Instead, re-run the data folder `icacls` from section 2.3 (with `/T`) so
  the service account gets access to the existing file, then start the service.
