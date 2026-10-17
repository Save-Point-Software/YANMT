# Security policy

## Reporting a vulnerability

Please report security issues privately through GitHub's **"Report a vulnerability"** button
(Security → Advisories) on this repository, not in a public issue. Include the YANMT version or
commit, what you found, and how to reproduce it. I'll acknowledge within a week.

## Design commitments

- **Read-only device access.** YANMT never changes device configuration. SSH commands are limited
  to an explicit allowlist enforced before anything is sent, and tests fail if config-mode or write
  commands appear in the SSH layer. SNMP has no SET code path. A bypass of either is a security bug.
- **Credentials** are encrypted at rest with DPAPI (machine scope). They're never logged, never written
  to the audit log, never returned to the browser after saving, and never included in exports.
- **Authentication** is Windows Integrated Authentication only. YANMT stores no web passwords.
  Access requires membership in a configured admin or viewer group (AD or local).
- **HTTPS only.** There is no plain-HTTP listener.
- **Least privilege.** YANMT is meant to run as a dedicated low-privilege service account or gMSA,
  not LocalSystem.

## Scope

In scope: the YANMT application and its default configuration. Out of scope: vulnerabilities in
IOS-XE, Windows, or .NET themselves (report those to their vendors), and setups that ignore the
documented hardening (for example, running as LocalSystem).
