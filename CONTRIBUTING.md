# Contributing to YANMT

Read [CLAUDE.md](CLAUDE.md) first. It lists the rules that aren't negotiable: read-only device
access, minimal server footprint, the package allowlist, and secrets handling.

## Dev machine setup (Windows, PowerShell)

```powershell
# 1. .NET 10 SDK (global.json pins the 10.0 band; any 10.0.1xx+ SDK works)
winget install --id Microsoft.DotNet.SDK.10

# 2. gitleaks, used by the pre-commit hook and CI
winget install --id Gitleaks.Gitleaks
gitleaks version

# 3. Enable the repo's git hooks (once per clone)
git config core.hooksPath .githooks

# 4. Repo-local tools (dotnet-ef)
dotnet tool restore
```

### HTTPS certificate for local runs

The default is the ASP.NET Core dev certificate. It's used when `Yanmt:Https:CertificateThumbprint`
is empty in `appsettings.Development.json`:

```powershell
dotnet dev-certs https --trust
```

To exercise the production code path (a certificate loaded from the store by thumbprint), create
a self-signed cert in your user store and put its thumbprint in `appsettings.Development.json`:

```powershell
$cert = New-SelfSignedCertificate -DnsName localhost, $env:COMPUTERNAME -CertStoreLocation Cert:\CurrentUser\My `
    -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(2) -FriendlyName "YANMT dev"
$cert.Thumbprint
```

The Development config already sets `StoreLocation` to `CurrentUser`. Trust the cert (copy it to
`Cert:\CurrentUser\Root`) if you want the browser to stop warning.

### Local role groups for testing

Development maps `.\YANMT-Admins` → admin and `.\YANMT-Viewers` → viewer. From an elevated PowerShell:

```powershell
New-LocalGroup -Name YANMT-Admins  -Description "YANMT administrators"
New-LocalGroup -Name YANMT-Viewers -Description "YANMT read-only users"
Add-LocalGroupMember -Group YANMT-Admins -Member $env:USERNAME

# Optional: a second local account to test viewer-only access
$pw = Read-Host -AsSecureString "Password for yanmt-viewer"
New-LocalUser -Name yanmt-viewer -Password $pw -FullName "YANMT test viewer"
Add-LocalGroupMember -Group YANMT-Viewers -Member yanmt-viewer
```

**Sign out of Windows and back in** after adding yourself to a group. Group membership is read from
your logon token.

## Build, run, test

```powershell
dotnet build
dotnet run --project src/Yanmt.Web          # https://localhost:7443, data in src\Yanmt.Web\data
dotnet test                                  # unit tests; CML integration tests skip without tests/lab.json
```

Browsers sign in to `localhost` automatically with Windows auth. Edge and Chrome treat it as
intranet; in Firefox, set `network.negotiate-auth.trusted-uris` / `network.automatic-ntlm-auth.trusted-uris` to `localhost`.

Integration tests (CML lab, opt-in):

```powershell
Copy-Item tests\lab.example.json tests\lab.json   # gitignored; fill in your lab addresses
dotnet test --project tests/Yanmt.Integration.Tests
```

To run only the unit tests while a `lab.json` exists:
`dotnet test --filter-not-trait "Category=Integration" --ignore-exit-code 8`. Exit code 8 means
"zero tests ran", which the filtered-out integration assembly reports.

After changing EF entities, add a migration:

```powershell
dotnet ef migrations add <Name> -p src/Yanmt.Infrastructure -s src/Yanmt.Infrastructure -o Data/Migrations
```

## Test data hygiene

- Use RFC 5737 addresses (`192.0.2.0/24`, `198.51.100.0/24`, `203.0.113.0/24`) and made-up hostnames.
- Never commit real device configs, credentials, SNMP users, or keys. Recorded SNMP fixtures and
  sample configs must be sanitized.
- Don't bypass the pre-commit hook (`--no-verify`). If gitleaks flags a false positive in a
  fixture, add a narrowly scoped allowlist entry and explain it in the PR.

## Packages

Ask before adding any NuGet package not already in `Directory.Packages.props`. Every package
ends up in the shipped single-file binary.
