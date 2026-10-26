# YANMT — Yet Another Network Manager Tool

A personal, open-source, **read-only** network monitoring web app for Cisco IOS-XE devices, built
natively for Windows Server. Think of it as a modern, web-based take on SNMPc: a hand-built map of
nodes whose colors come from ICMP, SNMPv3, and user-defined monitors, with drill-down into each node.

> **Read-only by design.** YANMT never changes device configuration, and never will. SSH is limited
> to an allowlist of `show` commands enforced in code and by tests; SNMP is GET/GETNEXT/GETBULK/WALK
> only, with no SET code path at all.

**Status:** under development, phase 4 of 6 (inventory, credentials, ICMP, SNMPv3 default stats, custom monitors, the live map with link binding, and the node detail page). See
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

Operational settings (status colors and ICMP polling now; SNMP intervals, thresholds, and retention
in later phases) are edited in the UI and stored in the database. Every change is audited, and the
pollers pick up a change within a few seconds.

Bootstrap polling limits live in `appsettings.json` under `Yanmt:Polling`: `IcmpConcurrency`
(bursts in flight at once, default 32), `SnmpConcurrency` (default-stats polls in flight, default
16), and `ReloadSeconds` (how often targets are re-read even without a change, default 300).

### SNMP default stats

Every enabled node with SNMP on and an SNMPv3 profile is polled every 5 minutes (configurable), in
as few requests as possible:

- one GET for the system group;
- one GETBULK walk for CPU and the enhanced mempool table together;
- only if that table is empty, a walk of CISCO-MEMORY-POOL-MIB.

Engine discovery is cached per device, so a poll doesn't pay an extra round trip.

- **CPU:** `cpmCPUTotal5minRev`, the busiest CPU if there are several. Warning at 80%, Critical at 90%.
- **Memory:** the `Processor` pool from CISCO-ENHANCED-MEMPOOL-MIB, using the 64-bit HC counters when
  filled in, otherwise the 32-bit ones, otherwise CISCO-MEMORY-POOL-MIB. Warning at 80%, Critical at 90%.
