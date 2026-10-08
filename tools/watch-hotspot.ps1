# Development helper: logs Mobile Hotspot clients and hotspot-subnet neighbors every 2 seconds.
param([int]$Seconds = 300)

Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType = WindowsRuntime]
$null = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime]

$profile = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
$manager = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)
$last = ''
$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline) {
    $clients = @($manager.GetTetheringClients() | ForEach-Object { "$($_.MacAddress) [" + (($_.HostNames | ForEach-Object { $_.DisplayName }) -join ', ') + ']' })
    $neighbors = @(Get-NetNeighbor -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -like '192.168.137.*' -and $_.State -ne 'Permanent' } |
        ForEach-Object { "$($_.IPAddress)=$($_.LinkLayerAddress)/$($_.State)" })
    $line = "state=$($manager.TetheringOperationalState) clients=$($manager.ClientCount) {" + ($clients -join '; ') + "} neighbors {" + ($neighbors -join '; ') + '}'
    if ($line -ne $last) {
        Write-Output ("{0:HH:mm:ss} {1}" -f (Get-Date), $line)
        $last = $line
    }
    Start-Sleep -Seconds 2
}
