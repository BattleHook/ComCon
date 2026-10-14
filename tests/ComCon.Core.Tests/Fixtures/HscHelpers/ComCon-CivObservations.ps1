# Offline passive display decoding only. No request construction, serial I/O or safety confirmation.
. (Join-Path $PSScriptRoot 'CIV-Common.ps1')
. (Join-Path $PSScriptRoot 'ComCon-IpcClient.ps1')

function New-ComConCivDisplayState {
    [CmdletBinding()]
    param(
        [ValidateRange(1, 4)][int]$ProfileId = 1,
        [ValidateRange(1, 60000)][int]$MaximumAgeMs = 1000,
        [byte]$RadioAddress = 0x7A,
        [byte]$ControllerAddress = 0xE0,
        [ValidatePattern('^(0|[1-9][0-9]{0,18})$')][string]$AfterObservationId = '0',
        [string]$ServerInstanceId,
        [switch]$LifecycleBound,
        [string]$CaptureId,
        [ValidatePattern('^(0|[1-9][0-9]{0,18})$')][string]$ProfileGeneration = '1',
        [ValidatePattern('^(0|[1-9][0-9]{0,18})$')][string]$OwnershipEpoch = '0',
        [ValidateNotNullOrEmpty()][string]$Model = 'IC7600',
        [string]$FaultLogPath
    )
    if ($RadioAddress -eq $ControllerAddress -or $RadioAddress -in @(0, 0xFE, 0xFD) -or
        $ControllerAddress -in @(0, 0xFE, 0xFD)) {
        throw 'CI-V radio/controller addresses must be distinct valid unicast addresses.'
    }
    [void](ConvertFrom-ComConDecimalId $AfterObservationId)
    [void](ConvertFrom-ComConDecimalId $ProfileGeneration)
    [void](ConvertFrom-ComConDecimalId $OwnershipEpoch)
    if ($CaptureId -and -not $LifecycleBound) {
        throw 'Capture identity requires a lifecycle-bound display state.'
    }
    if ($LifecycleBound -and $AfterObservationId -cne '0' -and [string]::IsNullOrWhiteSpace($CaptureId)) {
        throw 'Lifecycle resynchronization requires the matching capture identity/generation/epoch.'
    }
    if ($AfterObservationId -cne '0' -and [string]::IsNullOrWhiteSpace($ServerInstanceId)) {
        throw 'Explicit nonzero resynchronization requires the matching server instance identity.'
    }
    return [pscustomobject]@{
        profileId = $ProfileId; maximumAgeMs = $MaximumAgeMs
        model = $Model; telemetryEnabled = ($Model -ceq 'IC7600' -and -not $LifecycleBound)
        lifecycleBound = [bool]$LifecycleBound
        faultLogPath = $FaultLogPath
        lifecycleIdentity = $(if ($CaptureId) { $CaptureId } else { $null })
        lifecycleGeneration = $(if ($CaptureId) { $ProfileGeneration } else { $null })
        lifecycleEpoch = $(if ($CaptureId) { $OwnershipEpoch } else { $null })
        radioAddress = $RadioAddress; controllerAddress = $ControllerAddress
        serverInstanceId = $(if ($ServerInstanceId) { $ServerInstanceId } else { $null })
        profileGeneration = $(if ($ServerInstanceId) { $ProfileGeneration } else { $null })
        ownershipEpoch = $(if ($ServerInstanceId) { $OwnershipEpoch } else { $null })
        nextObservationId = $AfterObservationId; hasMore = $false; requiresReset = $false
        resynchronizeAfterObservationId = $null
        status = 'Unavailable'
        reason = $(if ($Model -ceq 'IC7600') { 'NO_OBSERVATIONS' } else { 'UNSUPPORTED_MODEL' })
        readings = @{}
        safetyConfirmation = $false
    }
}

function Write-ComConDevelopmentFaultLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][ValidateRange(1, 4)][int]$ProfileId,
        [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$Message
    )
    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'
    $maximumFileBytes = 1048576
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $messageText = $Message.Substring(0, [Math]::Min(2048, $Message.Length))
    $line = @{ timestampUtc = [DateTime]::UtcNow.ToString('o'); component = 'passiveDisplay'
        profileId = $ProfileId; message = $messageText } | ConvertTo-Json -Compress
    $encoding = New-Object System.Text.UTF8Encoding($false)
    $bytes = $encoding.GetBytes($line + [Environment]::NewLine)
    if ($bytes.Length -gt $maximumFileBytes) { throw 'The development fault record exceeds the file-size limit.' }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $identity = [BitConverter]::ToString($sha.ComputeHash($encoding.GetBytes($fullPath.ToUpperInvariant()))).Replace('-', '') }
    finally { $sha.Dispose() }
    $mutex = New-Object System.Threading.Mutex($false, ('ComConDevelopmentFaultLog_' + $identity))
    $acquired = $false
    try {
        try { $acquired = $mutex.WaitOne(2000) }
        catch [System.Threading.AbandonedMutexException] {
            $acquired = $true
            throw 'The development fault log lock was abandoned; retry explicitly.'
        }
        if (-not $acquired) { throw 'Timed out waiting for the development fault log lock.' }
        [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($fullPath))
        foreach ($file in @($fullPath, ($fullPath + '.1'), ($fullPath + '.2'))) {
            if (-not [System.IO.File]::Exists($file) -or (Get-Item -LiteralPath $file -ErrorAction Stop).Length -le $maximumFileBytes) { continue }
            $stream = New-Object System.IO.FileStream($file, [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::Read)
            try {
                if ($stream.Length -le $maximumFileBytes) { continue }
                $stream.Position = $stream.Length - $maximumFileBytes - 1
                $startsOnLine = $stream.ReadByte() -eq 10
                $tail = New-Object byte[] $maximumFileBytes
                $count = 0
                while ($count -lt $tail.Length) {
                    $read = $stream.Read($tail, $count, $tail.Length - $count)
                    if ($read -eq 0) { throw 'Unexpected end of oversized development fault log.' }
                    $count += $read
                }
                if ($tail[$tail.Length - 1] -ne 10) { throw 'An oversized development fault log ends with an incomplete record.' }
                $first = 0
                if (-not $startsOnLine) { $first = [Array]::IndexOf($tail, [byte]10) + 1 }
                if ($first -ge $tail.Length) { throw 'An oversized development fault log has no complete record within the size limit.' }
                $stream.Position = 0
                $stream.Write($tail, $first, $tail.Length - $first)
                $stream.SetLength($tail.Length - $first)
            }
            finally { $stream.Dispose() }
        }
        if ([System.IO.File]::Exists($fullPath) -and
            (Get-Item -LiteralPath $fullPath -ErrorAction Stop).Length + $bytes.Length -gt $maximumFileBytes) {
            [System.IO.File]::Delete($fullPath + '.2')
            if ([System.IO.File]::Exists($fullPath + '.1')) { [System.IO.File]::Move($fullPath + '.1', $fullPath + '.2') }
            [System.IO.File]::Move($fullPath, $fullPath + '.1')
        }
        $stream = New-Object System.IO.FileStream($fullPath, [System.IO.FileMode]::Append,
            [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
        try { $stream.Write($bytes, 0, $bytes.Length) }
        finally { $stream.Dispose() }
    }
    finally {
        if ($acquired) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

function Clear-ComConCivDisplayState {
    param([Parameter(Mandatory = $true)]$State, [Parameter(Mandatory = $true)][string]$Reason)
    $previousReason = $State.reason
    $State.readings.Clear()
    $State.status = 'Unavailable'
    $State.reason = $Reason
    $State.hasMore = $false
    if ($State.faultLogPath -and $Reason -cne $previousReason -and
        $Reason -notin @('NO_OBSERVATIONS', 'TELEMETRY_OFF', 'STALE')) {
        Write-ComConDevelopmentFaultLog -Path $State.faultLogPath -ProfileId $State.profileId -Message $Reason
    }
}

function Update-ComConCivDisplayLifecycle {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$State, [Parameter(Mandatory = $true)]$Lifecycle)
    Set-StrictMode -Version Latest
    try {
        if ($Lifecycle.implementation -cne 'offlinePassiveLifecyclePreparation' -or
            $Lifecycle.safetyConfirmation -isnot [bool] -or $Lifecycle.safetyConfirmation -or
            $Lifecycle.enabled -isnot [bool] -or $Lifecycle.state -cnotin @('Off', 'Observing', 'Faulted') -or
            $Lifecycle.enabled -ne ($Lifecycle.state -ceq 'Observing') -or
            $Lifecycle.serverInstanceId -isnot [string] -or
            [string]::IsNullOrWhiteSpace($Lifecycle.serverInstanceId) -or
            $Lifecycle.profileGeneration -isnot [string] -or $Lifecycle.ownershipEpoch -isnot [string] -or
            ($null -ne $Lifecycle.profileId -and ($Lifecycle.profileId -isnot [int] -or $Lifecycle.profileId -notin 1..4)) -or
            ($Lifecycle.state -ceq 'Observing' -and $null -eq $Lifecycle.profileId) -or
            ($null -ne $Lifecycle.fault -and $Lifecycle.fault -isnot [string]) -or
            ($Lifecycle.state -ceq 'Faulted' -and [string]::IsNullOrWhiteSpace($Lifecycle.fault)) -or
            ($Lifecycle.state -cne 'Faulted' -and $null -ne $Lifecycle.fault)) {
            throw 'Passive telemetry lifecycle metadata is invalid.'
        }
        [void](ConvertFrom-ComConDecimalId $Lifecycle.profileGeneration)
        [void](ConvertFrom-ComConDecimalId $Lifecycle.ownershipEpoch)
        $State.telemetryEnabled = $false
        if ($Lifecycle.profileId -ne $State.profileId -and $null -ne $Lifecycle.profileId) {
            $State.requiresReset = $true
            throw 'PROFILE_CHANGED: create a display state for the selected transceiver; old readings cleared.'
        }
        if ($null -ne $State.lifecycleIdentity -and
            ($State.lifecycleIdentity -cne $Lifecycle.serverInstanceId -or
             $State.lifecycleGeneration -cne $Lifecycle.profileGeneration -or
             $State.lifecycleEpoch -cne $Lifecycle.ownershipEpoch)) {
            $State.requiresReset = $true
            throw 'IDENTITY_CHANGED: capture changed; create a new display state.'
        }
        $State.lifecycleIdentity = $Lifecycle.serverInstanceId
        $State.lifecycleGeneration = $Lifecycle.profileGeneration
        $State.lifecycleEpoch = $Lifecycle.ownershipEpoch
        if ($State.model -cne 'IC7600') {
            Clear-ComConCivDisplayState $State 'UNSUPPORTED_MODEL'
        }
        elseif ($Lifecycle.state -ceq 'Off') {
            Clear-ComConCivDisplayState $State 'TELEMETRY_OFF'
        }
        elseif ($Lifecycle.state -ceq 'Faulted') {
            Clear-ComConCivDisplayState $State "RECEIVE_FAULT: $($Lifecycle.fault)"
        }
        else {
            if ($State.requiresReset) { throw 'RESET_REQUIRED: create a new display state.' }
            $State.telemetryEnabled = $true
        }
        Get-ComConCivDisplaySnapshot $State
    }
    catch {
        $State.telemetryEnabled = $false
        Clear-ComConCivDisplayState $State $_.Exception.Message
        throw
    }
}

function Receive-ComConCivDisplayLifecycle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^ComConTest_[A-Za-z0-9_]{1,89}$')][string]$TestPipeName,
        [ValidateRange(100, 3000)][int]$TimeoutMs = 3000
    )
    Set-StrictMode -Version Latest
    try {
        $State.lifecycleBound = $true
        if ($State.requiresReset) { throw 'RESET_REQUIRED: create a new display state before reconnecting.' }
        $status = Get-ComConIpcTelemetryStatus -ProfileId $State.profileId `
            -TestPipeName $TestPipeName -TimeoutMs $TimeoutMs
        if ($null -ne $State.serverInstanceId -and $State.serverInstanceId -cne $status.serverInstanceId) {
            $State.requiresReset = $true
            throw 'IDENTITY_CHANGED: lifecycle pipe host changed; create a new display state.'
        }
        $State.serverInstanceId = $status.serverInstanceId
        $snapshot = Update-ComConCivDisplayLifecycle -State $State -Lifecycle $status.telemetry
        $State.profileGeneration = $status.telemetry.profileGeneration
        $State.ownershipEpoch = $status.telemetry.ownershipEpoch
        $snapshot
    }
    catch {
        $State.requiresReset = $true
        $State.telemetryEnabled = $false
        Clear-ComConCivDisplayState -State $State -Reason $_.Exception.Message
        throw
    }
}

function Reset-ComConCivDisplayState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^ComConTest_[A-Za-z0-9_]{1,89}$')][string]$TestPipeName,
        [ValidateRange(100, 3000)][int]$TimeoutMs = 3000
    )
    Set-StrictMode -Version Latest
    if (-not $State.lifecycleBound) { throw 'Display reset requires lifecycle-bound simulation mode.' }
    $State.telemetryEnabled = $false
    $State.requiresReset = $true
    Clear-ComConCivDisplayState -State $State -Reason 'NO_OBSERVATIONS'
    try {
        $fresh = New-ComConCivDisplayState -LifecycleBound -ProfileId $State.profileId `
            -MaximumAgeMs $State.maximumAgeMs -RadioAddress $State.radioAddress `
            -ControllerAddress $State.controllerAddress -Model $State.model -FaultLogPath $State.faultLogPath
        $null = Receive-ComConCivDisplayLifecycle -State $fresh -TestPipeName $TestPipeName -TimeoutMs $TimeoutMs
        if ($fresh.telemetryEnabled) {
            $baseline = Get-ComConIpcObservations -TestPipeName $TestPipeName -ProfileId $fresh.profileId `
                -TimeoutMs $TimeoutMs -ExpectedServerInstanceId $fresh.serverInstanceId `
                -CaptureId $fresh.lifecycleIdentity -ProfileGeneration $fresh.lifecycleGeneration `
                -OwnershipEpoch $fresh.lifecycleEpoch -AfterObservationId '0'
            $fresh.nextObservationId = $baseline.latestObservationId
            $fresh.reason = 'AWAITING_FRESH_OBSERVATIONS'
        }
        return $fresh
    }
    catch {
        Clear-ComConCivDisplayState -State $State -Reason $_.Exception.Message
        throw
    }
}

