# Uninstalls Clash Verge and Proxifier and disables Cisco AnyConnect and Hotspot Shield services,
# which block Windows Internet Connection Sharing (Mobile Hotspot). Elevates itself.
#
# To re-enable later:  Set-Service vpnagent -StartupType Automatic; Set-Service hshld_12.12.0 -StartupType Automatic

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    return
}

trap {
    Write-Host "`nError: $_" -ForegroundColor Red
    Read-Host "`nPress Enter to close"
    exit 1
}

function Step($text) { Write-Host "`n> $text" -ForegroundColor Cyan }

function Find-Uninstall([string]$pattern) {
    Get-ItemProperty HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*, HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\* -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like $pattern } | Select-Object -First 1
}

Step 'Closing Clash Verge and Proxifier'
Get-Process | Where-Object { $_.ProcessName -match '^(clash-verge|Clash Verge|clash-core-service|verge-mihomo|mihomo|Proxifier|ProxyChecker)' } |
    ForEach-Object { Write-Host "  closing $($_.ProcessName)"; Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
Stop-Service clash_verge_service -Force -ErrorAction SilentlyContinue

Step 'Uninstalling Clash Verge'
$clash = Find-Uninstall 'Clash Verge'
if ($clash) {
    $p = Start-Process msiexec.exe -ArgumentList "/x $($clash.PSChildName) /qn /norestart" -Wait -PassThru
    Write-Host "  msiexec exit code $($p.ExitCode) (0 or 3010 = success)"
} else { Write-Host '  not installed' }

Step 'Uninstalling Proxifier'
$proxifier = Find-Uninstall 'Proxifier*'
if ($proxifier) {
    $exe = $proxifier.UninstallString.Trim('"')
    $p = Start-Process $exe -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
    Write-Host "  uninstaller exit code $($p.ExitCode) (0 = success)"
} else { Write-Host '  not installed' }

Step 'Disabling Cisco AnyConnect and Hotspot Shield services'
foreach ($s in Get-Service vpnagent, 'hshld*' -ErrorAction SilentlyContinue) {
    Stop-Service $s.Name -Force -ErrorAction SilentlyContinue
    Set-Service $s.Name -StartupType Disabled
    Write-Host "  disabled $($s.DisplayName)"
}
Get-Process vpnui, 'hsscp*', 'HotspotShield*' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Step 'Result'
foreach ($name in 'Clash Verge', 'Proxifier*') {
    $left = Find-Uninstall $name
    Write-Host ("  {0,-14} {1}" -f $name.TrimEnd('*'), $(if ($left) { 'STILL INSTALLED' } else { 'removed' })) -ForegroundColor $(if ($left) { 'Yellow' } else { 'Green' })
}
Get-Service vpnagent, 'hshld*' -ErrorAction SilentlyContinue | Format-Table Name, Status, StartType -AutoSize

Write-Host "Restart Windows now so the removed network drivers unload, then run repair-hotspot.cmd again." -ForegroundColor Green
Read-Host "`nPress Enter to close"
