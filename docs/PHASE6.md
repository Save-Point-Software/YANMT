# Phase 6 wrap-up: events, retention, export, docs

For lab testing. Decisions are in `docs/SPEC.md` (*Phase 6 decisions*); install steps are in
[DEPLOYMENT.md](DEPLOYMENT.md); lab node config is in the README (*CML lab setup*).

## What was built

- **Events (6.1, 6.2):** `/events` page with node, state, and source filters; acknowledge and
  acknowledge-all-for-the-current-filter (admin-only, confirm prompt, audited); `INotifier` seam with an
  in-app implementation; unacknowledged-Critical badge in the layout, live via `StatusBroadcaster`;
  events CSV export (no secrets). The node Events tab uses the same query.
- **Hourly rollups (6.3):** min/avg/max per hour for metric and ICMP samples (new tables and migration),
  rolled up idempotently with catch-up after downtime.
- **Retention (6.4):** raw 7 d, rollups 90 d, events 365 d, config versions keep-all (optional
  keep-newest-N per node, latest never deleted); all configurable under *Settings*. Nightly cleanup about
  03:00 UTC, `PRAGMA optimize` every run, `VACUUM` only when free pages exceed 25%. "Run cleanup now"
  button; each run writes one audit entry with counts.
- **Credential export/import (6.5):** admin-only, credential profiles only, passphrase-encrypted
  (AES-GCM, PBKDF2-SHA256), audited, never logs secrets.
- **Docs (6.6, 6.7):** deployment guide, README, CONTRIBUTING, SECURITY.

## Commands (Windows, PowerShell, repo root)

Prerequisites are in [CONTRIBUTING.md](../CONTRIBUTING.md) (SDK, gitleaks, dev certificate, local groups).

```powershell
# Build
dotnet build

# Run (https://localhost:7443, data in src\Yanmt.Web\data)
dotnet run --project src/Yanmt.Web

# Unit and Windows-only tests (CML tests skip without tests\lab.json)
dotnet test

# CML integration tests (opt-in)
Copy-Item tests\lab.example.json tests\lab.json    # gitignored; fill in lab addresses and credentials
dotnet test --project tests/Yanmt.Integration.Tests

# Unit tests only, while a lab.json exists (exit code 8 = filtered-out assembly ran zero tests)
dotnet test --filter-not-trait "Category=Integration" --ignore-exit-code 8

# Publish (self-contained, single-file, win-x64, untrimmed)
dotnet publish src/Yanmt.Web -c Release -r win-x64 -o publish
```

## CML checklist

Lab prep: `write memory` on every node after configuring (SNMP users and RSA keys are lost on reboot
otherwise); confirm with `show snmp user`.

- [ ] `dotnet test --project tests/Yanmt.Integration.Tests` passes (SSH, ping, and each node's SNMP profiles).
- [ ] Add a lab node with credentials; ICMP, uptime, CPU, memory, and system info populate.
- [ ] Take a node down (shut its management interface): an ICMP event appears and the Critical badge
      increments live without a page reload; bring it back and the state returns to Normal.
- [ ] `/events`: filter by node, state, and source; acknowledge one event (admin) and confirm the badge
      drops; acknowledge-all shows the unacknowledged count and only changes the filtered set. A viewer
      sees no acknowledge buttons and the server rejects the request.
- [ ] Events CSV export opens in Excel, honors the filters, and contains no secrets.
- [ ] Reload a lab router: a reboot event appears. SSH host key stays pinned across the reboot, and
      *Forget host key* is needed only after regenerating the RSA key.
- [ ] Config backup runs; changing the config (for example a description) creates a new version and an event.
- [ ] *Settings > Data retention*: set raw retention to the minimum, leave the app running past an hour
      boundary, then click *Run cleanup now*. Raw samples older than the setting are gone, hourly rollups
      remain, the counts are shown, and the audit log has one entry with counts only.
- [ ] Charts for a node show rolled-up history beyond the raw window.
- [ ] Set keep-newest-N config versions to 2 with 3+ versions stored: cleanup deletes the oldest and keeps the latest.
- [ ] Credential export, then import on a clean database (same server): profiles are restored and
      polling works. A wrong passphrase and a tampered file are both refused.
- [ ] Publish and install the service per DEPLOYMENT.md on a test server: service starts under the
      service account, HTTPS serves the bound certificate, Windows groups map to admin and viewer.
- [ ] Backup and restore per DEPLOYMENT.md section 5, including the DPAPI caveat on a different server.

## Known follow-ups

- `/export/events.csv` ignores unknown filter values (for example `state=crit`); it should return 400.
