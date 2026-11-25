[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [int]$Port = 3478,
    [int]$TimeoutMilliseconds = 4000
)
$ErrorActionPreference = "Stop"
$addresses = [System.Net.Dns]::GetHostAddresses($Server) | Where-Object AddressFamily -eq ([System.Net.Sockets.AddressFamily]::InterNetwork)
if (-not $addresses) { throw "Could not resolve IPv4 for '$Server'. ListenTogether STUN is currently IPv4-only." }
$endpoint = [System.Net.IPEndPoint]::new($addresses[0], $Port)
$client = [System.Net.Sockets.UdpClient]::new([System.Net.Sockets.AddressFamily]::InterNetwork)
$client.Client.ReceiveTimeout = $TimeoutMilliseconds
$client.Client.SendTimeout = $TimeoutMilliseconds
try {
    $request = [byte[]]::new(20)
    $request[1] = 1
    $request[4] = 0x21; $request[5] = 0x12; $request[6] = 0xA4; $request[7] = 0x42
    $transaction = [byte[]]::new(12)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($transaction)
    [Array]::Copy($transaction, 0, $request, 8, 12)
    [void]$client.Send($request, $request.Length, $endpoint)
    $remote = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
    $response = $client.Receive([ref]$remote)
    if ($response.Length -lt 20 -or $response[0] -ne 1 -or $response[1] -ne 1) { throw "Response is not a STUN Binding Success Response." }
    if ($response[4] -ne 0x21 -or $response[5] -ne 0x12 -or $response[6] -ne 0xA4 -or $response[7] -ne 0x42) { throw "Invalid STUN magic cookie." }
    for ($i = 0; $i -lt 12; $i++) { if ($response[8 + $i] -ne $request[8 + $i]) { throw "STUN transaction ID mismatch." } }
    $length = ($response[2] -shl 8) -bor $response[3]
    $offset = 20
    $mapped = $null
    while ($offset + 4 -le [Math]::Min($response.Length, 20 + $length)) {
        $type = ($response[$offset] -shl 8) -bor $response[$offset + 1]
        $attributeLength = ($response[$offset + 2] -shl 8) -bor $response[$offset + 3]
        $value = $offset + 4
        if ($value + $attributeLength -gt $response.Length) { break }
        if ($type -eq 0x0020 -and $attributeLength -ge 8 -and $response[$value + 1] -eq 1) {
            $mappedPort = ((($response[$value + 2] -shl 8) -bor $response[$value + 3]) -bxor 0x2112)
            $ip = [byte[]]::new(4)
            for ($i = 0; $i -lt 4; $i++) { $ip[$i] = $response[$value + 4 + $i] -bxor $response[4 + $i] }
            $mapped = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::new($ip), $mappedPort)
            break
        }
        $offset = $value + $attributeLength
        if (($attributeLength % 4) -ne 0) { $offset += 4 - ($attributeLength % 4) }
    }
    if (-not $mapped) { throw "STUN replied but did not include an IPv4 XOR-MAPPED-ADDRESS." }
    Write-Host "STUN reachable at $endpoint" -ForegroundColor Green
    Write-Host "Public UDP mapping observed: $mapped"
    Write-Host "This confirms STUN reachability only; it does not guarantee peer-to-peer hole punching."
}
catch [System.Net.Sockets.SocketException] {
    throw "No STUN response from $endpoint within $TimeoutMilliseconds ms. Check the STUN process, Windows Firewall UDP $Port, router UDP forwarding, DNS and CGNAT. $($_.Exception.Message)"
}
finally { $client.Dispose() }
