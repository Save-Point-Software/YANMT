# YANMT (Yet Another Network Manager Tool): Claude Code build prompt (v1)

## What you're building

YANMT is a personal, open-source, **read-only** network monitoring web app for Cisco IOS-XE devices, built natively for **Windows Server**. Think of it as a modern, web-based take on **SNMPc**: you see your network as a map of nodes, each node's status color comes from what's being polled, and you can click into a node to see and query more about it.

Version 1 does six things:

1. **Network map**: a hand-built map of nodes and links, with live status colors and drill-down
2. **ICMP** reachability and latency monitoring
3. **SNMPv3** polling: a small set of default stats on every device, plus **user-defined custom monitors**
4. **SSH** access to devices (used in v1 for config backups and connection tests)
5. **Config backups** with version history and diffs
6. **An event log** showing status changes across all of the above

It must **never** change device configuration. That's a permanent design rule, not a v1 limitation.

Before writing any code, read this whole document, then propose an implementation plan (solution layout, data model, background-service design, and phase breakdown). Wait for my approval. Build one phase at a time, and stop at the end of each phase so I can test against my lab.

## About me (calibrate to this)

- Senior network engineer (CCNP Enterprise). I know IOS-XE, BGP, GRE/DMVPN, SNMP, and firewalls well, so don't explain networking to me. I've used SNMPc and want YANMT to feel familiar to someone who has.
- I'm comfortable with .NET and Kestrel administration. Explain C# design choices briefly, but you don't need to explain hosting basics.

## The guiding constraint: minimal server footprint

**Nothing should need to be installed on the server except YANMT itself.** No .NET runtime install, no IIS, no database server, no Python, no service wrappers, no Node.

- Publish as a **self-contained, single-file, win-x64** executable (the .NET runtime is bundled). Deploying means copying a folder and registering a service.
- Use **built-in .NET / Windows capabilities first**. Only add a NuGet package when the framework truly lacks the feature. Every package gets bundled into the publish output, so nothing extra lands on the server. Still, keep the list short and ask me before adding any package not listed below.

## Stack

| Area | Decision |
|---|---|
| Runtime | **.NET 10 (LTS)**, C#. Confirm the minimum Windows Server version it supports and put that in the README. |
| Hosting | ASP.NET Core on **Kestrel**, run as a **Windows service** via `UseWindowsService()`. No IIS, no WinSW/NSSM. |
| HTTPS | Kestrel loads its certificate from the **Windows certificate store** (by thumbprint in config). |
| Web UI | **Blazor Server** (interactive server rendering), so the map and status update live over SignalR with no front-end build step. Build the map as an **SVG component** (pan, zoom, drag) and draw charts as hand-built SVG components rather than adding libraries. No CDN links; the server may have no internet access. |
| Web login | **Windows Integrated Authentication** (Negotiate/Kerberos, built into ASP.NET Core). Map two roles to AD groups set in config: `admin` and `viewer`. No LDAP library and no password handling in the app. |
| Database | **SQLite** via EF Core (`Microsoft.EntityFrameworkCore.Sqlite`), WAL mode, with migrations. It's an embedded file, so there's no database server. |
| ICMP | `System.Net.NetworkInformation.Ping` (built in, no admin rights needed) |
| SNMP | `Lextm.SharpSnmpLib`. Verify that the version you pick supports SNMPv3 **authPriv with SHA-2 auth and AES priv** before building on it, and tell me what you found. |
| SSH | `SSH.NET` (`Renci.SshNet`). Test against IOS-XE's SSH algorithm set early. Prefer the exec channel for single `show` commands, and fall back to a shell stream only if needed. |
| Diffs | `DiffPlex` |
| Secrets | DPAPI via `System.Security.Cryptography.ProtectedData`, using **machine scope**, for encrypting device credentials in the DB. Never log secrets or send them back to the browser after saving. |
| Logging | Built-in `ILogger` to rotating files plus the **Windows Event Log** for service-level errors |
| Scheduling | Built-in `BackgroundService` / `PeriodicTimer` hosted services. No Quartz or Hangfire. |
| Tests | xUnit, run in GitHub Actions on `windows-latest` |

