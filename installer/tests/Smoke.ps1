<#
.SYNOPSIS
  MSI smoke test for a throwaway Windows machine (GitHub Actions windows-latest). Issue #40.

.DESCRIPTION
  1. A manual install as the local account svc-yanmt (DEPLOYMENT.md section 2), started once so
     yanmt.db and secrets.entropy exist.
  2. An MSI install with a bad thumbprint: must fail early and change nothing.
  3. MSI install (version 1) over the manual install: replaces the service with NT SERVICE\YANMT,
     re-grants folders and the key, creates the local groups, keeps the hand-edited appsettings.json.
  4. Upgrade to version 2 with no properties: settings read back from the registry, database
     backed up, hand-edited appsettings.json kept.
  5. Version 1 again: refused (downgrade).
  6. Uninstall: service, firewall rule, event source, install folder and registry key gone; data kept.

  It creates a self-signed certificate, a local user and local groups, and changes the firewall.
  Never run it on a real server. No YANMT polling is configured and no device is contacted.
#>
#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory)] [string] $Msi1,
    [Parameter(Mandatory)] [string] $Msi2,
    [Parameter(Mandatory)] [string] $PublishDir,
    [string] $LogDir = (Join-Path $PWD 'smoke-logs')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Msi1 = (Resolve-Path $Msi1).Path
$Msi2 = (Resolve-Path $Msi2).Path
$PublishDir = (Resolve-Path $PublishDir).Path
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

$app = Join-Path $env:ProgramFiles 'YANMT'
$data = Join-Path $env:ProgramData 'YANMT'
$port = 8443
$remote = '192.0.2.0/24'   # RFC 5737 documentation range
$serviceSid = 'S-1-5-80-494844833-2242255151-845590027-1396523193-2268386581'
$failures = [System.Collections.Generic.List[string]]::new()

function Check([bool] $ok, [string] $what) {
    if ($ok) { Write-Host "PASS  $what" } else { Write-Host "FAIL  $what" -ForegroundColor Red; $failures.Add($what) }
}

function Step([string] $name) { Write-Host "`n=== $name ===" -ForegroundColor Cyan }

