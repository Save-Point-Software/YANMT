# YANMT deployment guide (Windows Server, PowerShell)

YANMT ships as one self-contained `yanmt.exe` that runs as a Windows service. Nothing else is installed on
the server: no .NET runtime, IIS, database server, Python or Node. There are two ways to install it:

- **The MSI (recommended, section 1).** One installer from GitHub Releases does the folders, permissions,
  service, groups, settings and firewall. The service runs as the virtual account `NT SERVICE\YANMT`, so
  there is no service-account password.
- **By hand (sections 2 to 4).** Copy the published folder and run the PowerShell steps yourself, with a
  local account or gMSA. Keep this for servers where you can't run an MSI, or to see what the MSI does.

Sections 5 to 7 (configuration, backup and restore, troubleshooting) apply to both.

Run every command below from the same elevated PowerShell prompt; later steps reuse variables (`$app`,
`$data`, `$cert`, `$account`) set by earlier ones. Replace the example names (`svc-yanmt`, `YANMT-Admins`,
`CONTOSO`, the management subnet) with your own.

## 1. Install with the MSI

The MSI does everything in sections 3 and 4 except three things, which stay manual: the certificate itself,
adding people to the groups, and antivirus exclusions. It never contacts a device.

### 1.1 Before you install

1. **Download** `yanmt-X.Y.Z.msi` and `SHA256SUMS.txt` from the GitHub release, and check the hash:

   ```powershell
   Get-FileHash .\yanmt-1.2.3.msi -Algorithm SHA256      # must match the line in SHA256SUMS.txt
   Unblock-File .\yanmt-1.2.3.msi
   ```

   The MSI is **not code-signed** yet. Windows marks the download as coming from the internet, and
   SmartScreen may show "Windows protected your PC" (*More info > Run anyway*). `Unblock-File` clears the
   mark once you have checked the hash.
2. **Certificate:** put it in `LocalMachine\My` with its private key (section 3.2) and note the thumbprint.
   The MSI never creates one. It checks that the certificate exists and has a private key, and it grants the
   service Read on that key.
3. **Groups:** domain groups must already exist. A local group written as `.\Name` is created if it's
   missing. The MSI never adds members (section 3.4).
4. **Data folder:** the MSI uses only `%ProgramData%\YANMT`. If `appsettings.json` or a machine environment
   variable sets a different `Yanmt:DataPath`, the install stops before changing anything.

### 1.2 Properties

| Property | Default | Meaning |
| --- | --- | --- |
| `CERT_THUMBPRINT` | (required) | Thumbprint of the HTTPS certificate in `LocalMachine\My`. Spaces are ignored. |
| `ADMIN_GROUPS` | (required) | Admin groups, separated by `;`: `CONTOSO\YANMT-Admins`, `.\YANMT-Admins` (local), or a SID. Single backslashes: this is not JSON. |
| `VIEWER_GROUPS` | empty | Viewer groups, same format. |
| `PORT` | `8443` | HTTPS port, 1 to 65535. |
| `FIREWALL_REMOTE_ADDRESS` | empty | Remote addresses allowed by the inbound rule: an address, subnet (`192.0.2.0/24`), range, or `LocalSubnet`, separated by `,` (`Any` is refused). **Empty means no firewall rule is created.** |
| `CLEAR_VIEWER_GROUPS` | | `1` removes the stored viewer groups (section 1.4). |
| `CLEAR_FIREWALL_REMOTE_ADDRESS` | | `1` removes the firewall rule and its stored scope (section 1.4). |

The values are stored under `HKLM\SOFTWARE\YANMT` and override the same keys in `appsettings.json`
(section 5). An upgrade or repair reads them back, so you don't type them again. A value on the command
line always wins.

### 1.3 Install