## Read-only enforcement (important)

- All SSH interaction goes through a single collector class that only allows commands from an explicit allowlist (`show running-config`, `show version`, `terminal length 0`, `terminal width ...`). Anything else throws before it's sent.
- Nothing in the codebase may enter config mode or write memory. Add a test that fails if `configure`, `conf t`, `write`, or `copy` command strings appear in the SSH layer.
- SNMP is GET/GETNEXT/GETBULK/WALK only. No SET. Don't expose a SET code path at all, including in the ad-hoc query tool.

## Status model (shared by everything)

Every source of status (ICMP, default SNMP stats, each custom monitor, backups) produces a state using one severity scale: **Normal, Warning, Critical, Unknown** (Unknown means no data or poll failed). Each node's map color is the **worst** current state among its enabled monitors. Links take the state of the monitor bound to them, if one is set. Keep the colors configurable, with SNMPc-like defaults (green, yellow, red, gray).

## v1 features

### Inventory

- Nodes with hostname, management IP, site, tags, an icon type (router, switch, firewall, server, generic), and an enabled flag.
- Reusable **credential profiles**: SSH (username, password, optional enable secret) and SNMPv3 (user, SHA-2 auth, AES priv). These are encrypted with DPAPI.
- "Test connection" buttons for ICMP, SNMP, and SSH separately, with clear error messages (timeout, auth failure, unsupported algorithm, etc.).
- CSV import and export of nodes. Exports never include secrets.

### Network map

- **Manually built, like SNMPc.** No auto-discovery or auto-layout in v1. Admins drag nodes onto the canvas, position them, and draw links between them.
- **Submaps:** a map can contain submap nodes (for example, one per site). A submap's icon shows the worst state of everything inside it, and clicking it opens that submap.
- Optional background image per map (uploaded floor plan or region graphic), stored in the data folder.
- Node labels show hostname and, optionally, one chosen value (for example, current latency or CPU).
- Edit mode for admins, view-only for viewers. The map updates live as states change, with no refresh.
- **Click a node to drill down** (see the node detail view below).

### ICMP monitoring

- Ping every enabled node on a configurable interval (default 60s), sending a few echoes each cycle to record latency (min/avg/max) and packet loss.
- Feeds the status model: Normal, Warning (loss or latency above a threshold), or Critical (unreachable). Require a configurable number of consecutive failures before going Critical, so a single dropped packet doesn't flap the state.

### SNMPv3: default stats

Polled on every SNMP-enabled node with no setup. Default interval is 5 minutes, configurable.

- **Uptime** (`sysUpTime`), used to detect reboots
- **CPU utilization** (CISCO-PROCESS-MIB, 5-minute average)
- **Memory utilization** (CISCO-ENHANCED-MEMPOOL-MIB, falling back to CISCO-MEMORY-POOL-MIB)
- **System info** (`sysDescr`, `sysName`, `sysLocation`, `sysContact`) collected for display, not for status

CPU and memory have default Warning and Critical thresholds that can be changed globally or per node. **Nothing else is hard-coded.** Interface, BGP, tunnel, or any other monitoring comes from custom monitors.

### SNMPv3: custom monitors (the core feature)

Modeled on SNMPc's user-defined polling and trend expressions. An admin can define a monitor that polls any OID and turns its value into a state.

A **monitor definition** has:

- **Name and description**
- **OID**, which can be either:
  - a scalar (for example, `1.3.6.1.2.1.1.3.0`)
  - a table column with a specific instance (for example, `ifOperStatus.10001`, stored numerically)
  - a table column polled across all rows, producing one state per row (for example, every row of `ifOperStatus`)
