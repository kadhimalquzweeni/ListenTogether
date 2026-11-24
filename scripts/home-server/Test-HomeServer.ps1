[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [int]$StunPort = 3478,
    [int]$TimeoutMilliseconds = 4000
)

$ErrorActionPreference = "Stop"
$failed = [System.Collections.Generic.List[string]]::new()
$passed = [System.Collections.Generic.List[string]]::new()

$ip = $null
if (-not [System.Net.IPAddress]::TryParse($Server, [ref]$ip) -or
    $ip.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
    throw "Supply the host's public IPv4 address, not a URL or LAN address. Example: -Server 203.0.113.42"
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

Write-Host "Testing ListenTogether at $($ip.ToString()) from this network..." -ForegroundColor Cyan
Write-Host "Run this on a different internet connection, not the host's Wi-Fi."
Write-Host ""

foreach ($port in @(80, 443)) {
    if (Test-TcpPort -Address $ip.ToString() -Port $port -Timeout $TimeoutMilliseconds) {
        $passed.Add("TCP $port is reachable.")
        Write-Host "[PASS] TCP $port reachable" -ForegroundColor Green
    }
    else {
        $failed.Add("TCP $port is unreachable. Check router forwarding TCP external $port -> host LAN IP port $port, the matching Windows Firewall rule, and whether another service is listening.")
        Write-Host "[FAIL] TCP $port unreachable" -ForegroundColor Red
    }
}

$stun = Test-StunBinding -Address $ip.ToString() -Port $StunPort -Timeout $TimeoutMilliseconds
if ($stun.Success) {
    $passed.Add("UDP $StunPort STUN binding succeeded.")
    Write-Host "[PASS] UDP $StunPort STUN reachable. $($stun.Reason)" -ForegroundColor Green
}
else {
    $failed.Add("UDP $StunPort STUN failed. Check that ListenTogether.Server is running, router forwarding UDP external $StunPort -> host LAN IP port $StunPort, and the matching Windows Firewall rule. TCP port checks cannot verify UDP.")
    Write-Host "[FAIL] UDP $StunPort STUN unreachable. $($stun.Reason)" -ForegroundColor Red
}

Write-Host ""
if ($failed.Count -eq 0) {
    Write-Host "All required public ports are reachable. Router forwarding appears correct." -ForegroundColor Green
    Write-Host "This confirms signalling/STUN reachability, not that every peer network permits direct UDP hole punching."
    exit 0
}

Write-Host "Router / host configuration items to fix:" -ForegroundColor Yellow
foreach ($item in $failed) {
    Write-Host "  - $item" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "Also verify:" -ForegroundColor Yellow
Write-Host "  - The router forwards to the host PC's current reserved LAN IPv4 address."
Write-Host "  - The router WAN IPv4 matches the host's detected public IPv4 (no upstream NAT/CGNAT)."
Write-Host "  - The host PC is powered on and ListenTogether.Server/Caddy are running."
Write-Host "  - Your ISP/router does not block inbound ports 80, 443, or UDP $StunPort."
exit 1
