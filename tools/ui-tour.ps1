# Development helper: launches the app without elevation, walks every page via UI Automation and saves screenshots.
param(
    [string]$Exe = "$PSScriptRoot\..\src\AeroGatePilot.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\AeroGatePilot.exe",
    [string]$OutDir = "$PSScriptRoot\..\docs\screenshots",
    [string[]]$Pages = @('Dashboard', 'Users', 'Plans', 'Network & Wi-Fi', 'Login portal', 'Diagnostics', 'Activity'),
    [switch]$Persian,
    [switch]$KeepOpen,
    [string[]]$ClickNames = @(),
    [switch]$ScrollToEnd
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$exePath = (Resolve-Path $Exe).Path
Get-Process AeroGatePilot -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exePath } | Stop-Process -Force
$psi = New-Object System.Diagnostics.ProcessStartInfo (Resolve-Path $Exe)
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['__COMPAT_LAYER'] = 'RunAsInvoker'
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$proc = [System.Diagnostics.Process]::Start($psi)

$root = $null
for ($i = 0; $i -lt 40 -and -not $root; $i++) {
    Start-Sleep -Milliseconds 500
    $proc.Refresh()
    if ($proc.HasExited) { throw "App exited with code $($proc.ExitCode)" }
    if ($proc.MainWindowHandle -ne 0) { $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle) }
}
if (-not $root) { throw 'Main window did not appear.' }
Start-Sleep -Seconds 2

function Find-ByName([string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $name
    $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Click([System.Windows.Automation.AutomationElement]$el) {
    $name = $el.Current.Name
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    while ($el) {
        $pattern = $null
        if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); return }
        if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); return }
        if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle(); return }
        $el = $walker.GetParent($el)
    }
    throw "Element '$name' is not clickable."
}

$nav = [ordered]@{}
foreach ($page in $Pages) {
    $el = Find-ByName $page
    if ($el) { $nav[$page] = $el } else { Write-Warning "Navigation item '$page' not found" }
}

$suffix = 'en'
if ($Persian) {
    $toggleLabel = -join ([char[]](0x0641, 0x0627, 0x0631, 0x0633, 0x06CC))
    Click (Find-ByName $toggleLabel)
    Start-Sleep -Seconds 1
    $suffix = 'fa'
}

$capture = Join-Path $PSScriptRoot 'capture-window.ps1'
foreach ($page in $nav.Keys) {
    Click $nav[$page]
    Start-Sleep -Milliseconds 1500
    foreach ($name in $ClickNames) {
        $el = Find-ByName $name
        if ($el) { Click $el; Start-Sleep -Milliseconds 400 }
    }
    $slug = ($page -replace '[^A-Za-z]+', '-').Trim('-').ToLowerInvariant()
    & $capture -ProcessName AeroGatePilot -OutFile (Join-Path (Resolve-Path $OutDir) "$slug-$suffix.png")
    if ($ScrollToEnd) {
        $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty), $true
        foreach ($el in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
            $scroll = $el.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            if ($scroll.Current.VerticallyScrollable) { $scroll.SetScrollPercent(-1, 100) }
        }
        Start-Sleep -Milliseconds 600
        & $capture -ProcessName AeroGatePilot -OutFile (Join-Path (Resolve-Path $OutDir) "$slug-$suffix-end.png")
    }
}

if (-not $KeepOpen) { $proc.Kill() }
