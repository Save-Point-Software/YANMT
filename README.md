# YANMT — Yet Another Network Manager Tool

A personal, open-source, **read-only** network monitoring web app for Cisco IOS-XE devices, built
natively for Windows Server. Think of it as a modern, web-based take on SNMPc: a hand-built map of
nodes whose colors come from ICMP, SNMPv3, and user-defined monitors, with drill-down into each node.

> **Read-only by design.** YANMT never changes device configuration, and never will. SSH is limited
> to an allowlist of `show` commands enforced in code and by tests; SNMP is GET/GETNEXT/GETBULK/WALK
> only, with no SET code path at all.

**Status:** under development, phase 1a of 6 (skeleton, hosting, auth, inventory). See
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

Logs go to `<DataPath>\logs\yanmt-yyyyMMdd.log`. Service-level errors also go to the Windows
Application event log under source `YANMT`, once the installer registers that source.

## Developing

See [CONTRIBUTING.md](CONTRIBUTING.md) for dev-machine setup (SDK, gitleaks, hooks, dev certificate,
local role groups) and how to run and test.

The full Windows Server install guide, the custom-monitor guide, and the CML lab setup arrive in
later phases (see the spec).

## License

MIT. See [LICENSE](LICENSE).
