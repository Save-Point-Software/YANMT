<#
.SYNOPSIS
  MSI smoke test for a throwaway Windows machine (GitHub Actions windows-latest). Issue #40.

.DESCRIPTION
  1. A manual install as the local account svc-yanmt (DEPLOYMENT.md section 3), started once so
     yanmt.db and secrets.entropy exist. Its yanmt.exe comes from -LegacyPublishDir, a build with a
     higher file version than the MSI's (issue #47: the MSI then skipped its service component).
  2. An MSI install with a bad thumbprint: must fail early and change nothing.
  3. MSI install (version 1) over the manual install: replaces the service with NT SERVICE\YANMT,
     re-grants folders and the key, creates the local groups, keeps the hand-edited appsettings.json,
     replaces the newer-versioned yanmt.exe, and covers all firewall profiles.
  2b. A custom Yanmt:DataPath (appsettings.json, or a machine environment variable): refused early.
  4. Upgrade to version 2 with no properties: settings read back from the registry, database
     backed up, hand-edited appsettings.json kept.
  4b/c. Change: clear the viewer groups and the firewall scope, then set them again.
  5. Version 1 again: refused (downgrade).
  6. Uninstall: service, firewall rule, event source, install folder and registry key gone; data kept.
  7. CREATE_SELF_SIGNED_CERT=1 (issue #49): with CERT_THUMBPRINT too it's refused; alone it creates one certificate
     (SAN, non-exportable RSA 3072, key readable by NT SERVICE\YANMT) that HTTPS presents. A repair, and /i again
     with CREATE_SELF_SIGNED_CERT=1, create no second one. Then uninstall (the certificate stays).
  8. A self-signed install that rolls back (WIXFAILWHENDEFERRED=1) leaves no certificate behind.

  It creates a self-signed certificate, a local user and local groups, and changes the firewall.
  Never run it on a real server. No YANMT polling is configured and no device is contacted.
#>
#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory)] [string] $Msi1,
    [Parameter(Mandatory)] [string] $Msi2,
    [Parameter(Mandatory)] [string] $PublishDir,
    # The manual install's files: the same app published with a higher version than the MSIs' yanmt.exe.
    [Parameter(Mandatory)] [string] $LegacyPublishDir,
    [string] $LogDir = (Join-Path $PWD 'smoke-logs')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Msi1 = (Resolve-Path $Msi1).Path
$Msi2 = (Resolve-Path $Msi2).Path
$PublishDir = (Resolve-Path $PublishDir).Path
$LegacyPublishDir = (Resolve-Path $LegacyPublishDir).Path
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
    if ($p.ExitCode -notin 0, 3010) {
        # The custom actions log "YANMT:" lines; "Return value 3" marks the action that failed.
        $text = Get-Content (Join-Path $LogDir $log)
        $text | Select-String -Pattern 'YANMT', 'Return value 3', 'Error \d{4}', 'Exception' | Select-Object -First 40 |
            ForEach-Object { Write-Host "    $($_.Line)" }
        $hit = $text | Select-String 'Return value 3' | Select-Object -First 1
        if ($hit) { $text[[Math]::Max(0, $hit.LineNumber - 25)..($hit.LineNumber - 1)] | ForEach-Object { Write-Host "  | $_" } }
    }
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

function Get-Setting([string] $name) {
    $r = Get-Settings
    if ($r -and $r.PSObject.Properties[$name]) { return $r.$name }
    return $null
}

# The app's startup line "Installer settings from HKLM\SOFTWARE\YANMT override appsettings.json: <keys>".
# Read with sharing: the service has the log open.
function Get-InstallerSettingsLines {
    $lines = foreach ($f in Get-ChildItem (Join-Path $data 'logs') -Filter 'yanmt-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime) {
        $stream = [System.IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite, Delete')
        try { ([System.IO.StreamReader]::new($stream)).ReadToEnd() -split "`r?`n" } finally { $stream.Dispose() }
    }
    return @($lines | Where-Object { $_ -like '*Installer settings from HKLM*' })
}

# The certificate HTTPS on $port presents (Kestrel serves one certificate, whatever the SNI name).
function Get-ServedThumbprint {
    try {
        $tcp = [System.Net.Sockets.TcpClient]::new('localhost', $port)
        try {
            $ssl = [System.Net.Security.SslStream]::new($tcp.GetStream(), $false, { param($s, $c, $ch, $e) $true })
            $ssl.AuthenticateAsClient('localhost')
            return [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($ssl.RemoteCertificate).Thumbprint
        } finally { $tcp.Dispose() }
    } catch { return "error: $($_.Exception.Message)" }
}

function Get-SelfSigned { @(Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.FriendlyName -eq 'YANMT (self-signed)' }) }

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
    Copy-Item (Join-Path $LegacyPublishDir '*') -Destination $app -Recurse -Force
    $msiExe = Get-Item (Join-Path $PublishDir 'yanmt.exe')
    $legacyVersion = [version](Get-Item "$app\yanmt.exe").VersionInfo.FileVersion
    $msiVersion = [version]$msiExe.VersionInfo.FileVersion
    Write-Host "Manual yanmt.exe $legacyVersion, MSI yanmt.exe $msiVersion"
    if ($legacyVersion -le $msiVersion) { throw "The manual install's yanmt.exe ($legacyVersion) must be newer than the MSI's ($msiVersion)." }

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
    Check ((Get-Service-Info).StartName -like '*\svc-yanmt') "manual install runs as svc-yanmt (got '$((Get-Service-Info).StartName)')"
    Check ((Get-AclSids "$data\secrets.entropy") -contains $userSid) 'manual install: svc-yanmt is on secrets.entropy (precondition)'

    # --------------------------------------------------------------------------------------------
    Step '2. Bad thumbprint fails early'
    $code = Invoke-Msi 'bad-thumbprint.log' @('/i', $Msi1, 'CERT_THUMBPRINT=0000000000000000000000000000000000000000', 'ADMIN_GROUPS=.\YANMT-Admins')
    Check ($code -eq 1603) "bad thumbprint: msiexec fails (got $code)"
    Check ((Get-Service-Info).StartName -like '*\svc-yanmt') 'bad thumbprint: the manual service is untouched'
    Check (@(Get-Products).Count -eq 0) 'bad thumbprint: nothing installed'
    Check ((Select-String -Path (Join-Path $LogDir 'bad-thumbprint.log') -Pattern 'No certificate with thumbprint' -Quiet)) 'bad thumbprint: the log says why'

    # --------------------------------------------------------------------------------------------
    Step '2b. A custom Yanmt:DataPath is refused before anything changes'
    $settingsFile = Join-Path $app 'appsettings.json'
    $original = Get-Content $settingsFile -Raw
    $json = $original | ConvertFrom-Json
    $json.Yanmt.DataPath = 'C:\YanmtCustomData'
    $json | ConvertTo-Json -Depth 10 | Set-Content $settingsFile -Encoding utf8
    $code = Invoke-Msi 'custom-datapath-json.log' @('/i', $Msi1, "CERT_THUMBPRINT=$thumb", 'ADMIN_GROUPS=.\YANMT-Admins')
    Set-Content $settingsFile $original -NoNewline -Encoding utf8
    Check ($code -eq 1603) "custom DataPath in appsettings.json: refused (got $code)"
    Check ((Select-String -Path (Join-Path $LogDir 'custom-datapath-json.log') -Pattern 'sets Yanmt:DataPath' -Quiet)) 'custom DataPath in appsettings.json: the log says why'

    [Environment]::SetEnvironmentVariable('Yanmt__DataPath', 'D:\YanmtElsewhere', 'Machine')
    $code = Invoke-Msi 'custom-datapath-env.log' @('/i', $Msi1, "CERT_THUMBPRINT=$thumb", 'ADMIN_GROUPS=.\YANMT-Admins')
    [Environment]::SetEnvironmentVariable('Yanmt__DataPath', $null, 'Machine')
    Check ($code -eq 1603) "custom DataPath in a machine environment variable: refused (got $code)"
    Check ((Select-String -Path (Join-Path $LogDir 'custom-datapath-env.log') -Pattern 'Yanmt__DataPath' -Quiet)) 'custom DataPath in a machine environment variable: the log says why'

    $svc = Get-Service-Info
    Check ($svc.StartName -like '*\svc-yanmt' -and $svc.State -eq 'Running') 'custom DataPath: the manual service is untouched and running'
    Check (@(Get-Products).Count -eq 0) 'custom DataPath: nothing installed'

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
    $installed = Get-Item "$app\yanmt.exe"
    Check ($installed.VersionInfo.FileVersion -eq $msiExe.VersionInfo.FileVersion -and
        (Get-FileHash $installed.FullName).Hash -eq (Get-FileHash $msiExe.FullName).Hash) "install v1: the newer manual yanmt.exe was replaced by the MSI's (got $($installed.VersionInfo.FileVersion))"
    $v1log = Join-Path $LogDir 'install-v1.log'
    Check (-not (Select-String -Path $v1log -Pattern 'Disallowing installation of component.*higher versioned keyfile' -Quiet)) 'install v1: no component skipped for a newer key file'
    Check ((Select-String -Path $v1log -Pattern 'Component: YanmtService;.*Action: Local' -Quiet)) 'install v1: YanmtService component installed'
    Check ((Select-String -Path $v1log -Pattern 'YANMT: the YANMT service exists, runs as NT SERVICE\\YANMT' -Quiet)) 'install v1: VerifyYanmtService checked the service'
    Check ((Select-String -Path $v1log -Pattern 'YANMT: legacy install detected: replacing all files regardless of version' -Quiet)) 'install v1: the log says a legacy install was detected'
    $okLines = @(Select-String -Path $v1log -Pattern 'YANMT: settings OK')
    Check ($okLines.Count -eq 1 -and "$($okLines[0].Line)" -match 'admin groups \[\.\\YANMT-Admins\], viewer groups \[\.\\YANMT-Viewers\], firewall remote address \[192\.0\.2\.0/24\]') "install v1: one 'settings OK' line, with the given values ($(($okLines | ForEach-Object Line) -join ' / '))"
    Check ((sc.exe qfailure YANMT | Out-String) -match 'RESTART -- Delay = 60000') 'install v1: failure actions restart after 60 s'

    $dataAcl = Get-Acl $data
    Check $dataAcl.AreAccessRulesProtected 'data folder: protected DACL'
    Check ((Get-AclSids $data) -contains $serviceSid) 'data folder: NT SERVICE\YANMT has access'
    Check (-not ((Get-AclSids $data) -contains $userSid)) 'data folder: svc-yanmt removed'
    $leftovers = @(Get-ChildItem $data -Recurse -Force | Where-Object { (Get-AclSids $_.FullName) -contains $userSid })
    Check ($leftovers.Count -eq 0) "data folder: svc-yanmt on no file inside ($(($leftovers | ForEach-Object FullName) -join ', '))"
    Check ((Get-Acl "$data\secrets.entropy").AreAccessRulesProtected -and (Get-AclSids "$data\secrets.entropy") -contains $serviceSid) 'secrets.entropy: protected, NT SERVICE\YANMT has access'
    Check ((Get-AclSids $app) -contains $serviceSid -and -not ((Get-AclSids $app) -contains $userSid)) 'install folder: NT SERVICE\YANMT, not svc-yanmt'
    Check (Test-KeyRead $cert $serviceSid) 'certificate private key: NT SERVICE\YANMT can read it'

    $rule = Get-Rule
    Check ($null -ne $rule) 'firewall rule exists'
    if ($rule) {
        Check ((([int]$rule.Profile) -band 7) -eq 7) "firewall rule: Domain, Private and Public (got $($rule.Profile))"
        Check ((($rule | Get-NetFirewallPortFilter).LocalPort) -eq "$port") 'firewall rule: TCP port'
        Check ((($rule | Get-NetFirewallAddressFilter).RemoteAddress -join ',') -match '^192\.0\.2\.0/') "firewall rule: remote address $remote"
    }
    # The runner's firewall may be off; then there is nothing to cover. Either way, no warning.
    $fwLine = Select-String -Path $v1log -Pattern 'YANMT( WARNING)?: (firewall|could not read the firewall)' | Select-Object -Last 1
    Check ("$fwLine" -match 'YANMT: firewall: (the active profile\(s\) .* are covered|off for the active)') "install v1: the log says the active firewall profile is covered ($fwLine)"

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
    $products = @(Get-Products)
    Check ($products.Count -eq 1 -and $products[0].DisplayVersion -eq '0.0.65535') "upgrade: only v2 is installed ($(($products | ForEach-Object DisplayVersion) -join ', '))"
    $reg = Get-Settings
    Check ($reg.CertificateThumbprint -eq $thumb -and $reg.AdminGroups -eq '.\YANMT-Admins' -and $reg.FirewallRemoteAddress -eq $remote) 'upgrade: settings read back from the registry'
    Check ($null -ne (Get-Rule)) 'upgrade: firewall rule kept'
    $backups = @(Get-ChildItem "$data\backups\pre-upgrade" -Directory -ErrorAction SilentlyContinue)
    Check ($backups.Count -eq 1 -and $backups[0].Name -like '0.0.1*' -and (Test-Path "$($backups[0].FullName)\yanmt.db") -and (Test-Path "$($backups[0].FullName)\secrets.entropy")) 'upgrade: database and secrets.entropy backed up to backups\pre-upgrade\0.0.1-*'

    $last = @(Get-InstallerSettingsLines) | Select-Object -Last 1
    Check ("$last" -match 'ViewerGroups') "upgrade: the app takes ViewerGroups from the registry ($last)"

    # --------------------------------------------------------------------------------------------
    Step '4b. Change (silent): clear the viewer groups and the firewall scope'
    $before = @(Get-InstallerSettingsLines).Count
    $code = Invoke-Msi 'clear-settings.log' @('/i', $Msi2, 'CLEAR_VIEWER_GROUPS=1', 'CLEAR_FIREWALL_REMOTE_ADDRESS=1')
    Check ($code -eq 0) "clear: exit code 0 (got $code)"
    Check ($null -eq (Get-Rule)) 'clear: firewall rule removed'
    Check ($null -eq (Get-Setting 'FirewallRemoteAddress')) 'clear: FirewallRemoteAddress removed from the registry'
    Check ($null -eq (Get-Setting 'ViewerGroups')) 'clear: ViewerGroups removed from the registry'
    Check ((Get-Setting 'AdminGroups') -eq '.\YANMT-Admins' -and (Get-Setting 'CertificateThumbprint') -eq $thumb) 'clear: other settings kept'
    Check (Wait-Until { (Get-Service YANMT).Status -eq 'Running' } 60) 'clear: service is running'
    Check (Wait-Until { @(Get-InstallerSettingsLines).Count -gt $before } 60) 'clear: the service restarted with the new settings'
    $last = @(Get-InstallerSettingsLines) | Select-Object -Last 1
    Check ("$last" -notmatch 'ViewerGroups' -and "$last" -match 'AdminGroups') "clear: the app no longer takes ViewerGroups from the registry ($last)"

    Step '4c. Change (silent): set them again'
    $before = @(Get-InstallerSettingsLines).Count
    $code = Invoke-Msi 'set-settings.log' @('/i', $Msi2, 'VIEWER_GROUPS=.\YANMT-Viewers', "FIREWALL_REMOTE_ADDRESS=$remote")
    Check ($code -eq 0) "set again: exit code 0 (got $code)"
    Check ($null -ne (Get-Rule)) 'set again: firewall rule back'
    Check ((Get-Setting 'ViewerGroups') -eq '.\YANMT-Viewers' -and (Get-Setting 'FirewallRemoteAddress') -eq $remote) 'set again: values stored'
    Check (Wait-Until { @(Get-InstallerSettingsLines).Count -gt $before } 60) 'set again: the service restarted'
    $last = @(Get-InstallerSettingsLines) | Select-Object -Last 1
    Check ("$last" -match 'ViewerGroups') 'set again: the app takes ViewerGroups from the registry'

    # --------------------------------------------------------------------------------------------
    Step '5. Downgrade to v1 is refused'
    $code = Invoke-Msi 'downgrade-v1.log' @('/i', $Msi1)
    Check ($code -ne 0) "downgrade: refused (exit code $code)"
    Check (@(Get-Products)[0].DisplayVersion -eq '0.0.65535') 'downgrade: v2 still installed'
    Check ((Get-Service YANMT).Status -eq 'Running') 'downgrade: service still running'

    # --------------------------------------------------------------------------------------------
    Step '6. Uninstall'
    $code = Invoke-Msi 'uninstall.log' @('/x', $Msi2)
    Check ($code -eq 0) "uninstall: exit code 0 (got $code)"
    Check ($null -eq (Get-Service-Info)) 'uninstall: service deleted'
    Check ($null -eq (Get-Rule)) 'uninstall: firewall rule removed'
    Check (-not (Test-Path 'HKLM:\SOFTWARE\YANMT')) 'uninstall: HKLM\SOFTWARE\YANMT removed'
    Check (-not [System.Diagnostics.EventLog]::SourceExists('YANMT')) 'uninstall: event source removed'
    Check (-not (Test-Path $app)) "uninstall: install folder removed ($((Get-ChildItem $app -Recurse -ErrorAction SilentlyContinue | ForEach-Object FullName) -join ', '))"
    Check ((Test-Path "$data\yanmt.db") -and (Test-Path "$data\secrets.entropy") -and (Test-Path "$data\backups\pre-upgrade")) 'uninstall: data folder kept'
    Check (@(Get-Products).Count -eq 0) 'uninstall: no YANMT product left'

    # --------------------------------------------------------------------------------------------
    Step '7. Self-signed certificate (CREATE_SELF_SIGNED_CERT=1)'
    Check (@(Get-SelfSigned).Count -eq 0) 'self-signed: no YANMT certificate yet (precondition)'
    $code = Invoke-Msi 'self-signed-both.log' @('/i', $Msi2, "CERT_THUMBPRINT=$thumb", 'CREATE_SELF_SIGNED_CERT=1', 'ADMIN_GROUPS=.\YANMT-Admins')
    Check ($code -eq 1603) "self-signed with CERT_THUMBPRINT too: refused (got $code)"
    Check ((Select-String -Path (Join-Path $LogDir 'self-signed-both.log') -Pattern 'not both' -Quiet)) 'self-signed with CERT_THUMBPRINT too: the log says why'
    Check (@(Get-SelfSigned).Count -eq 0 -and @(Get-Products).Count -eq 0) 'self-signed with CERT_THUMBPRINT too: nothing created or installed'

    $code = Invoke-Msi 'self-signed.log' @('/i', $Msi2, 'CREATE_SELF_SIGNED_CERT=1', 'ADMIN_GROUPS=.\YANMT-Admins', "PORT=$port")
    Check ($code -eq 0) "self-signed install: exit code 0 (got $code)"
    $created = @(Get-SelfSigned)
    Check ($created.Count -eq 1) "self-signed install: exactly one certificate 'YANMT (self-signed)' in LocalMachine\My (got $($created.Count))"
    if ($created.Count -ge 1) {
        $ss = $created[0]
        $ipProps = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties()
        $fqdn = if ($ipProps.DomainName) { "$($ipProps.HostName).$($ipProps.DomainName)" } else { $ipProps.HostName }
        $san = @($ss.DnsNameList | ForEach-Object { $_.Unicode })
        Check (($san -contains $env:COMPUTERNAME) -and ($san -contains $fqdn)) "self-signed: SAN has $env:COMPUTERNAME and $fqdn (got $($san -join ', '))"
        Check ($ss.HasPrivateKey) 'self-signed: has its private key'
        $eku = @($ss.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId })
        Check ($eku -contains '1.3.6.1.5.5.7.3.1') "self-signed: Server Authentication EKU (got $($eku -join ', '))"
        $years = ($ss.NotAfter - $ss.NotBefore).TotalDays / 365.25
        Check ($years -gt 1.99 -and $years -lt 2.01) "self-signed: valid for two years ($($ss.NotBefore) to $($ss.NotAfter))"
        $ssKey = (Get-KeyDescriptor $ss).Key
        Check ($ssKey.KeySize -eq 3072) "self-signed: RSA 3072 (got $($ssKey.KeySize))"
        Check ($ssKey.ExportPolicy -eq [System.Security.Cryptography.CngExportPolicies]::None) "self-signed: private key not exportable (got $($ssKey.ExportPolicy))"
        Check ($ssKey.IsMachineKey) 'self-signed: machine key'
        Check (Test-KeyRead $ss $serviceSid) 'self-signed: NT SERVICE\YANMT can read the private key'
        Check ((Get-Setting 'CertificateThumbprint') -eq $ss.Thumbprint) "self-signed: its thumbprint is stored (got '$(Get-Setting 'CertificateThumbprint')')"
        Check (Wait-Until { (Get-Service YANMT).Status -eq 'Running' } 60) 'self-signed: service is running'
        Check (Wait-Until { (Get-ServedThumbprint) -eq $ss.Thumbprint } 60) "self-signed: HTTPS on $port presents that certificate (got $(Get-ServedThumbprint))"
        Check (Wait-Until { (Get-HttpStatus) -eq '401' } 60) "self-signed: HTTPS answers 401 (got $(Get-HttpStatus))"

        Step '7b. Repair, and /i again with CREATE_SELF_SIGNED_CERT=1: no second certificate'
        $code = Invoke-Msi 'self-signed-repair.log' @('/fa', $Msi2)
        Check ($code -eq 0) "repair: exit code 0 (got $code)"
        $code = Invoke-Msi 'self-signed-again.log' @('/i', $Msi2, 'CREATE_SELF_SIGNED_CERT=1')
        Check ($code -eq 0) "/i again with CREATE_SELF_SIGNED_CERT=1: exit code 0 (got $code)"
        Check ((Select-String -Path (Join-Path $LogDir 'self-signed-again.log') -Pattern 'CREATE_SELF_SIGNED_CERT=1 ignored' -Quiet)) '/i again: the log says CREATE_SELF_SIGNED_CERT=1 was ignored'
        $after = @(Get-SelfSigned)
        Check ($after.Count -eq 1 -and $after[0].Thumbprint -eq $ss.Thumbprint) "repair and /i again: still the one certificate (got $(($after | ForEach-Object Thumbprint) -join ', '))"
        Check ((Get-Setting 'CertificateThumbprint') -eq $ss.Thumbprint) 'repair and /i again: the stored thumbprint is unchanged'
        Check (Wait-Until { (Get-ServedThumbprint) -eq $ss.Thumbprint } 60) 'repair and /i again: HTTPS still presents it'
    }

    $code = Invoke-Msi 'self-signed-uninstall.log' @('/x', $Msi2)
    Check ($code -eq 0) "self-signed uninstall: exit code 0 (got $code)"
    Check (@(Get-SelfSigned).Count -eq 1) 'self-signed uninstall: the certificate is kept'

    # --------------------------------------------------------------------------------------------
    Step '8. A self-signed install that rolls back removes its certificate'
    $before = @(Get-SelfSigned | ForEach-Object Thumbprint)
    $code = Invoke-Msi 'self-signed-rollback.log' @('/i', $Msi2, 'CREATE_SELF_SIGNED_CERT=1', 'ADMIN_GROUPS=.\YANMT-Admins', 'WIXFAILWHENDEFERRED=1')
    Check ($code -eq 1603) "rollback: msiexec fails (got $code)"
    $rbLog = Join-Path $LogDir 'self-signed-rollback.log'
    Check ((Select-String -Path $rbLog -Pattern 'YANMT: created the self-signed certificate' -Quiet)) 'rollback: the certificate was created before the failure'
    Check ((Select-String -Path $rbLog -Pattern 'YANMT: rollback: removed the self-signed certificate' -Quiet)) 'rollback: the log says it was removed'
    $left = @(Get-SelfSigned | ForEach-Object Thumbprint | Where-Object { $before -notcontains $_ })
    Check ($left.Count -eq 0) "rollback: no new certificate left ($($left -join ', '))"
    Check (@(Get-Products).Count -eq 0) 'rollback: nothing installed'
}
catch {
    Write-Host "ERROR  $_" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
    $failures.Add("script error: $_")
    Show-Logs
}

Copy-Item (Join-Path $data 'logs') (Join-Path $LogDir 'app-logs') -Recurse -ErrorAction SilentlyContinue

Write-Host ''
if ($failures.Count -gt 0) {
    Show-Logs
    Write-Host "$($failures.Count) check(s) failed:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}

Write-Host 'All MSI smoke checks passed.' -ForegroundColor Green
