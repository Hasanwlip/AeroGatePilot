# Repairs Windows Internet Connection Sharing for Mobile Hotspot when phones join but never get an IP address.
# Root cause it fixes: a removed adapter (old VPN/TAP) still flagged as the ICS public or private connection in
# root\Microsoft\HomeNet, which makes every new sharing attempt fail with 0x80040201.
# Fully automatic: elevates itself, turns the hotspot off, clears stale entries, restarts services,
# turns the hotspot back on and verifies. The result is written to repair-hotspot.log next to this script.

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    return
}

$logFile = Join-Path $PSScriptRoot 'repair-hotspot.log'
Set-Content $logFile "repair-hotspot $(Get-Date -Format s)"
function Log($text, $color = 'Gray') {
    Write-Host $text -ForegroundColor $color
    Add-Content $logFile $text
}

trap {
    Log "ERROR: $_" Red
    Start-Sleep -Seconds 20
    exit 1
}

Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType = WindowsRuntime]
$null = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime]
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1

function Await($operation) {
    $task = $asTask.MakeGenericMethod([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult]).Invoke($null, @($operation))
    $null = $task.Wait(60000)
    $task.Result
}

function Get-Tethering {
    $profile = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
    if (-not $profile) { throw 'No internet connection - plug in the Ethernet cable.' }
    [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)
}

function Test-IcsServing {
    $svc = Get-CimInstance Win32_Service -Filter "Name='SharedAccess'"
    [bool](Get-NetUDPEndpoint -OwningProcess $svc.ProcessId -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in 53, 67 })
}

Log '> Turning Mobile Hotspot off' Cyan
$tethering = Get-Tethering
if ($tethering.TetheringOperationalState -ne 'Off') {
    $r = Await ($tethering.StopTetheringAsync())
    Log "  $($r.Status)"
}

Log '> Clearing stale Internet Connection Sharing entries' Cyan
$present = @(Get-NetAdapter -IncludeHidden | ForEach-Object { $_.InterfaceGuid.ToString().ToUpperInvariant().Trim('{}') })
foreach ($entry in Get-WmiObject -Namespace root\Microsoft\HomeNet -Class HNet_ConnectionProperties) {
    if (-not ($entry.IsIcsPublic -or $entry.IsIcsPrivate)) { continue }
    $guid = ([regex]::Match($entry.Connection, '\{([0-9A-Fa-f-]+)\}').Groups[1].Value).ToUpperInvariant()
    $state = if ($present -contains $guid) { 'present adapter' } else { 'adapter no longer exists' }
    $entry.IsIcsPublic = $false
    $entry.IsIcsPrivate = $false
    $null = $entry.Put()
    Log "  cleared {$guid} ($state)" Green
}

Log '> Restarting sharing services' Cyan
Set-Service NlaSvc -StartupType Automatic -ErrorAction SilentlyContinue
Start-Service NlaSvc -ErrorAction SilentlyContinue
Stop-Service icssvc -Force -ErrorAction SilentlyContinue
Stop-Service SharedAccess -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
Start-Service SharedAccess
Start-Service icssvc
Log ("  " + ((Get-Service NlaSvc, SharedAccess, icssvc | ForEach-Object { "$($_.Name)=$($_.Status)" }) -join ', '))

Log '> Turning Mobile Hotspot on' Cyan
$tethering = Get-Tethering
$r = Await ($tethering.StartTetheringAsync())
Log "  $($r.Status) $($r.AdditionalErrorMessage)"
$cfg = $tethering.GetCurrentAccessPointConfiguration()
Log "  SSID: $($cfg.Ssid)"

Log '> Waiting for internet sharing (DHCP/DNS) to start' Cyan
$deadline = (Get-Date).AddSeconds(45)
$ok = $false
while (-not $ok -and (Get-Date) -lt $deadline) {
    $ok = Test-IcsServing
    if (-not $ok) { Start-Sleep -Seconds 3 }
}

if ($ok) {
    Log "RESULT: OK - Windows is sharing the internet over Wi-Fi '$($cfg.Ssid)'. On the phone: Forget This Network, then join again." Green
} else {
    Log 'RESULT: FAILED - sharing still did not start.' Yellow
}
Start-Sleep -Seconds 15