- Both thresholds can be changed globally (Settings) or per node (the node's edit page). There's no
  dampening: the CPU figure is already a 5-minute average.
- **Uptime and reboots:** a reboot is detected when snmpEngineBoots goes up, or when sysUpTime goes
  backwards without being near its 497-day wrap. It logs an event, and the node shows **Warning
  until the next poll**.
- **Failed polls:** the Uptime source goes Unknown with one "SNMP poll failed" event; CPU and memory
  go Unknown quietly. Recovery logs one "recovered" event.
- **Missing MIBs:** a device without the CPU or memory MIB (or a view that hides it) simply has no
  CPU or memory source, rather than sitting gray.
- **System info** (sysDescr, sysName, sysLocation, sysContact) is shown on the node page.

Nothing else is hard-coded: interface, BGP, tunnel, and other monitoring comes from custom monitors.

### Node page

Click a node on the map or in the list. Its page has these tabs:

- **Overview:** state, IP, uptime, last reboot, system info, every status source, and charts. The
  charts are hand-built SVG: ICMP latency and loss, CPU, and memory over 1 h, 6 h, 24 h, or 7 d, with
  each bucket's min/max band, threshold lines, and gaps where polls were missed.
- **Interfaces:** walks IF-MIB live when you open it (never polled continuously). It shows name,
  description, alias, admin/oper status, speed, and current in/out rates and utilization from two
  counter samples 5 s apart. 64-bit counters are used where available.
- **SNMP query:** GET, GETNEXT, or WALK any OID. Accepts numeric OIDs or names from the built-in
  table, such as `ifDescr.3` or `IF-MIB::ifHCInOctets`. A WALK stops at 2,000 rows. Read-only:
  YANMT has no SET.
- **Events** for the node.
- **Config backups** (phase 5).

Viewers and admins can both use the Interfaces and SNMP query tabs, since both are read-only.
Admins also get **Monitor this** on both tabs.

## Custom monitors

A custom monitor polls any OID and turns its value into a state. Create one under **Monitors**
(admins). The same monitor is also the template: apply it to as many nodes, rows, or tags as you
like, and editing it changes all of them.

### What to poll

- **Scalar** OIDs include their instance, e.g. `sysUpTime.0`.
- **Table columns** are written without an index, e.g. `ifOperStatus`. When you apply one, you
  choose a single row (e.g. interface 3) or every row.
- Names from the built-in table work anywhere an OID is asked for, and are stored as numbers.
- A **row label column** (e.g. `ifDescr`, `ifAlias`, `ifName`) names each row. It's used by the
  picker and in state names, and to notice renumbering. If the row at a picked index later carries a
  different label, the instance goes Warning ("instance changed: was Tunnel0, now Gi4") until you
  re-pick it.

### Value

- **Raw** uses the value as polled; **rate per second** uses the change between polls of a
  counter. The rate handles Counter32 wrap, treats a Counter64 going down as a reset, and uses the
  agent's sysUpTime for timing. The first poll of a rate is Unknown ("waiting for a second sample").
- **Multiplied by** scales the value (e.g. 8 for octets to bits).
- **As a percentage of** a second OID (read from the same row) times its own multiplier, e.g.
  interface utilization = rate(ifHCInOctets) × 8 ÷ (ifHighSpeed × 1000000) × 100.
- **Value map:** `1=up, 2=down` shows labels instead of numbers.

### State rules

- Rules are ordered, and the first match wins. Operators: `=`, `≠`, `>`, `≥`, `<`, `≤`,
  `between`, `contains`, `regex`.
- Numeric operators compare the number. `=` and `≠` also accept a mapped label (`= established`).
- `contains` and `regex` match the mapped label, or the raw text when there's no map.
- When no rule matches, the default state applies. No value at all (missing object, first rate
  sample) is always Unknown.
- **Consecutive matches** dampens flapping: a new state must hold for that many polls in a row.
- **Interval** overrides the SNMP default for this monitor. **Log state changes** controls events.

### Applying a monitor

- **Node:** one node, optionally at one picked row ("Pick…" walks the label column on that node).
- **Tag:** every node carrying the tag, now or later (live membership). Usually with every row.
- **Row filters** (table monitors applied to every row): keep rows where a column passes a test
  (e.g. `ifAdminStatus = 1`), and/or whose label matches a regex (e.g. `^(Gi|Te)`, or `^(?!Null)` to
  skip Null0). A row you picked is always polled.
- **Test on node** in the editor reads the OID now with your unsaved settings, and shows each
  row's value and the rule it matches. Rows the filters would skip are shown greyed.
- **Removing** an assignment, or untagging a node, deletes that monitor's states and history there.
  Disabling a monitor or a node only switches the states off.

Each node's due monitors are read together: picked rows and scalars in batched GETs, and every
all-rows column in one GETBULK walk. Monitors with the same interval on a node share a poll.

### Starter library (shipped disabled)

| Monitor | What it does |
|---|---|
| Interface oper status | `ifOperStatus` of admin-up interfaces. Down is Critical. |
| Interface utilization in % / out % | Rate of `ifHC{In,Out}Octets` × 8 as a % of `ifHighSpeed` × 10⁶. Warning 75%, Critical 90%. |
| Interface input errors per second | Rate of `ifInErrors`. Any errors are Warning; 10/s or more is Critical. |
| BGP peer state | `bgpPeerState` of admin-started peers. Anything but established is Critical. |
| CPU 1-minute average | `cpmCPUTotal1minRev` every 60 s. Warning 80%, Critical 90%. |

The interface examples skip Null interfaces. Enable one and apply it, or **Copy** it and change
the copy. They're seeded once, so a deleted example stays deleted.

### Examples

- **Tunnel up:** `ifOperStatus` (table), label `ifDescr`, map `1=up, 2=down`, rules `= up -> Normal`,
  default Critical. Apply it to the node and pick `Tunnel0`.
- **Interface utilization on a link:** apply "Interface utilization in %" to the hub's uplink. Then,
  on the map, select the link and choose it under **Color from monitor**, and tick **show its value**.
- **Errors anywhere in the core:** enable "Interface input errors per second" and apply it to the
  tag `core`.

### Map links

In edit mode, select a link to bind it to a monitor instance on either end node. The line then
takes that instance's state color; unbound links stay neutral gray. It can also show the current
value next to its label.

### ICMP polling

Every enabled node with ICMP on gets a burst of echoes each interval (defaults: 4 echoes, 2 s
timeout, every 60 s).

- **Warning:** loss at or above the loss threshold (default 50%), or average latency at or above the
  latency threshold (default 150 ms). This applies straight away.
- **No reply at all:** Warning first, then **Critical** after the configured number of consecutive
  failed polls (default 3), so one lost burst doesn't flap the map. Any reply clears the count, and
  recovery is immediate.
- **Unknown:** no data yet, or the poll itself failed on the YANMT host.

State changes go to the event log; the event UI arrives in phase 6, and each node's page shows its
recent events. Samples are stored for the charts in phase 3. Retention cleanup also arrives in
phase 6, so until then `IcmpSamples` grows by about 288,000 rows a day per 200 nodes.

### Map

`/` shows the home map (the oldest top-level map, created as "Network" on first visit). Viewers can
pan (drag), zoom (wheel or +/−), and click: a node opens its page, and a submap opens that map. Admins
choose **Edit map** to:

- place nodes and submaps (new or existing maps), drag them, and draw links between icons;
- choose a value to show under a node's name (ICMP latency for now);
- upload a PNG, JPEG, or GIF background (up to 10 MB). The canvas takes the image's size. SVG isn't
  accepted, because it's served from YANMT's own origin and could carry script.

A node's color is the worst state of its enabled sources; a disabled node is gray. A submap shows the
worst state of the enabled nodes inside it, recursively. Disabled nodes don't count, so an
intentionally unmonitored box doesn't gray out a site. States update live, with no refresh. Map
structure edits by another admin show up on the next page load.

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
