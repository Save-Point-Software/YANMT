# YANMT — Yet Another Network Manager Tool

A personal, open-source, **read-only** network monitoring web app for Cisco IOS-XE devices, built
natively for Windows Server. Think of it as a modern, web-based take on SNMPc: a hand-built map of
nodes whose colors come from ICMP, SNMPv3, and user-defined monitors, with drill-down into each node.

> **Read-only by design.** YANMT never changes device configuration, and never will. SSH is limited
> to an allowlist of `show` commands enforced in code and by tests; SNMP is GET/GETNEXT/GETBULK/WALK
> only, with no SET code path at all.

**Status:** under development, phase 1b of 6 (inventory, credential profiles, Test Connection, read-only guards). See
[`docs/SPEC.md`](docs/SPEC.md) for the full v1 scope.

## Requirements

- **Windows Server 2016 or later** (x64). Server 2012/2012 R2 work only with Extended Security
  Updates, per .NET 10's supported-OS list.
- Nothing else. YANMT is published as a self-contained single-file executable with the .NET runtime
  bundled. There's no IIS, database server, or runtime install.

## Configuration (so far)

Bootstrap settings live in `appsettings.json` next to `yanmt.exe`. It never contains secrets.

| Key | Default | Meaning |
|---|---|---|
| `Yanmt:DataPath` | `%ProgramData%\YANMT` | DB, config backups, map images, logs. A relative path is relative to the exe folder. |
| `Yanmt:Https:Port` | `8443` | HTTPS port (HTTPS only; there is no HTTP listener). |
| `Yanmt:Https:ListenAddress` | all interfaces | Bind address. |
| `Yanmt:Https:CertificateThumbprint` | — | **Required** in production. Certificate in the Windows store. |
| `Yanmt:Https:StoreName` / `StoreLocation` | `My` / `LocalMachine` | Store to load the certificate from. |
| `Yanmt:Roles:AdminGroups` | `[]` | Groups (or accounts) granted `admin`. |
| `Yanmt:Roles:ViewerGroups` | `[]` | Groups (or accounts) granted `viewer`. |
| `Yanmt:Logging:MaxFileSizeBytes` | 10 MB | Log file roll size. |
| `Yanmt:Logging:RetainedFileCount` | 30 | Log files kept. |

**Role groups** can be AD groups (`CONTOSO\YANMT-Admins`), local groups on the server
(`YANMT-Admins`, `.\YANMT-Admins`, or `SERVER01\YANMT-Admins`), or raw SIDs. Use local groups for
servers that aren't domain-joined; users then sign in with local accounts over NTLM. Names are
resolved to SIDs at startup, and anything that doesn't resolve is logged as an error. After changing
someone's group membership, they must sign out of Windows and back in to get a fresh token.

Operational settings (status colors now; intervals, thresholds, and retention in later phases) are
edited in the UI and stored in the database. Every change is audited.

### Credentials

SSH and SNMPv3 credentials live in reusable **credential profiles** (admin only, under
*Credentials*). Passwords are encrypted with DPAPI in machine scope plus extra entropy from
`<DataPath>\secrets.entropy`. That file is created on first start, readable only by the service
account, SYSTEM, and Administrators. Passwords are never shown again after saving, never logged, and
never written to the audit log. **Back up `secrets.entropy` together with `yanmt.db`.** Without it, or
on a different server, stored passwords can't be decrypted. The profile page says so, and you
re-enter them (a passphrase export/import arrives in phase 6).

- **SNMPv3:** authPriv only. **SHA-256/AES-128** is the verified pairing on IOS-XE and the default.
  SHA-1 is offered as a *legacy* option for devices without SHA-2 auth, and only with AES-128. There's
  no MD5 or DES. AES-192/256 are selectable but marked "not verified on IOS-XE" (see `docs/SPEC.md`,
  *SNMPv3 lab results*). YANMT verifies the HMAC of every response itself, because SharpSnmpLib doesn't.
- **SSH:** username and password for a plain privilege-15 local user. The SSH host key is pinned on the
  first successful connection (trust on first use). A later mismatch is refused until an admin trusts
  the new key from the node page.

Assign profiles on a node's edit page, or with the `snmp_profile` / `ssh_profile` CSV columns (profile
names; blank means none). The node page has **Test ICMP / SNMP / SSH** buttons that report a specific
cause: timeout, port unreachable, unknown user, wrong auth or privacy password, missing SNMP view,
unsupported SSH algorithm, host key changed, and so on.

Logs go to `<DataPath>\logs\yanmt-yyyyMMdd.log`. Service-level errors also go to the Windows
Application event log under source `YANMT`, once the installer registers that source.

## Developing

See [CONTRIBUTING.md](CONTRIBUTING.md) for dev-machine setup (SDK, gitleaks, hooks, dev certificate,
local role groups) and how to run and test.

The full Windows Server install guide and the custom-monitor guide arrive in later phases (see the spec).

## CML lab setup (so far)

Any IOS-XE 17.x node reachable from the YANMT host works. Minimal config:

```
! SSH for YANMT: a plain privilege-15 local user, no parser view and no enable.
! YANMT's command allowlist is the read-only enforcement.
ip domain name lab.example
crypto key generate rsa modulus 3072
ip ssh version 2
username yanmt privilege 15 algorithm-type scrypt secret <ssh-password>
line vty 0 4
 login local
 transport input ssh
!
! SNMPv3 authPriv with a read-only view (no write view on the group)
snmp-server view YANMT-RO iso included
snmp-server group YANMT-RO v3 priv read YANMT-RO
snmp-server user yanmt-ro-a128 YANMT-RO v3 auth sha-2 256 <auth-password> priv aes 128 <priv-password>
!
! Only on devices without SHA-2 auth (legacy):
snmp-server user yanmt-sha1 YANMT-RO v3 auth sha <auth-password> priv aes 128 <priv-password>
```

The SHA-2 keyword differs between IOS-XE releases. Check `snmp-server user x YANMT-RO v3 auth ?` on
your image. SNMPv3 passwords must be at least 8 characters. `snmp-server user` lines don't appear in
`show running-config`; check them with `show snmp user`.

To run the opt-in integration tests, copy `tests/lab.example.json` to `tests/lab.json` (gitignored),
fill in node addresses and the lab credentials, then run `dotnet test --project tests/Yanmt.Integration.Tests`.

Each node lists the SNMP profiles it's configured for in `snmpProfiles`: `sha256Aes128`,
`sha1Aes128`, or `sha256Aes256` (whose test is skipped in v1). A profile's tests run only against the
nodes that list it, and skip when none do. An unknown key, or one with no matching `snmp` entry, is
an error. SSH and ping tests run on every node.

## License

MIT. See [LICENSE](LICENSE).