**Interactive:** run the MSI. One dialog asks for the properties above. Bad values (unknown certificate,
group that doesn't resolve, bad port) are shown there before anything is installed.

**Silent:** `msiexec` is a GUI program, so PowerShell doesn't wait for it and doesn't see its exit code.
Define this helper once in the prompt; the examples in this section use it. It waits, writes a verbose
log, and stops with an error unless the MSI succeeded (0, or 3010 for "succeeded, restart needed").

```powershell
function Invoke-Msi([string] $Log, [string[]] $Arguments) {
    # Start-Process quotes nothing, so quote each value: NAME="value" and the full path of the MSI.
    $quoted = foreach ($a in $Arguments) {
        if ($a -match '^([A-Z_]+)=(.*)$') { "$($Matches[1])=`"$($Matches[2])`"" }
        elseif ($a -match '^/') { $a }
        else { "`"$((Resolve-Path $a -ErrorAction Stop).Path)`"" }
    }
    $line = (@($quoted) + @('/qn', '/norestart', '/l*v', "`"$(Join-Path $PWD $Log)`"")) -join ' '
    $p = Start-Process msiexec.exe -ArgumentList $line -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) { throw "msiexec failed with exit code $($p.ExitCode); see $Log." }
}

Invoke-Msi install.log '/i', '.\yanmt-1.2.3.msi',
    'CERT_THUMBPRINT=A1B2C3D4E5F60718293A4B5C6D7E8F9012345678',
    'ADMIN_GROUPS=CONTOSO\YANMT-Admins', 'VIEWER_GROUPS=CONTOSO\YANMT-Viewers',
    'FIREWALL_REMOTE_ADDRESS=192.0.2.0/24'
```

Bad settings fail the install with exit code 1603 and change nothing; the reason is in the log. With plain
`msiexec /i ... /qn` the prompt returns at once, so a following command can run before the install is done.

What the MSI does:

- Binaries in `C:\Program Files\YANMT`. Read and execute for `NT SERVICE\YANMT`, Full control for
  Administrators and SYSTEM.
- `C:\ProgramData\YANMT`: Modify for the service, Full control for Administrators and SYSTEM, nothing
  inherited. Files already in it are reset to inherit that ACL, and `secrets.entropy` gets its own
  protected ACL.
- Read for the service on the certificate's private key. The installer reads the grant back and fails if
  it isn't there.
- Creates missing local (`.\Name`) groups.
- Registers the `YANMT` service (automatic start; on failure it restarts after 60 s, three times, and the
  count resets after a day) and the `YANMT` Event Log source in the Application log.
- If `FIREWALL_REMOTE_ADDRESS` is set, an inbound rule *YANMT web UI* for TCP `PORT`, profiles Domain and
  Private (never Public), limited to those addresses.
- Starts the service. **A service that doesn't start does not roll the install back.** The final dialog shows
  the sign-in URL, or says the service didn't start and points to `C:\ProgramData\YANMT\logs\startup-failure.log`
  (section 7). A silent install still exits 0; look for `YANMT WARNING` in its log.

Then, by hand: add people to the groups (section 3.4), add the antivirus exclusions (section 3.9), and sign in
(section 3.8). Other settings (log sizes, polling concurrency, listen address) still go in
`C:\Program Files\YANMT\appsettings.json` (section 5).

For the commands in sections 6 and 7, set:

```powershell
$app     = 'C:\Program Files\YANMT'
$data    = 'C:\ProgramData\YANMT'
$account = 'NT SERVICE\YANMT'
```

### 1.4 Change settings

Run the installed version's MSI again, or use *Change* in *Apps > Installed apps*: the dialog opens with
the current values. Silent, give only what changes:

```powershell
Invoke-Msi change.log '/i', '.\yanmt-1.2.3.msi', 'PORT=9443'
Invoke-Msi clear.log  '/i', '.\yanmt-1.2.3.msi', 'CLEAR_VIEWER_GROUPS=1', 'CLEAR_FIREWALL_REMOTE_ADDRESS=1'
```

An empty value on the command line means "not given", so use the `CLEAR_` properties to remove the viewer
groups or the firewall rule. In the dialog, leaving those fields empty does the same. With the viewer groups
cleared, `Yanmt:Roles:ViewerGroups` from `appsettings.json` applies again (empty by default).

### 1.5 Upgrade

Run the newer MSI (interactively, or silently with no properties). The MSI:

1. Stops the service.
2. Copies `yanmt.db` (with `-wal`/`-shm`) and `secrets.entropy` to
   `C:\ProgramData\YANMT\backups\pre-upgrade\<old version>-<timestamp>\`. It keeps the newest three.
3. Replaces the binaries. It keeps `appsettings.json`.
4. Starts the service, which applies database migrations.

A failed upgrade rolls back to the old version. The same or an older version is refused, because
migrations are one-way. To go back to an older version, use the backup that the upgrade **from** that
version made. Its folder name starts with that version. Everything recorded since that upgrade is lost.

```powershell
$older = '1.2.3'                        # the version to go back to; you need its MSI
$ErrorActionPreference = 'Stop'