- **Value handling:** use the raw value, or calculate **rate per second from deltas** for counters (handling 32/64-bit counter wrap and device reboots), or a **percentage** of a second OID (for example, used ÷ total)
- **Optional value map** that turns integers into labels (for example, BGP peer state `6 = established`, `ifOperStatus 1 = up, 2 = down`)
- **State rules,** evaluated in order, with the first match winning. Each rule pairs an operator (`=`, `!=`, `>`, `>=`, `<`, `<=`, `between`, `contains`, `regex`) and a value with a resulting state (Normal, Warning, or Critical). A default state applies when no rule matches.
- **Poll interval,** overriding the default if set
- **Consecutive matches before the state changes,** to dampen flapping
- **Event on state change,** on or off

How monitors get applied:

- **Monitor templates:** save a definition once (for example, "Interface oper status," "BGP peer established," "Tunnel up") and apply it to many nodes, or to every node with a given tag.
- **Per-node instances:** apply a monitor to one node and pick the instance (for example, a specific `ifIndex`) from a list the app fetches live by walking the table. Show readable labels like `ifDescr` or `ifAlias` so I'm not picking raw indexes.
- **Bind to a link:** a monitor on an interface can be attached to a map link so the link shows that interface's state.
- **Ship a small starter library** of disabled example templates (interface oper status, interface utilization percentage, interface errors per second, BGP4-MIB peer state, CPU) that I can enable or copy. These are examples, not hard-coded behavior.

OID names:

- In v1, monitors store **numeric OIDs**. Include a built-in name table for common objects (SNMPv2-MIB, IF-MIB, BGP4-MIB, the Cisco CPU and memory MIBs) so the UI can show names next to numbers and accept names when typing.
- Importing MIB files is out of scope for v1, but design the name lookup so a MIB importer can be added later.

### Node detail view (the drill-down)

Opens when a node is clicked on the map or in a list. It shows:

- **Header:** current state, IP, system info, and uptime
- **ICMP:** latency and loss charts
- **Default stats:** CPU and memory charts
- **Custom monitors** on this node, with current value, state, and a small trend chart for each
- **Interfaces tab:** fetched **live on demand** by walking IF-MIB when opened, not polled continuously. It shows name, alias, admin/oper status, speed, and current rates (from two quick samples). Each row has a "Monitor this" action that creates a custom monitor for that interface from a template.
- **SNMP query tool (read-only):** GET, GETNEXT, or WALK any OID on this node and see the results in a table. Results can be turned into a custom monitor with one click. This replaces SNMPc's MIB browser for v1.
- **Config backups:** version history and diffs for this node
- **Events:** recent events for this node

### Config backups (SSH)

- Pull `show running-config` on a schedule (default daily) and on demand.
- Normalize before comparing: strip volatile lines such as `! Last configuration change`, `! NVRAM config last updated`, `ntp clock-period`, and the `Building configuration...` / `Current configuration : N bytes` header, plus a configurable regex list for others.
- Store a new version only when the normalized config has changed. Keep full version history per node.
- Diff view between any two versions, both side-by-side and unified.
- Mask secret lines (`password`, `secret`, `key`, and SNMP `auth`/`priv`) in the UI for viewers. Admins can reveal them.
- Download a single version, or a node's full history as a zip.
- A failed backup sets that source to Warning, and a config change creates an event.

### Events (in-app only)

- One event log covering every state change: ICMP, default stats, custom monitors, reboots, backups, and config changes.
- Show a banner or badge for unacknowledged Critical events. Events can be acknowledged, filtered by node, state, and source, and exported to CSV.
- Route them all through one internal notifier interface so email or webhooks can be added later.

### Data retention

- Raw samples kept 7 days, hourly rollups (min/avg/max) kept 90 days, both configurable.
- Run a nightly cleanup and SQLite `VACUUM`/`optimize`.

## Non-functional requirements

