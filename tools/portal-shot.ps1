# Development helper: opens the portal preview from the running app and screenshots it with headless Edge.
param(
    [string]$OutDir = "$PSScriptRoot\..\docs\screenshots",
    [string]$Lang = 'en'
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$proc = Get-Process AeroGatePilot -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)

function Find-ByName([string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $name
    $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-Element($el) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    while ($el) {
        $p = $null
        if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); return }
        if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return }
        $el = $walker.GetParent($el)
    }
}

Invoke-Element (Find-ByName 'Login portal')
Start-Sleep -Milliseconds 800
Invoke-Element (Find-ByName 'Preview in browser')
Start-Sleep -Seconds 3

$port = Get-NetTCPConnection -State Listen -OwningProcess $proc.Id -ErrorAction Stop |
    Where-Object LocalAddress -eq '127.0.0.1' | Select-Object -First 1 -ExpandProperty LocalPort
if (-not $port) { throw 'Preview server is not listening.' }
Write-Output "Preview on port $port"

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
$out = Join-Path (Resolve-Path $OutDir) "portal-login-$Lang.png"
& $edge --headless=new --disable-gpu --hide-scrollbars --window-size=1280,860 --screenshot="$out" "http://127.0.0.1:$port/portal/?lang=$Lang" 2>$null
Start-Sleep -Seconds 4
Write-Output "Saved $out"
