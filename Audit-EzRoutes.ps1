param(
    [ValidateRange(0, 10000)][int]$ProbeCount = 0,
    [switch]$ProbeAll,
    [switch]$Resume,
    [switch]$VerifyTunnel,
    [switch]$RequireExpiredAccess,
    [string]$Output
)

$ErrorActionPreference = 'Stop'
if ($ProbeAll -and $ProbeCount -gt 0) { throw 'Choose either -ProbeAll or -ProbeCount.' }
if ($Resume -and !$Output) { throw 'Pass the existing report path with -Output when resuming.' }
if ($VerifyTunnel -and ($ProbeAll -or $Resume -or $ProbeCount -gt 1)) {
    throw 'Tunnel verification uses one fresh route. Run it without batch or resume options.'
}
if ($RequireExpiredAccess -and ($VerifyTunnel -or $ProbeAll -or $Resume -or $ProbeCount -gt 1)) {
    throw 'The expired-access check uses one fresh route. Run it without tunnel, batch, or resume options.'
}
if (!$Output) {
    $name = 'ez-route-audit-{0:yyyyMMdd-HHmmss}.json' -f (Get-Date)
    $directory = Join-Path $PSScriptRoot 'ezconnect-research/audits'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $Output = Join-Path $directory $name
}
$count = if ($ProbeAll) { -1 } else { $ProbeCount }
if ($VerifyTunnel) { $count = 1 }
if ($RequireExpiredAccess) { $count = 1 }
$project = Join-Path $PSScriptRoot 'tools/RouteAudit/RouteAudit.csproj'
$auditArgs = @($count, $Output)
if ($Resume) { $auditArgs += '--resume' }
if ($VerifyTunnel) { $auditArgs += '--verify-tunnel' }
if ($RequireExpiredAccess) { $auditArgs += '--require-expired-access' }
dotnet run --project $project -c Release -- @auditArgs