function Get-ComConCivDisplaySnapshot {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$State)
    Set-StrictMode -Version Latest
    $now = [System.Diagnostics.Stopwatch]::GetTimestamp()
    $values = @{}
    foreach ($name in @($State.readings.Keys)) {
        $entry = $State.readings[$name]
        $age = [decimal]$entry.ageMs + [decimal][Math]::Ceiling(
            ($now - $entry.receiptTicks) * 1000.0 / [System.Diagnostics.Stopwatch]::Frequency)
        if ($age -ge $State.maximumAgeMs) {
            $State.readings.Remove($name)
        }
        else {
            $values[$name] = [pscustomobject]@{
                value = $entry.value; ageMs = [long]$age
                observationId = $entry.observationId; safetyConfirmation = $false
            }
        }
    }
    if ($values.Count -eq 0 -and $State.status -ceq 'Available') {
        Clear-ComConCivDisplayState -State $State -Reason 'STALE'
    }
    return [pscustomobject]@{
        status = $State.status; reason = $State.reason; readings = $values
        serverInstanceId = $State.serverInstanceId; nextObservationId = $State.nextObservationId
        captureId = $State.lifecycleIdentity; profileGeneration = $State.profileGeneration
        ownershipEpoch = $State.ownershipEpoch; lifecycleBound = $State.lifecycleBound
        requiresReset = $State.requiresReset; hasMore = $State.hasMore
        resynchronizeAfterObservationId = $State.resynchronizeAfterObservationId
        source = 'simulatedPassiveRx'; safetyConfirmation = $false
        profileId = $State.profileId; model = $State.model; telemetryEnabled = $State.telemetryEnabled
    }
}

