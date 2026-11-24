[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [string]$HostLanIp,
    [int]$StunPort = 3478,
    [int]$TimeoutMilliseconds = 4000
)

$ErrorActionPreference = "Stop"
$failed = [System.Collections.Generic.List[object]]::new()

$ip = $null
if (-not [System.Net.IPAddress]::TryParse($Server, [ref]$ip) -or
    $ip.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
    throw "Supply the host's public IPv4 address, not a URL or LAN address. Example: -Server 203.0.113.42"
}

if ($HostLanIp) {
    $lanIp = $null
    if (-not [System.Net.IPAddress]::TryParse($HostLanIp, [ref]$lanIp) -or
        $lanIp.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw "HostLanIp must be the host PC's LAN IPv4 address, such as 192.168.1.50."
    }
    $octets = $HostLanIp.Split('.') | ForEach-Object { [int]$_ }
    $isPrivate = $octets[0] -eq 10 -or
        ($octets[0] -eq 172 -and $octets[1] -ge 16 -and $octets[1] -le 31) -or
        ($octets[0] -eq 192 -and $octets[1] -eq 168)
    if (-not $isPrivate) {
        throw "HostLanIp should be a private LAN address (10.x, 172.16-31.x, or 192.168.x)."
    }
}

function Test-TcpPort {
    param([string]$Address, [int]$Port, [int]$Timeout)
    $client = [System.Net.Sockets.TcpClient]::new([System.Net.Sockets.AddressFamily]::InterNetwork)
    try {
        $task = $client.ConnectAsync($Address, $Port)
        if (-not $task.Wait($Timeout)) { return $false }
        return $client.Connected
    }
    catch { return $false }
    finally { $client.Dispose() }
}

function Test-StunBinding {
    param([string]$Address, [int]$Port, [int]$Timeout)

    $udp = [System.Net.Sockets.UdpClient]::new([System.Net.Sockets.AddressFamily]::InterNetwork)
    try {
        $udp.Client.ReceiveTimeout = $Timeout
        $udp.Client.SendTimeout = $Timeout
        $endpoint = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse($Address), $Port)

        $request = [byte[]]::new(20)
        $request[0] = 0
        $request[1] = 1
        $request[4] = 0x21; $request[5] = 0x12; $request[6] = 0xA4; $request[7] = 0x42
        $transaction = [byte[]]::new(12)
        [System.Security.Cryptography.RandomNumberGenerator]::Fill($transaction)
        [Array]::Copy($transaction, 0, $request, 8, 12)

        [void]$udp.Send($request, $request.Length, $endpoint)
        $remote = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
        $response = $udp.Receive([ref]$remote)

        if ($response.Length -lt 20 -or $response[0] -ne 1 -or $response[1] -ne 1) {
            return @{ Success = $false; Reason = "The endpoint did not return a STUN Binding Success response." }
        }
        if ($response[4] -ne 0x21 -or $response[5] -ne 0x12 -or $response[6] -ne 0xA4 -or $response[7] -ne 0x42) {
            return @{ Success = $false; Reason = "The response had an invalid STUN magic cookie." }
        }
        for ($i = 0; $i -lt 12; $i++) {
            if ($response[8 + $i] -ne $request[8 + $i]) {
                return @{ Success = $false; Reason = "The STUN transaction ID did not match." }
            }
        }
        return @{ Success = $true; Reason = "Valid STUN response received from $remote." }
    }
    catch [System.Net.Sockets.SocketException] {
        return @{ Success = $false; Reason = "No UDP response within $Timeout ms." }
    }
    finally { $udp.Dispose() }
}