# The newest backup made when upgrading from $older (by time, not by name).
$bk = Get-ChildItem "$data\backups\pre-upgrade" -Directory | Where-Object Name -like "$older-*" |
    Sort-Object CreationTimeUtc | Select-Object -Last 1
if (-not $bk -or -not (Test-Path "$($bk.FullName)\yanmt.db")) { throw "No pre-upgrade backup from $older in $data\backups\pre-upgrade." }
"Restoring $($bk.FullName) (made $($bk.CreationTime))"

Invoke-Msi uninstall.log '/x', '.\yanmt-1.2.4.msi'          # the newer version; the data folder stays
if (Get-Service YANMT -ErrorAction SilentlyContinue) { throw 'The YANMT service still exists; the uninstall did not finish.' }

foreach ($f in 'yanmt.db', 'yanmt.db-wal', 'yanmt.db-shm') {
    if (Test-Path "$data\$f") { Remove-Item "$data\$f" }       # stops here if a file can't be deleted
}
Copy-Item "$($bk.FullName)\*" $data

Invoke-Msi install.log '/i', ".\yanmt-$older.msi", 'CERT_THUMBPRINT=...', 'ADMIN_GROUPS=...'
```

Delete the newer `-wal`/`-shm` files as shown: SQLite would otherwise apply them to the older database.
Each step stops on an error, so the data is never touched after a failed uninstall. The uninstall removes
the stored settings, so give the properties again. Reinstalling resets the data folder ACL, including the
restored files.

### 1.6 Uninstall

Use *Apps > Installed apps > YANMT > Uninstall*, or silently `Invoke-Msi uninstall.log '/x', '.\yanmt-1.2.3.msi'` (section 1.3).

Removed: the service, the firewall rule, the Event Log source, `HKLM\SOFTWARE\YANMT`, and the install
folder. That includes **`appsettings.json`**; an upgrade keeps it, an uninstall doesn't. Copy it somewhere
else first if you edited it and want to keep it.

Kept: the data folder (database, backups, maps, logs, `secrets.entropy`), the service's Read grant on the
certificate key, and the local groups. To discard everything as well:

```powershell
Remove-Item 'C:\ProgramData\YANMT' -Recurse   # database, backups and credentials: gone for good
```

### 1.7 Moving a manual install to the MSI

Back up first (section 6), then install the MSI as in section 1.3 with the properties. Your
`appsettings.json` is not read for them. The MSI:

- stops and deletes the existing `YANMT` service, whatever account it ran as, and creates its own as
  `NT SERVICE\YANMT`;
- resets the data folder ACL, including existing files and `secrets.entropy`, so the old account's grants
  are gone, and grants the service Read on the certificate key;
- keeps `appsettings.json`. Its port, certificate and group values are now overridden by the MSI's.

Stored credentials and config history stay readable: DPAPI is machine scope, and `secrets.entropy` stays in
place. A manual install with a custom `Yanmt:DataPath` is refused (section 1.1), and the old service keeps
running.

If the manual install has a firewall rule and you set `FIREWALL_REMOTE_ADDRESS`, remove the old rule
**before** installing: both are named *YANMT web UI*, and `Remove-NetFirewallRule -DisplayName` would delete
both. Without `FIREWALL_REMOTE_ADDRESS`, keep the old rule; the MSI won't touch it.

```powershell
Remove-NetFirewallRule -DisplayName 'YANMT web UI'   # only before an install with FIREWALL_REMOTE_ADDRESS
```

The MSI doesn't delete the old service account. Once the service runs under the MSI, you can remove it:

```powershell
Remove-LocalUser svc-yanmt                    # local account
# gMSA: Uninstall-ADServiceAccount svc-yanmt, and delete it in AD if nothing else uses it.
```

The old account's Read grant on the certificate's private key remains; remove it in `certlm.msc` (*Manage
Private Keys*) if you like. Antivirus exclusions use the same paths, so they still apply.

## 2. Build the package

On a build machine with the .NET SDK (not the server):

```powershell
dotnet publish src/Yanmt.Web -c Release -r win-x64 -o publish
```

The output is self-contained, single-file, win-x64 and untrimmed. Copy the `publish` folder to the server.
`appsettings.Development.json` is not included.

## 3. Install by hand

The steps run in this order: the certificate before its private-key grant, and the account, groups and
configuration before the service's first start.

### 3.1 Folders

```powershell
$app  = 'C:\Program Files\YANMT'      # binaries and appsettings.json
$data = 'C:\ProgramData\YANMT'        # database, backups, maps, logs, secrets.entropy