function Update-ComConCivDisplayState {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$State, [Parameter(Mandatory = $true)]$Batch)
    Set-StrictMode -Version Latest
    try {
        if (-not $State.telemetryEnabled -or $State.model -cne 'IC7600') {
            throw "DISPLAY_DISABLED: $($State.reason)"
        }
        if ($State.requiresReset) { throw 'RESET_REQUIRED: create a new display state to resynchronize.' }
        if ($Batch.profileId -isnot [int] -or $Batch.profileId -ne $State.profileId -or
            $Batch.serverInstanceId -isnot [string] -or [string]::IsNullOrWhiteSpace($Batch.serverInstanceId)) {
            throw 'Display batch profile/server identity is invalid.'
        }
        if ($null -ne $State.serverInstanceId -and
            ($Batch.serverInstanceId -cne $State.serverInstanceId -or
             $Batch.profileGeneration -cne $State.profileGeneration -or
             $Batch.ownershipEpoch -cne $State.ownershipEpoch)) {
            $State.requiresReset = $true
            throw 'IDENTITY_CHANGED: create a new display state; histories cannot be merged.'
        }
        Assert-ComConObservationBatch -Batch $Batch -AfterObservationId $State.nextObservationId
        if ($State.lifecycleBound -or $Batch.implementation -ceq 'offlinePassiveLifecycleSimulation') {
            if (-not $State.lifecycleBound -or $null -eq $State.lifecycleIdentity -or
                $Batch.implementation -cne 'offlinePassiveLifecycleSimulation' -or
                $Batch.captureId -cne $State.lifecycleIdentity -or
                $Batch.profileGeneration -cne $State.lifecycleGeneration -or
                $Batch.ownershipEpoch -cne $State.lifecycleEpoch) {
                $State.requiresReset = $true
                throw 'IDENTITY_CHANGED: observations do not match the validated lifecycle capture.'
            }
        }
        if ($Batch.gap) {
            $State.requiresReset = $true
            $State.resynchronizeAfterObservationId = $Batch.latestObservationId
            $State.serverInstanceId = $Batch.serverInstanceId
            $State.profileGeneration = $Batch.profileGeneration
            $State.ownershipEpoch = $Batch.ownershipEpoch
            throw 'OBSERVATION_GAP: create a new display state to resynchronize.'
        }
        $receiptTicks = [System.Diagnostics.Stopwatch]::GetTimestamp()
        $decoded = @($Batch.observations | ForEach-Object {
            ConvertFrom-ComConCivObservation -Observation $_ -RadioAddress $State.radioAddress `
                -ControllerAddress $State.controllerAddress -MaximumAgeMs $State.maximumAgeMs
        })
        $State.serverInstanceId = $Batch.serverInstanceId
        $State.profileGeneration = $Batch.profileGeneration
        $State.ownershipEpoch = $Batch.ownershipEpoch
        foreach ($reading in $decoded) {
            if ($reading.accepted) {
                $State.readings[$reading.reading] = [pscustomobject]@{
                    value = $reading.value; ageMs = $reading.ageMs
                    observationId = $reading.observationId
                    receiptTicks = $receiptTicks
                }
                $State.status = 'Available'
                $State.reason = 'PASSIVE_DISPLAY_ONLY'
            }
            elseif ($reading.reason -notin @('UNRELATED_OR_ECHO', 'UNSUPPORTED_COMMAND')) {
                Clear-ComConCivDisplayState -State $State -Reason $reading.reason
            }
        }
        $State.nextObservationId = $Batch.nextObservationId
        $State.hasMore = $Batch.hasMore
        Get-ComConCivDisplaySnapshot -State $State
    }
    catch {
        if ($State.lifecycleBound) { $State.telemetryEnabled = $false }
        Clear-ComConCivDisplayState -State $State -Reason $_.Exception.Message
        throw
    }
}

function Receive-ComConCivDisplayState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^ComConTest_[A-Za-z0-9_]{1,89}$')][string]$TestPipeName,
        [ValidateRange(100, 3000)][int]$TimeoutMs = 3000
    )
    Set-StrictMode -Version Latest
    try {
        if ($State.lifecycleBound) {
            $null = Receive-ComConCivDisplayLifecycle -State $State -TestPipeName $TestPipeName -TimeoutMs $TimeoutMs
        }
        if (-not $State.telemetryEnabled -or $State.model -cne 'IC7600') {
            throw "DISPLAY_DISABLED: $($State.reason)"
        }
        if ($State.requiresReset) { throw 'RESET_REQUIRED: create a new display state to resynchronize.' }
        $query = @{
            TestPipeName = $TestPipeName; ProfileId = $State.profileId; TimeoutMs = $TimeoutMs
            AfterObservationId = $State.nextObservationId; ExpectedServerInstanceId = $State.serverInstanceId
        }
        if ($State.lifecycleBound) {
            $query.CaptureId = $State.lifecycleIdentity
            $query.ProfileGeneration = $State.lifecycleGeneration
            $query.OwnershipEpoch = $State.lifecycleEpoch
        }
        $batch = Get-ComConIpcObservations @query
        Update-ComConCivDisplayState -State $State -Batch $batch
    }
    catch {
        if ($_.Exception.Message -match 'IDENTITY_CHANGED|CURSOR_INVALID') { $State.requiresReset = $true }
        if ($State.lifecycleBound) {
            $State.telemetryEnabled = $false
            if ($_.Exception.Message -notmatch 'DISPLAY_DISABLED|TELEMETRY_OFF') { $State.requiresReset = $true }
        }
        Clear-ComConCivDisplayState -State $State -Reason $_.Exception.Message
        throw
    }
}

function Get-ComConCivTelemetry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^ComConTest_[A-Za-z0-9_]{1,89}$')][string]$TestPipeName,
        [ValidateRange(1, 4)][int]$ProfileId = 1,
        [ValidateRange(100, 3000)][int]$TimeoutMs = 3000,
        [ValidatePattern('^(0|[1-9][0-9]{0,18})$')][string]$AfterObservationId = '0',
        [byte]$RadioAddress = 0x7A,
        [byte]$ControllerAddress = 0xE0,
        [ValidateRange(1, 60000)][int]$MaximumAgeMs = 1000
    )

    Set-StrictMode -Version Latest
    $batch = Get-ComConIpcObservations -TestPipeName $TestPipeName -ProfileId $ProfileId `
        -TimeoutMs $TimeoutMs -AfterObservationId $AfterObservationId
    if ($batch.gap) {
        throw "OBSERVATION_GAP: receive history is incomplete; latest cursor is $($batch.latestObservationId). Explicitly resynchronize the display cursor."
    }
    return [pscustomobject]@{
        serverInstanceId = $batch.serverInstanceId; profileId = $batch.profileId
        profileGeneration = $batch.profileGeneration; ownershipEpoch = $batch.ownershipEpoch
        source = $batch.source; safetyConfirmation = $false
        nextObservationId = $batch.nextObservationId; hasMore = $batch.hasMore
        readings = @($batch.observations | ForEach-Object {
            ConvertFrom-ComConCivObservation -Observation $_ -RadioAddress $RadioAddress `
                -ControllerAddress $ControllerAddress -MaximumAgeMs $MaximumAgeMs
        })
    }
}

function ConvertFrom-ComConCivObservation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Observation,
        [byte]$RadioAddress = 0x7A,
        [byte]$ControllerAddress = 0xE0,
        [ValidateRange(1, 60000)][int]$MaximumAgeMs = 1000,
        [ValidateSet('IC7600')][string]$Model = 'IC7600'
    )

    Set-StrictMode -Version Latest
    if ($RadioAddress -eq $ControllerAddress -or $RadioAddress -in @(0, 0xFE, 0xFD) -or
        $ControllerAddress -in @(0, 0xFE, 0xFD)) {
        throw 'CI-V radio/controller addresses must be distinct valid unicast addresses.'
    }
    if ($Observation.source -cne 'simulatedPassiveRx' -or
        $Observation.observationId -isnot [string] -or
        $Observation.observationId -cnotmatch '^[1-9][0-9]{0,18}$' -or
        ($Observation.ageMs -isnot [int] -and $Observation.ageMs -isnot [long]) -or
        $Observation.ageMs -lt 0 -or $Observation.hex -isnot [string] -or
        $Observation.hex -cnotmatch '^(?:[0-9A-F]{2}){1,256}$') {
        throw 'Passive CI-V observation metadata is invalid.'
    }
    [void](ConvertFrom-ComConDecimalId $Observation.observationId)
    $age = [decimal]$Observation.ageMs
    if ($null -ne $Observation.PSObject.Properties['clientReceiptTicks']) {
        $received = $Observation.clientReceiptTicks
        $now = [System.Diagnostics.Stopwatch]::GetTimestamp()
        if ($received -isnot [long] -or $received -lt 0 -or $received -gt $now) {
            throw 'Passive CI-V local receipt timestamp is invalid.'
        }
        $age += [decimal][Math]::Ceiling(($now - $received) * 1000.0 / [System.Diagnostics.Stopwatch]::Frequency)
        if ($age -gt [long]::MaxValue) { throw 'Passive CI-V age overflow.' }
    }
    $display = [ordered]@{
        observationId = $Observation.observationId; source = $Observation.source
        ageMs = [long]$age; model = $Model; safetyConfirmation = $false
        accepted = $false; reason = ''; reading = $null; value = $null
    }
    if ($age -ge $MaximumAgeMs) {
        $display.reason = 'STALE'
        return [pscustomobject]$display
    }
    $bytes = ConvertTo-ByteArray $Observation.hex
    if ($bytes.Length -lt 6 -or $bytes[0] -ne 0xFE -or $bytes[1] -ne 0xFE -or
        $bytes[-1] -ne 0xFD -or @($bytes[2..($bytes.Length - 2)] |
            Where-Object { $_ -in @(0xFE, 0xFD) }).Count -ne 0) {
        $display.reason = 'MALFORMED_FRAME'
        return [pscustomobject]$display
    }
    if ($bytes[2] -ne $ControllerAddress -or $bytes[3] -ne $RadioAddress) {
        $display.reason = 'UNRELATED_OR_ECHO'
        return [pscustomobject]$display
    }
    $body = [byte[]]$bytes[4..($bytes.Length - 2)]
    $command = $body[0].ToString('X2')
    $commandLength = 1
    if ($body[0] -in @(0x14, 0x15, 0x16, 0x1C)) {
        if ($body.Length -lt 2) {
            $display.reason = 'MALFORMED_DATA'
            return [pscustomobject]$display
        }
        $command = ConvertTo-HexText ([byte[]]$body[0..1])
        $commandLength = 2
    }
    if (-not $ReadCommands.Contains($command)) {
        $display.reason = 'UNSUPPORTED_COMMAND'
        return [pscustomobject]$display
    }
    $expectedLength = 2
    if ($command -ceq '03') { $expectedLength = 5 }
    if ($command -ceq '16 45' -or $command -ceq '1C 00') { $expectedLength = 1 }
    if ($body.Length -ne $commandLength + $expectedLength) {
        $display.reason = 'MALFORMED_DATA'
        return [pscustomobject]$display
    }
    $data = [byte[]]$body[$commandLength..($body.Length - 1)]
    if ($command -ceq '03' -or
        ($command -match '^1[45] ' -and $command -cne '15 12')) {
        if (@($data | Where-Object { ($_ -shr 4) -gt 9 -or ($_ -band 0x0F) -gt 9 }).Count -ne 0) {
            $display.reason = 'INVALID_BCD'
            return [pscustomobject]$display
        }
        if ($command -cne '03' -and (Get-Bcd $data) -gt 255) {
            $display.reason = 'VALUE_OUT_OF_RANGE'
            return [pscustomobject]$display
        }
    }
    if (($command -ceq '1C 00' -or $command -ceq '16 45') -and $data[0] -notin @(0, 1)) {
        $display.reason = 'VALUE_OUT_OF_RANGE'
        return [pscustomobject]$display
    }
    $decoded = Get-DecodedValue $command $data
    $display.accepted = $true
    $display.reason = 'PASSIVE_DISPLAY_ONLY'
    $display.reading = $ReadingNames[$command]
    $display.value = $decoded.VALUE
    return [pscustomobject]$display
}