- **Concurrency:** poll up to 200 nodes with bounded parallelism (configurable limit, per-node timeouts and retries). Group a node's due OIDs into as few SNMP requests as possible (GETBULK for tables). One slow or dead node must never delay the others, and a poller failure must never crash the web app.
- **Map performance:** the map should stay smooth with 200 nodes and a few hundred links. Push only state changes over SignalR, not full redraws.
- **Run as a dedicated low-privilege service account or gMSA,** not LocalSystem. Document the exact rights it needs: log on as a service, read/write on the data folder, and read access to the certificate's private key.
- **Audit log** of admin changes: nodes, maps, monitors, credentials, and settings.
- **Defender:** document recommended real-time-scanning exclusions for the data folder (SQLite DB and backups).
- **Testing:**
  - Unit tests run with no lab, using recorded SNMP responses and config files as fixtures. They cover counter-wrap and rate math, percentage calculations, state-rule evaluation and dampening, worst-state rollup for nodes and submaps, config normalization, secret masking, and the read-only allowlist.
  - An opt-in integration suite targets my **Cisco CML** lab through a config file.
- **Test data hygiene:** use RFC 5737 IPs (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24) and made-up hostnames everywhere. Never commit real configs, credentials, or keys. Add gitleaks to CI and as a pre-commit hook.

## Deployment (document all of this in PowerShell)

- `dotnet publish` command for the self-contained single-file build
- Install: copy the folder, create the data directory with correct ACLs, register the service with `sc.exe` or `New-Service`, set the service account, bind the certificate, and open the firewall port
- Upgrade (stop the service, swap the binaries, start the service; migrations apply automatically on startup) and uninstall
- Config lives in `appsettings.json` next to the exe, with no secrets in it
- Backing up YANMT: what to copy (DB, backup folder, map images) and how the DPAPI machine-scope key affects restoring to a different server. Credentials won't decrypt on a new machine, so document re-entering them, or provide an admin export that re-encrypts with a passphrase.

## Repo and open-source setup

- Repo name `yanmt`, MIT license
- README:
  - What it is, plus a clear "read-only by design" statement
  - Windows Server install guide
  - Configuration reference
  - A guide to writing custom monitors, with examples
  - CML lab setup I can reproduce: a minimal topology and the IOS-XE snippets needed for SSH, SNMPv3 authPriv with a read-only view, and a low-privilege local user for YANMT
- `CONTRIBUTING.md`, `SECURITY.md`, and a `.gitignore` that excludes the DB, backups, logs, map images, and lab config files
- A `CLAUDE.md` at the repo root summarizing these rules (read-only enforcement, the minimal-footprint rule, the package allowlist, no hard-coded monitoring beyond the defaults, and secrets handling) so future sessions follow them

## Out of scope for v1 (leave clean seams; don't build)

- Pushing config of any kind, and SNMP SET
- Auto-discovery (CDP/LLDP/subnet scans) and auto-layout of maps
- MIB file import and compilation
- SNMP traps and syslog receiving
- CLI parsing of `show` output
- NETCONF/RESTCONF
- Non-IOS-XE platforms (custom monitors should work on any SNMPv3 device anyway, but only test IOS-XE)
- Email, webhook, or Teams alerting
- PostgreSQL or SQL Server support

## How I want you to work

1. Propose the plan and phase breakdown first, and wait for my approval. A suggested order:
   - Phase 1: skeleton, service hosting, auth, inventory, DPAPI credentials, and the status model
   - Phase 2: ICMP and the map (build, edit, live status, submaps)
   - Phase 3: SNMPv3 default stats and the node detail view, including the interfaces tab and SNMP query tool
   - Phase 4: custom monitors, templates, link binding, and the starter library
   - Phase 5: SSH config backups and diffs
   - Phase 6: events, retention, and deployment docs
2. At the end of each phase, give me: what was built, the exact commands to build, run, and test it on Windows, and what to check in CML.
3. Ask me before adding any NuGet package not listed in the stack table.
4. When something is ambiguous, ask rather than guess, especially anything touching device access or security.