New-Item -ItemType Directory -Force -Path $app, $data | Out-Null
Copy-Item .\publish\* -Destination $app -Recurse -Force
```

The data folder defaults to `%ProgramData%\YANMT`. To move it, set `Yanmt:DataPath` in `appsettings.json`
(see section 5). YANMT creates `logs`, `backups` and `maps` inside it at startup.

### 3.2 Certificate

YANMT serves HTTPS only and binds the certificate itself through Kestrel: there is no
`netsh http add sslcert` step. The certificate must be in `LocalMachine\My` before you grant the service
account access to its private key (section 3.3).

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

Either way, note the thumbprint for `Yanmt:Https:CertificateThumbprint` (section 3.5):

```powershell
$cert.Thumbprint
```

### 3.3 Service account

Run YANMT as a dedicated low-privilege account, not LocalSystem. On a domain a gMSA is preferred; on a
workgroup server use a local account. The account needs:

| Right | Why |
| --- | --- |
| Log on as a service | Start as a service. See the note at the end of section 3.6. |
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
icacls $data /inheritance:r /grant "${account}:(OI)(CI)M" 'BUILTIN\Administrators:(OI)(CI)F' 'NT AUTHORITY\SYSTEM:(OI)(CI)F'
icacls $app  /inheritance:r /grant "${account}:(OI)(CI)RX" 'BUILTIN\Administrators:(OI)(CI)F' 'NT AUTHORITY\SYSTEM:(OI)(CI)F'
# Files already in the folders: drop their own ACEs and inherit the folder ACL above.
icacls "$app\*" /reset /T /Q
icacls "$data\*" /reset /T /Q
# secrets.entropy (if it exists yet) keeps its own protected ACL, as YANMT creates it.
if (Test-Path "$data\secrets.entropy") {
    icacls "$data\secrets.entropy" /inheritance:r /grant "${account}:M" 'BUILTIN\Administrators:F' 'NT AUTHORITY\SYSTEM:F'
}
```

Don't add `/T` to the first two commands. On a file, `/inheritance:r` removes the inherited ACEs and the
`(OI)(CI)` grants don't apply, so every existing file is left with an **empty DACL** (nobody, not even an
admin, can read it; the service fails with error 5). The `/reset` lines handle existing files instead.

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

**Script (alternative):** run this only after the certificate exists (`$cert` from section 3.2), then run
the check below it. In the v1 shakedown a scripted grant didn't take effect, so don't skip the check.

```powershell
if (-not $cert) { throw 'Set $cert first (section 3.2).' }
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

### 3.4 Windows groups

Authentication is Windows Integrated (Negotiate). Create the groups **before** the configuration and the
service's first start; they are referenced in `appsettings.json` (section 3.5).

**Workgroup (local groups):**

```powershell
New-LocalGroup -Name 'YANMT-Admins'  -Description 'YANMT administrators'
New-LocalGroup -Name 'YANMT-Viewers' -Description 'YANMT read-only users'
Add-LocalGroupMember -Group 'YANMT-Admins' -Member "$env:COMPUTERNAME\alice"
```

**Domain:** create `YANMT-Admins` and `YANMT-Viewers` in AD (or use local groups containing domain users)
and add your people.

### 3.5 Configuration

Edit `$app\appsettings.json` (full reference in section 5). Set at least the certificate thumbprint from
section 3.2 and the groups from section 3.4. HTTPS only; there is no HTTP listener.

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

### 3.6 Register and start the service

> The first run creates `$data\secrets.entropy`. It gets the data folder's permissions (so the service
> account from section 3.3 can read it, whoever ran YANMT first), without Users or other broad groups, plus
> SYSTEM and Administrators. Set the data folder permissions **before** the first run, by hand or as a service.

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
section 7.

**Log on as a service (error 1069):** the first `Start-Service` can fail with a logon error (1069, "The
service did not start due to a logon failure") if the account lacks *Log on as a service*. Fix: open
`services.msc` > *YANMT* > *Properties* > *Log On*, re-type the account's password in both boxes, click *OK*
(Windows grants the right when you do this), then start the service again. You can also grant the right in
Local Security Policy or a GPO (*User Rights Assignment > Log on as a service*).

### 3.7 Firewall

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

### 3.8 First sign-in

Browse to `https://<server-dns-name>:8443/`. Use the DNS name that matches the certificate and that your
browser treats as intranet, otherwise it will prompt for credentials instead of signing in silently.
A user in neither group gets "Access denied"; after changing group membership they must sign out of
Windows and back in.

