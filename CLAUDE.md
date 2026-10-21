# CLAUDE.md — rules for working on YANMT

YANMT is a read-only, SNMPc-style network monitor for Cisco IOS-XE, delivered as a self-contained
Windows service (Kestrel + Blazor Server + SQLite). The full spec is `docs/SPEC.md`; read it before
non-trivial work. The owner is a CCNP-level network engineer: don't explain networking.

## Non-negotiable rules

### 1. Read-only, permanently
- YANMT must **never** change device configuration. That's a design rule, not a v1 limitation.
- **SSH:** the device account is a plain **privilege-15 local user** (no parser view, no `enable`),
  so the device does not protect us; the app does.
  - All SSH goes through one collector class that only sends allowlisted commands:
    `show running-config`, `show version`, `terminal length 0`, `terminal width <n>`.
    Anything else throws **before** it is sent.
  - No code may enter config mode or write memory. `tests/Yanmt.Infrastructure.Tests/ReadOnly/ReadOnlyScanTests.cs`
    fails if `configure`, `conf t`, `write`, or `copy` strings appear in the SSH layer, if any SSH.NET type
    is used outside `ReadOnlySshCollector`, or if a command is sent anywhere but its allowlist-checked
    `SendAsync`. Never weaken the allowlist or those tests.
  - SSH host keys are pinned per node (trust on first use); a mismatch is refused, never auto-accepted.
- **SNMP:** GET / GETNEXT / GETBULK / WALK only. No SET code path anywhere, including the ad-hoc
  query tool. Don't reference SharpSnmpLib's set APIs. SharpSnmpLib is used only under
  `Collectors/Snmp`, behind `ISnmpReader`.
  - SharpSnmpLib does not verify response HMACs. Every SNMPv3 response must pass
    `UsmAuthentication.Verify` (and the authPriv/user/engine checks in `SnmpV3Reader`) before use.
  - SharpSnmpLib caches localized keys process-wide and returns the cached array. Never modify a
    byte array it returns (zeroing one broke every later request with that password and engine).
  - Supported crypto: **SHA-256/AES-128** is the verified v1 pairing and the default. **SHA-1**
    (HMAC-SHA-96) is a legacy option, AES-128 only, flagged "legacy" in the UI. SHA-384/512 and
    AES-192/256 exist, but AES-192/256 are flagged "not verified on IOS-XE". **Never add MD5 or DES.**
    Lab results and the AES-256 decision are in `docs/SPEC.md` (*SNMPv3 lab results*).

### 2. Minimal server footprint
- Nothing installed on the server but YANMT: no .NET runtime install, IIS, DB server, Python,
  Node, NSSM/WinSW. Publish is self-contained single-file win-x64, untrimmed.
- Built-in .NET/Windows features first. No CDN links (the server may have no internet).
- Charts and the map are hand-built SVG components; no JS/chart libraries, no front-end build step.

### 3. Package allowlist — ask before adding anything else
Runtime: `Microsoft.EntityFrameworkCore.Sqlite`, `Lextm.SharpSnmpLib`, `SSH.NET`, `DiffPlex`,
`System.Security.Cryptography.ProtectedData`, `Microsoft.Extensions.Hosting.WindowsServices`,
`Microsoft.AspNetCore.Authentication.Negotiate`.
Dev/test only: `Microsoft.EntityFrameworkCore.Design` (PrivateAssets=all), `xunit.v3`, and the
repo-local `dotnet-ef` tool. Versions are pinned centrally in `Directory.Packages.props`.

### 4. No hard-coded monitoring beyond the defaults
Default SNMP stats are only uptime, CPU, memory, and system info. Interface, BGP, tunnel, and
everything else comes from user-defined custom monitors (the starter library ships disabled).

### 5. Secrets
- Device credentials are encrypted with DPAPI (machine scope) in the DB. Never log them, never put
  them in the audit log (mark properties `[Secret]`), never send them back to the browser after saving.
- `appsettings.json` holds no secrets. CSV exports never include secrets.
- Test data: RFC 5737 addresses only (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24) and made-up
  hostnames. Never commit real configs, credentials, or keys. gitleaks runs in CI and as a pre-commit hook.

## How the code is laid out
- `src/Yanmt.Core` — pure logic, **no I/O and no package references** (status model, rollups,
  CSV, validation, later rules/rate math/normalization/allowlist). Most unit tests target this.
- `src/Yanmt.Infrastructure` — EF Core (SQLite, WAL, migrations under `Data/Migrations`), audit
  interceptor, role mapping, file logger, services; later collectors and the polling engine.
- `src/Yanmt.Web` — host (`Program.cs`), Kestrel/HTTPS/Windows service/auth, Blazor components.
- Status severity is one scale everywhere: Normal, Warning, Critical, Unknown. Rollup rank:
  Critical > Warning > Unknown > Normal.

## Conventions
- Audited entities implement `IAudited` and must be saved with `SaveChangesAsync`.
- Use `IDbContextFactory<YanmtDbContext>` (scoped) — never inject a DbContext into a component.
- After changing entities: `dotnet tool restore` then
  `dotnet ef migrations add <Name> -p src/Yanmt.Infrastructure -s src/Yanmt.Infrastructure -o Data/Migrations`.
  The test `Model_has_no_changes_missing_from_migrations` catches a forgotten migration.
- Tests run on Microsoft.Testing.Platform (`global.json`): `dotnet test`. Integration tests carry
  `[Trait("Category","Integration")]` and skip unless `tests/lab.json` exists. SNMP lab tests are
  capability-aware: a profile runs only against nodes whose `snmpProfiles` list it.
- Work in phases (see `docs/SPEC.md`), and stop at the end of each phase for lab testing.
- When something touching device access or security is ambiguous, ask rather than guess.
