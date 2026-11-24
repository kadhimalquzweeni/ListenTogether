[CmdletBinding(SupportsShouldProcess = $true)]
param([string]$RulePrefix = "ListenTogether Home Server")
$ErrorActionPreference = "Stop"
$rules = @(
    @{ Name = "$RulePrefix - HTTPS"; Protocol = "TCP"; LocalPort = 443 },
    @{ Name = "$RulePrefix - HTTP"; Protocol = "TCP"; LocalPort = 80 },
    @{ Name = "$RulePrefix - STUN"; Protocol = "UDP"; LocalPort = 3478 }
)
foreach ($rule in $rules) {
    if ($PSCmdlet.ShouldProcess("$($rule.Protocol) port $($rule.LocalPort)", "Replace firewall rule $($rule.Name)")) {
        Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Action Allow -Protocol $rule.Protocol -LocalPort $rule.LocalPort -Profile Domain,Private,Public | Out-Null
    }
}
Write-Host "Firewall rules configured. Configure router forwarding separately." -ForegroundColor Green