### 3.9 Antivirus

If Defender (or another AV) scans the data folder, the SQLite database and its `-wal` / `-shm` files can
be slowed or locked. Add exclusions:

```powershell
Add-MpPreference -ExclusionPath $data
Add-MpPreference -ExclusionProcess "$app\yanmt.exe"
```

Apply the same in your enterprise AV policy if it is not Defender.

## 4. Upgrade and uninstall (manual install)

### Upgrade

Database migrations apply automatically at startup.

```powershell
Stop-Service YANMT
# Back up first (section 6). Then swap the binaries; keep your edited appsettings.json.
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

For an MSI install, see sections 1.5 and 1.6 instead.

## 5. Configuration reference

Settings live in `appsettings.json` next to the exe. **It holds no secrets.** Device credentials are
stored encrypted in the database. Any key can also be set with an environment variable using `__` as
the separator (for example `Yanmt__Https__Port=9443`). Restart the service after changes.

**Installer settings.** The MSI (section 1) stores `PORT`, `CERT_THUMBPRINT`, `ADMIN_GROUPS` and
`VIEWER_GROUPS` under `HKLM\SOFTWARE\YANMT` (values `Port`, `CertificateThumbprint`, `AdminGroups`,
`ViewerGroups`; group lists separated by `;`). They override `appsettings.json`, and environment
variables override them. An empty or missing value leaves `appsettings.json` in charge, and a manual
install has no such key. Change them with the installer (section 1.4), not in `appsettings.json`.
The startup log names the settings that came from the registry.

| Key | Default | Meaning |
| --- | --- | --- |
| `Yanmt:DataPath` | empty (`%ProgramData%\YANMT`) | Data folder. Environment variables are expanded; a relative path is relative to the install folder. |
| `Yanmt:Https:Port` | `8443` | HTTPS port. |
| `Yanmt:Https:ListenAddress` | empty (all addresses) | A single IP address to bind to. |
| `Yanmt:Https:CertificateThumbprint` | empty | Certificate to serve. Required outside development. |
| `Yanmt:Https:StoreName` / `StoreLocation` | `My` / `LocalMachine` | Where to find the certificate. |
| `Yanmt:Roles:AdminGroups` | empty | Groups with full access: `CONTOSO\YANMT-Admins`, `YANMT-Admins` or `.\YANMT-Admins` (local), or a SID. Double each backslash in JSON (section 3.5). |
| `Yanmt:Roles:ViewerGroups` | empty | Groups with read-only access. |
| `Yanmt:Logging:MaxFileSizeBytes` | `10485760` | Log file size before rolling. |
| `Yanmt:Logging:RetainedFileCount` | `30` | Log files kept. |
| `Yanmt:Polling:IcmpConcurrency` | `32` | Parallel ICMP polls. |
| `Yanmt:Polling:SnmpConcurrency` | `16` | Parallel SNMP polls. |
| `Yanmt:Polling:ReloadSeconds` | `300` | Fallback inventory reload interval (saves trigger an immediate reload). |
| `Logging:*` | Information; ASP.NET Core and EF Core at Warning | Standard .NET log levels. Errors also go to the Windows Event Log. |

Data retention (raw samples, rollups, events, config versions) and the other runtime settings are
changed in the web UI under *Settings*, not in `appsettings.json`.

Example (domain groups; see section 3.5 for local groups):

```json
{
  "Yanmt": {
    "Https": { "Port": 8443, "CertificateThumbprint": "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678" },
    "Roles": { "AdminGroups": [ "CONTOSO\\YANMT-Admins" ], "ViewerGroups": [ "CONTOSO\\YANMT-Viewers" ] }
  }
}
```

## 6. Backup and restore

### What to back up

Everything is in the data folder (`$data`):

| Item | Notes |
| --- | --- |
| `yanmt.db` (+ `yanmt.db-wal`, `yanmt.db-shm`) | Inventory, maps, monitors, settings, history, audit log, encrypted credentials. |
| `secrets.entropy` | Extra DPAPI entropy for the stored credentials and config backups. **Without it neither can be decrypted.** Back it up with the database. |
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
# Re-apply the data folder ACL: a copied folder inherits the parent's permissions, not section 3.3's.
# No /T on the first line (it would leave every restored file with an empty DACL, see section 3.3).
icacls $data /inheritance:r /grant "${account}:(OI)(CI)M" 'BUILTIN\Administrators:(OI)(CI)F' 'NT AUTHORITY\SYSTEM:(OI)(CI)F'
icacls "$data\*" /reset /T /Q
icacls "$data\secrets.entropy" /inheritance:r /grant "${account}:M" 'BUILTIN\Administrators:F' 'NT AUTHORITY\SYSTEM:F'
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
- Install on the new server with the MSI (section 1, plus the certificate from section 3.2), or repeat
  section 3 (certificate, service account, groups, firewall) for a manual install.

## 7. Troubleshooting

### The service won't start

Windows only reports a generic error. The real reason is in YANMT's own logs:

- `$data\logs\startup-failure.log`: **every** fatal startup error, with the startup timings.
- `$data\logs\startup.log`: one block per start, written as each phase ends (runtime start, configuration,
  certificate, host build, migrations, credential key, web server listening). It shows how far a start got
  and where the time went.
- `$data\logs\yanmt-YYYYMMDD.log` (today's date): everything once logging is up, including a CRIT line for a
  failed start and a "YANMT started" line with the timings.

```powershell
Get-Content "$data\logs\startup-failure.log" -Tail 30 -ErrorAction SilentlyContinue
Get-Content "$data\logs\startup.log" -Tail 20 -ErrorAction SilentlyContinue
Get-Content "$data\logs\yanmt-$(Get-Date -Format yyyyMMdd).log" -Tail 50
```

What Windows reports:

- **Event 7034, "terminated unexpectedly"** (exit code 1): YANMT reached the service manager, then failed.
  Migrations, the starter library and the credential key run after the service is reported running, so
  they never hit the 30 s start timeout. Read `startup-failure.log`.
- **Error 1053 / event 7009 (timeout)**: the process didn't reach the service manager within 30 s, or failed
  before it did (configuration, data folder, certificate). The last line of `startup.log` shows how far it
  got. The first start as a new account can be slow because Windows creates the account's profile then; if
  `startup.log` shows a slow start and no error, start the service again.

| Message | Cause and fix |
| --- | --- |
| Error 1069, logon failure | The account lacks *Log on as a service*, or the password is wrong. See the end of section 3.6. |
| "Keyset does not exist" | The service account can't read the certificate's private key. Grant Read (section 3.3, *Certificate private key*) and verify. |
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

### "Cannot read ...secrets.entropy" / "Cannot create ...secrets.entropy"

The message names the account the service runs as and the exact `icacls` command to fix it. "Cannot read"
means the file exists but that account has no read access (it was created by an older YANMT version run by
hand as an admin, or restored or migrated with its old permissions). It also says whether anything is
encrypted with it (credential profiles **or config backup history**), and so whether deleting the file is safe:

- **No credential profiles and no config backups** (fresh install; the message says "Nothing is encrypted
  with it yet"): delete the file and start the service; it recreates
  it with the right ACL.

  ```powershell
  Remove-Item "$data\secrets.entropy"
  Start-Service YANMT
  ```

- **Credential profiles or config backups exist:** do **not** delete it, even if every credential profile has
  been deleted. Config history stays encrypted with this file; without it the history is lost for good and any
  stored credentials must be re-entered. Grant the service account read on it (the error message has this
  command with the real path and account), then start the service:

  ```powershell
  icacls "$data\secrets.entropy" /grant "${account}:R"
  icacls "$data\secrets.entropy"     # the service account must be listed with (R)
  Start-Service YANMT
  ```