function Write-RequiredRouterRule {
    param([string]$Name, [string]$Protocol, [int]$Port, [string]$LanAddress)

    Write-Host ""
    Write-Host "ROUTER CHANGE REQUIRED - $Name" -ForegroundColor Yellow
    Write-Host "Open Port Forwarding / Virtual Server / NAT Rules. Add a rule or edit the existing one to match:"
    Write-Host "  Rule name / service: ListenTogether $Name"
    Write-Host "  Protocol:            $Protocol"
    Write-Host "  External / WAN port: $Port"
    Write-Host "  Internal / LAN port: $Port"
    if ($LanAddress) {
        Write-Host "  Device / target IP:  $LanAddress"
    }
    else {
        Write-Host "  Device / target IP:  <host PC's reserved LAN IPv4 address>"
        Write-Host "                       Run Setup-ListenTogether.ps1 on the host PC to display it."
    }
    Write-Host "  Enabled:             Yes"
    Write-Host "  Source / remote IP:  Any / blank"
    Write-Host "  Schedule:            Always"
    Write-Host "Use the exact protocol shown; do not forward TCP 5000."
}

Write-Host "Testing ListenTogether at $($ip.ToString()) from this network..." -ForegroundColor Cyan
Write-Host "Run this on a different internet connection, not the host's Wi-Fi."
if ($HostLanIp) { Write-Host "Host LAN destination supplied: $HostLanIp" }
Write-Host ""

$tcp80 = Test-TcpPort -Address $ip.ToString() -Port 80 -Timeout $TimeoutMilliseconds
$tcp443 = Test-TcpPort -Address $ip.ToString() -Port 443 -Timeout $TimeoutMilliseconds

if ($tcp80) {
    Write-Host "[PASS] TCP 80 reachable" -ForegroundColor Green
}
else {
    Write-Host "[FAIL] TCP 80 unreachable" -ForegroundColor Red
    $failed.Add(@{ Name = "HTTP / certificate validation"; Protocol = "TCP"; Port = 80 })
}

if ($tcp443) {
    Write-Host "[PASS] TCP 443 reachable" -ForegroundColor Green
}
else {
    Write-Host "[FAIL] TCP 443 unreachable" -ForegroundColor Red
    $failed.Add(@{ Name = "HTTPS / app connection"; Protocol = "TCP"; Port = 443 })
}

$stun = Test-StunBinding -Address $ip.ToString() -Port $StunPort -Timeout $TimeoutMilliseconds
if ($stun.Success) {
    Write-Host "[PASS] UDP $StunPort STUN reachable. $($stun.Reason)" -ForegroundColor Green
}
else {
    Write-Host "[FAIL] UDP $StunPort STUN unreachable. $($stun.Reason)" -ForegroundColor Red
    $failed.Add(@{ Name = "STUN / direct peer connectivity"; Protocol = "UDP"; Port = $StunPort })
}

Write-Host ""
if ($failed.Count -eq 0) {
    Write-Host "All required public ports are reachable. Router forwarding appears correct." -ForegroundColor Green
    Write-Host "This confirms signalling/STUN reachability, not that every peer network permits direct UDP hole punching."
    exit 0
}

Write-Host "The following router forwarding entries need to be added or corrected:" -ForegroundColor Yellow
foreach ($item in $failed) {
    Write-RequiredRouterRule -Name $item.Name -Protocol $item.Protocol -Port $item.Port -LanAddress $HostLanIp
}

Write-Host ""
Write-Host "If those router entries already match exactly, check these host/ISP settings next:" -ForegroundColor Cyan
if (-not $tcp80 -or -not $tcp443) {
    Write-Host "  - Windows Firewall: allow inbound TCP 80 and TCP 443 on the active network profile."
    Write-Host "  - Host services: Caddy must be running and listening on TCP 80/443."
}
if (-not $stun.Success) {
    Write-Host "  - Windows Firewall: allow inbound UDP $StunPort."
    Write-Host "  - Host service: ListenTogether.Server must be running and listening on UDP $StunPort."
}
Write-Host "  - DHCP reservation: ensure the router forwards to the host PC's current LAN IP, not an old address."
Write-Host "  - Double NAT / CGNAT: router WAN IPv4 must match public IPv4 $($ip.ToString()). If not, ask your ISP for a public IPv4 address or put the upstream router into bridge mode."
Write-Host "  - ISP/router filtering: confirm inbound TCP 80/443 and UDP $StunPort are not blocked."
Write-Host ""
Write-Host "A failed outside test cannot identify the exact incorrect router field. The rule above is the required target configuration; re-test after correcting it."
exit 1
