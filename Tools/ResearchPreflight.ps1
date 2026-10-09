[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int]$ApiPort = 1337,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$TargetProcessName,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ObserverStatusPath,

    [ValidateRange(1, 300)]
    [int]$MaximumHeartbeatAgeSeconds = 10
)

$ErrorActionPreference = 'Stop'
$checks = [System.Collections.Generic.List[object]]::new()

function Add-Check {
    param(
        [string]$Name,
        [bool]$Passed,
        [string]$Details
    )

    $checks.Add([pscustomobject]@{
        Name = $Name
        Passed = $Passed
        Details = $Details
    })
}

$xauProcesses = @(Get-Process -Name 'XAU' -ErrorAction SilentlyContinue)
$xauDetails = if ($xauProcesses.Count -gt 0) {
    'PID(s): ' + (($xauProcesses | Select-Object -ExpandProperty Id) -join ', ')
} else {
    'XAU.exe is not running.'
}
Add-Check 'XAU process' ($xauProcesses.Count -gt 0) $xauDetails

$listener = @(Get-NetTCPConnection -State Listen -LocalPort $ApiPort -ErrorAction SilentlyContinue)
$xauProcessIds = @($xauProcesses | Select-Object -ExpandProperty Id)
$xauListener = @($listener | Where-Object { $xauProcessIds -contains $_.OwningProcess })
$httpSysListener = @($listener | Where-Object { $_.OwningProcess -eq 4 })
$portOwnedByOtherProcess = @($listener | Where-Object {
    $xauProcessIds -notcontains $_.OwningProcess -and $_.OwningProcess -ne 4
})
$apiSignatureVerified = $false

if ($portOwnedByOtherProcess.Count -gt 0)
{
    Add-Check 'Local API port owner' $false (
        "Port $ApiPort is owned by PID(s): " + (($portOwnedByOtherProcess | Select-Object -ExpandProperty OwningProcess -Unique) -join ', '))
}
elseif ($xauListener.Count -eq 0 -and $httpSysListener.Count -eq 0)
{
    Add-Check 'Local API port owner' $false "XAU is not listening on port $ApiPort."
}

try
{
    $response = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$ApiPort/" -TimeoutSec 3
    $apiSignatureVerified = $response.StatusCode -eq 200 -and $response.Content -match '<title>XAU API Endpoints</title>'
    $readinessDetails = if ($apiSignatureVerified) {
        "GET / returned HTTP $($response.StatusCode) with the expected XAU endpoint signature."
    } else {
        "GET / returned HTTP $($response.StatusCode), but the response was not identified as XAU."
    }
    Add-Check 'Local API readiness' $apiSignatureVerified $readinessDetails
}

catch
{
    $statusCode = $_.Exception.Response.StatusCode.value__
    if ($statusCode)
    {
        Add-Check 'Local API readiness' $false "GET / returned HTTP $statusCode; the API is reachable but not ready."
    }
    else
    {
        Add-Check 'Local API readiness' $false "GET / failed: $($_.Exception.Message)"
    }
}

if ($portOwnedByOtherProcess.Count -eq 0 -and ($xauListener.Count -gt 0 -or ($httpSysListener.Count -gt 0 -and $apiSignatureVerified)))
{
    $ownerDetails = if ($xauListener.Count -gt 0) {
        "XAU owns a listening socket on port $ApiPort."
    } else {
        "HTTP.sys owns port $ApiPort and the XAU endpoint signature was verified."
    }
    Add-Check 'Local API port owner' $true $ownerDetails
}
elseif ($portOwnedByOtherProcess.Count -eq 0 -and $listener.Count -gt 0)
{
    Add-Check 'Local API port owner' $false "Port $ApiPort is registered through HTTP.sys, but the XAU endpoint was not verified."
}

$targetProcesses = @(Get-Process -Name $TargetProcessName -ErrorAction SilentlyContinue)
if ($targetProcesses.Count -eq 0)
{
    Add-Check 'Target process identity' $false "No process named $TargetProcessName is running."
}
else
{
    $identities = $targetProcesses | ForEach-Object {
        $path = try { $_.Path } catch { $null }
        $pathDisplay = if ($path) { $path } else { '<unavailable>' }
        "PID=$($_.Id), name=$($_.ProcessName), path=$pathDisplay"
    }
    Add-Check 'Target process identity' $true ($identities -join '; ')
}

$observerStatus = $null
try
{
    $resolvedStatusPath = (Resolve-Path -LiteralPath $ObserverStatusPath).Path
    $observerStatus = Get-Content -Raw -LiteralPath $resolvedStatusPath | ConvertFrom-Json
    Add-Check 'Observer status file' $true "Loaded $resolvedStatusPath."
}
catch
{
    Add-Check 'Observer status file' $false "Observer status is unavailable or invalid: $($_.Exception.Message)"
}

if ($observerStatus)
{
    $updatedUtc = [DateTime]::MinValue
    $hasHeartbeat = [DateTime]::TryParse(
        [string]$observerStatus.updatedUtc,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal,
        [ref]$updatedUtc)
    $heartbeatAge = if ($hasHeartbeat) { [DateTime]::UtcNow - $updatedUtc.ToUniversalTime() } else { [TimeSpan]::MaxValue }
    $active = $hasHeartbeat -and -not $observerStatus.stoppedUtc -and $heartbeatAge.TotalSeconds -le $MaximumHeartbeatAgeSeconds
    $heartbeatDetails = if ($hasHeartbeat) {
        "Age=$([Math]::Round($heartbeatAge.TotalSeconds, 1))s; stopped=$([bool]$observerStatus.stoppedUtc)."
    } else {
        'The observer status has no valid updatedUtc heartbeat.'
    }
    Add-Check 'Observer heartbeat' $active $heartbeatDetails

    $targetIds = @($targetProcesses | Select-Object -ExpandProperty Id)
    $observedTargets = @($observerStatus.targets | Where-Object {
        $targetIds -contains [int]$_.pid -and $_.name -like $TargetProcessName -and -not $_.endedUtc
    })
    $observerTargetDetails = if ($observedTargets.Count -gt 0) {
        'Verified PID(s): ' + (($observedTargets | Select-Object -ExpandProperty pid) -join ', ')
    } else {
        'The observer heartbeat does not identify a live matching target PID.'
    }
    Add-Check 'Observer target PID' ($observedTargets.Count -gt 0) $observerTargetDetails
}
else
{
    Add-Check 'Observer heartbeat' $false 'Cannot verify without a valid observer status file.'
    Add-Check 'Observer target PID' $false 'Cannot verify without a valid observer status file.'
}

$dbwinHandles = @()
try
{
    $dbwinHandles += [Threading.EventWaitHandle]::OpenExisting('DBWIN_BUFFER_READY')
    $dbwinHandles += [Threading.EventWaitHandle]::OpenExisting('DBWIN_DATA_READY')
    Add-Check 'Observer diagnostic source' $true 'DBWIN ready and data events are present.'
}
catch
{
    Add-Check 'Observer diagnostic source' $false 'DBWIN observer resources are not active.'
}
finally
{
    $dbwinHandles | ForEach-Object { $_.Dispose() }
}

$ready = -not ($checks | Where-Object { -not $_.Passed })
$state = if ($ready) { 'READY' } else { 'NOT READY' }
Write-Output "Research Diagnostic Preflight: $state"
$checks | ForEach-Object {
    $marker = if ($_.Passed) { 'PASS' } else { 'FAIL' }
    Write-Output "[$marker] $($_.Name): $($_.Details)"
}

if (-not $ready) { exit 2 }