# Start-Process joins arguments with spaces and quotes nothing, so values are quoted here: NAME="value".
function Invoke-Msi([string] $log, [string[]] $arguments) {
    $quoted = foreach ($a in $arguments) {
        if ($a -match '^([A-Z_]+)=(.*)$') { "$($Matches[1])=`"$($Matches[2])`"" } elseif ($a -match '^/') { $a } else { "`"$a`"" }
    }
    $line = (@($quoted) + @('/qn', '/norestart', '/l*v', "`"$(Join-Path $LogDir $log)`"")) -join ' '
    $p = Start-Process msiexec.exe -ArgumentList $line -Wait -PassThru
    Write-Host "msiexec $line -> $($p.ExitCode)"
    return $p.ExitCode
}

function Wait-Until([scriptblock] $condition, [int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return $true }
        Start-Sleep -Seconds 2
    }
    return [bool](& $condition)
}

function Get-Service-Info { Get-CimInstance Win32_Service -Filter "Name='YANMT'" }

function Get-HttpStatus {
    # Negotiate answers an anonymous request with 401. -k: the certificate is self-signed.
    $code = & curl.exe -k -s -o NUL -w '%{http_code}' --max-time 10 "https://localhost:$port/"
    return "$code"
}

function Get-KeyDescriptor($cert) {
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
    $key = ([System.Security.Cryptography.RSACng]$rsa).Key
    $value = $key.GetProperty('Security Descr', [System.Security.Cryptography.CngPropertyOptions]4).GetValue()
    return @{ Key = $key; Descriptor = [System.Security.AccessControl.RawSecurityDescriptor]::new($value, 0) }
}

function Test-KeyRead($cert, [string] $sid) {
    $sd = (Get-KeyDescriptor $cert).Descriptor
    foreach ($ace in $sd.DiscretionaryAcl) {
        if ($ace.SecurityIdentifier.Value -eq $sid -and $ace.AceQualifier -eq 'AccessAllowed' -and ($ace.AccessMask -band 0x80000000)) {
            return $true
        }
    }
    return $false
}

function Grant-KeyRead($cert, [string] $sid) {
    $k = Get-KeyDescriptor $cert
    $sd = $k.Descriptor
    $ace = [System.Security.AccessControl.CommonAce]::new('None', 'AccessAllowed', [int]0x80000000,
        [System.Security.Principal.SecurityIdentifier]::new($sid), $false, $null)
    $sd.DiscretionaryAcl.InsertAce($sd.DiscretionaryAcl.Count, $ace)
    $bytes = [byte[]]::new($sd.BinaryLength)
    $sd.GetBinaryForm($bytes, 0)
    $options = [System.Security.Cryptography.CngPropertyOptions]([int]::MinValue -bor 4)   # Persist | DACL
    $k.Key.SetProperty([System.Security.Cryptography.CngProperty]::new('Security Descr', $bytes, $options))
}

function Get-AclSids([string] $path) {
    (Get-Acl -LiteralPath $path).GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]) |
        ForEach-Object { $_.IdentityReference.Value }
}

function Get-Rule { Get-NetFirewallRule -DisplayName 'YANMT web UI' -ErrorAction SilentlyContinue }

function Get-Settings { Get-ItemProperty -Path 'HKLM:\SOFTWARE\YANMT' -ErrorAction SilentlyContinue }

function Get-Products {
    @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'YANMT' })
}

function Set-Marker([string] $value) {
    $path = Join-Path $app 'appsettings.json'
    $json = Get-Content $path -Raw | ConvertFrom-Json
    $json.Yanmt.Logging.RetainedFileCount = [int]$value
    $json | ConvertTo-Json -Depth 10 | Set-Content $path -Encoding utf8
}

function Get-Marker { (Get-Content (Join-Path $app 'appsettings.json') -Raw | ConvertFrom-Json).Yanmt.Logging.RetainedFileCount }

function Show-Logs {
    Write-Host '--- Service Control Manager events'
    Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; StartTime = (Get-Date).AddMinutes(-30) } -MaxEvents 8 -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host "$($_.TimeCreated) $($_.Id): $($_.Message)" }
    foreach ($f in 'startup-failure.log', 'startup.log') {
        $p = Join-Path $data "logs\$f"
        if (Test-Path $p) { Write-Host "--- $p"; Get-Content $p -Tail 40 }
    }
}

try {
    # --------------------------------------------------------------------------------------------
    Step '1. Manual install as svc-yanmt'
    $cert = New-SelfSignedCertificate -DnsName 'localhost', $env:COMPUTERNAME -CertStoreLocation Cert:\LocalMachine\My
    $thumb = $cert.Thumbprint
    Write-Host "Certificate $thumb"

    $password = ConvertTo-SecureString ("Aa1!" + [Guid]::NewGuid().ToString('N')) -AsPlainText -Force
    $user = New-LocalUser -Name 'svc-yanmt' -Password $password -PasswordNeverExpires -Description 'YANMT smoke test'
    $account = "$env:COMPUTERNAME\svc-yanmt"
    $userSid = $user.SID.Value

    # Log on as a service, as an admin would in Local Security Policy.
    $inf = Join-Path $env:TEMP 'yanmt-rights.inf'
    secedit /export /cfg $inf /areas USER_RIGHTS | Out-Null
    $lines = [System.Collections.Generic.List[string]](Get-Content $inf)
    $i = -1
    for ($n = 0; $n -lt $lines.Count; $n++) { if ($lines[$n] -like 'SeServiceLogonRight*') { $i = $n } }
    if ($i -ge 0) { $lines[$i] = $lines[$i] + ",*$userSid" } else { $lines.Insert($lines.IndexOf('[Privilege Rights]') + 1, "SeServiceLogonRight = *$userSid") }
    Set-Content $inf $lines -Encoding Unicode
    secedit /configure /db (Join-Path $env:TEMP 'yanmt-rights.sdb') /cfg $inf /areas USER_RIGHTS | Select-Object -Last 2
    secedit /export /cfg $inf /areas USER_RIGHTS | Out-Null
    Write-Host "Log on as a service: $((Get-Content $inf) -like 'SeServiceLogonRight*')  (svc-yanmt is *$userSid)"

    New-Item -ItemType Directory -Force -Path $app, $data | Out-Null
    Copy-Item (Join-Path $PublishDir '*') -Destination $app -Recurse -Force

    $settings = Get-Content (Join-Path $app 'appsettings.json') -Raw | ConvertFrom-Json
    $settings.Yanmt.Https.CertificateThumbprint = $thumb
    $settings.Yanmt.Roles.AdminGroups = @('.\YANMT-Admins')
    $settings | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $app 'appsettings.json') -Encoding utf8
    Set-Marker '41'

    # Folder ACL, then existing files back to inheriting it. Not DEPLOYMENT.md's "/inheritance:r /grant (OI)(CI)... /T":
    # on files that strips the inherited ACEs and the (OI)(CI) grants don't apply, leaving an empty DACL
    # (yanmt.exe then fails to start with error 5).
    icacls $data /inheritance:r /grant "${account}:(OI)(CI)M" '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Select-Object -Last 1
    icacls $app /inheritance:r /grant "${account}:(OI)(CI)RX" '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Select-Object -Last 1
    icacls "$app\*" /reset /T /Q | Select-Object -Last 1
    Grant-KeyRead $cert $userSid

    $credential = [System.Management.Automation.PSCredential]::new($account, $password)
    New-Service -Name YANMT -DisplayName 'YANMT network monitor' -BinaryPathName "`"$app\yanmt.exe`"" `
        -StartupType Automatic -Credential $credential | Out-Null
    sc.exe start YANMT | Out-String | Write-Host   # prints the Win32 error code when it fails
    if (-not (Wait-Until { (Get-Service YANMT).Status -eq 'Running' } 60)) {
        sc.exe query YANMT | Out-String | Write-Host
        icacls $app | Out-String | Write-Host
        icacls "$app\yanmt.exe" | Out-String | Write-Host
        icacls $env:ProgramFiles | Out-String | Write-Host
        Show-Logs
        throw 'The manual install (svc-yanmt) did not start.'
    }
    $ready = Wait-Until { (Test-Path "$data\secrets.entropy") -and (Test-Path "$data\yanmt.db") } 120
    if (-not $ready) { Show-Logs; throw 'The manual install did not create secrets.entropy and yanmt.db.' }
    Check ((Get-Service-Info).StartName -eq $account) 'manual install runs as svc-yanmt'
    Check ((Get-AclSids "$data\secrets.entropy") -contains $userSid) 'manual install: svc-yanmt is on secrets.entropy (precondition)'

    # --------------------------------------------------------------------------------------------
    Step '2. Bad thumbprint fails early'
    $code = Invoke-Msi 'bad-thumbprint.log' @('/i', $Msi1, 'CERT_THUMBPRINT=0000000000000000000000000000000000000000', 'ADMIN_GROUPS=.\YANMT-Admins')
    Check ($code -eq 1603) "bad thumbprint: msiexec fails (got $code)"
    Check ((Get-Service-Info).StartName -eq $account) 'bad thumbprint: the manual service is untouched'
    Check ((Get-Products).Count -eq 0) 'bad thumbprint: nothing installed'
    Check ((Select-String -Path (Join-Path $LogDir 'bad-thumbprint.log') -Pattern 'No certificate with thumbprint' -Quiet)) 'bad thumbprint: the log says why'

    # --------------------------------------------------------------------------------------------
    Step '3. MSI v1 over the manual install'
    $code = Invoke-Msi 'install-v1.log' @('/i', $Msi1, "CERT_THUMBPRINT=$($thumb.Insert(20, ' '))", 'ADMIN_GROUPS=.\YANMT-Admins',
        'VIEWER_GROUPS=.\YANMT-Viewers', "FIREWALL_REMOTE_ADDRESS=$remote", "PORT=$port")
    Check ($code -eq 0) "install v1: exit code 0 (got $code)"
    $svc = Get-Service-Info
    Check ($null -ne $svc -and $svc.StartName -eq 'NT SERVICE\YANMT') "install v1: service runs as NT SERVICE\YANMT (got '$($svc.StartName)')"
    Check ((sc.exe showsid YANMT | Out-String) -match [regex]::Escape($serviceSid)) 'install v1: service SID matches the one the custom actions compute'
    Check (Wait-Until { (Get-Service YANMT).Status -eq 'Running' } 60) 'install v1: service is running'
    Check (Wait-Until { (Get-HttpStatus) -eq '401' } 60) "install v1: HTTPS on $port answers 401 (got $(Get-HttpStatus))"
    Check ($svc.StartMode -eq 'Auto') 'install v1: automatic start'
    Check ((sc.exe qfailure YANMT | Out-String) -match 'RESTART -- Delay = 60000') 'install v1: failure actions restart after 60 s'

    $dataAcl = Get-Acl $data
    Check $dataAcl.AreAccessRulesProtected 'data folder: protected DACL'
    Check ((Get-AclSids $data) -contains $serviceSid) 'data folder: NT SERVICE\YANMT has access'
    Check (-not ((Get-AclSids $data) -contains $userSid)) 'data folder: svc-yanmt removed'
    $leftovers = @(Get-ChildItem $data -Recurse -Force | Where-Object { (Get-AclSids $_.FullName) -contains $userSid })
    Check ($leftovers.Count -eq 0) "data folder: svc-yanmt on no file inside ($($leftovers.FullName -join ', '))"
    Check ((Get-Acl "$data\secrets.entropy").AreAccessRulesProtected -and (Get-AclSids "$data\secrets.entropy") -contains $serviceSid) 'secrets.entropy: protected, NT SERVICE\YANMT has access'
    Check ((Get-AclSids $app) -contains $serviceSid -and -not ((Get-AclSids $app) -contains $userSid)) 'install folder: NT SERVICE\YANMT, not svc-yanmt'
    Check (Test-KeyRead $cert $serviceSid) 'certificate private key: NT SERVICE\YANMT can read it'

    $rule = Get-Rule
    Check ($null -ne $rule) 'firewall rule exists'
    if ($rule) {
        Check ("$($rule.Profile)" -eq 'Domain, Private') "firewall rule: Domain and Private only (got $($rule.Profile))"
        Check ((($rule | Get-NetFirewallPortFilter).LocalPort) -eq "$port") 'firewall rule: TCP port'
        Check ((($rule | Get-NetFirewallAddressFilter).RemoteAddress -join ',') -match '^192\.0\.2\.0/') "firewall rule: remote address $remote"
    }

    $reg = Get-Settings
    Check ($reg.Port -eq "$port" -and $reg.CertificateThumbprint -eq $thumb -and $reg.AdminGroups -eq '.\YANMT-Admins' -and $reg.ViewerGroups -eq '.\YANMT-Viewers') 'registry: settings written (thumbprint without spaces)'
    Check ([System.Diagnostics.EventLog]::SourceExists('YANMT')) 'event source YANMT registered'
    Check ($null -ne (Get-LocalGroup 'YANMT-Admins' -ErrorAction SilentlyContinue) -and $null -ne (Get-LocalGroup 'YANMT-Viewers' -ErrorAction SilentlyContinue)) 'local groups created'
    Check (@(Get-LocalGroupMember 'YANMT-Admins' -ErrorAction SilentlyContinue).Count -eq 0) 'local groups: no members added'
    Check ((Get-Marker) -eq 41) 'appsettings.json from the manual install kept'
    Check ($null -ne (Get-LocalUser 'svc-yanmt' -ErrorAction SilentlyContinue)) 'svc-yanmt account left alone'

    # --------------------------------------------------------------------------------------------
    Step '4. Upgrade to v2 (no properties)'
    Set-Marker '42'
    $code = Invoke-Msi 'upgrade-v2.log' @('/i', $Msi2)
    Check ($code -eq 0) "upgrade: exit code 0 (got $code)"
    Check (Wait-Until { (Get-Service YANMT).Status -eq 'Running' } 60) 'upgrade: service is running'
    Check (Wait-Until { (Get-HttpStatus) -eq '401' } 60) 'upgrade: HTTPS answers 401'
    Check ((Get-Marker) -eq 42) 'upgrade: hand-edited appsettings.json kept'
    $products = Get-Products
    Check ($products.Count -eq 1 -and $products[0].DisplayVersion -eq '0.0.2') "upgrade: only v2 is installed ($($products.DisplayVersion -join ', '))"
    $reg = Get-Settings
    Check ($reg.CertificateThumbprint -eq $thumb -and $reg.AdminGroups -eq '.\YANMT-Admins' -and $reg.FirewallRemoteAddress -eq $remote) 'upgrade: settings read back from the registry'
    Check ($null -ne (Get-Rule)) 'upgrade: firewall rule kept'
    $backups = @(Get-ChildItem "$data\backups\pre-upgrade" -Directory -ErrorAction SilentlyContinue)
    Check ($backups.Count -eq 1 -and $backups[0].Name -like '0.0.1*' -and (Test-Path "$($backups[0].FullName)\yanmt.db") -and (Test-Path "$($backups[0].FullName)\secrets.entropy")) 'upgrade: database and secrets.entropy backed up to backups\pre-upgrade\0.0.1-*'

    # --------------------------------------------------------------------------------------------
    Step '5. Downgrade to v1 is refused'
    $code = Invoke-Msi 'downgrade-v1.log' @('/i', $Msi1)
    Check ($code -ne 0) "downgrade: refused (exit code $code)"
    Check ((Get-Products)[0].DisplayVersion -eq '0.0.2') 'downgrade: v2 still installed'
    Check ((Get-Service YANMT).Status -eq 'Running') 'downgrade: service still running'

    # --------------------------------------------------------------------------------------------
    Step '6. Uninstall'
    $code = Invoke-Msi 'uninstall.log' @('/x', $Msi2)
    Check ($code -eq 0) "uninstall: exit code 0 (got $code)"
    Check ($null -eq (Get-Service-Info)) 'uninstall: service deleted'
    Check ($null -eq (Get-Rule)) 'uninstall: firewall rule removed'
    Check (-not (Test-Path 'HKLM:\SOFTWARE\YANMT')) 'uninstall: HKLM\SOFTWARE\YANMT removed'
    Check (-not [System.Diagnostics.EventLog]::SourceExists('YANMT')) 'uninstall: event source removed'
    Check (-not (Test-Path $app)) "uninstall: install folder removed ($(@(Get-ChildItem $app -Recurse -ErrorAction SilentlyContinue).FullName -join ', '))"
    Check ((Test-Path "$data\yanmt.db") -and (Test-Path "$data\secrets.entropy") -and (Test-Path "$data\backups\pre-upgrade")) 'uninstall: data folder kept'
    Check ((Get-Products).Count -eq 0) 'uninstall: no YANMT product left'
}
catch {
    Write-Host "ERROR  $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
    $failures.Add("script error: $_")
    Show-Logs
}

Write-Host ''
if ($failures.Count -gt 0) {
    Show-Logs
    Write-Host "$($failures.Count) check(s) failed:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}

Write-Host 'All MSI smoke checks passed.' -ForegroundColor Green
