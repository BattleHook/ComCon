# Dot-source this helper; loading it performs no I/O and changes no runtime settings.
function Get-ComConIpcStatus {
    [CmdletBinding()]
    param(
        [ValidateRange(1, 4)]
        [int]$ProfileId = 1,
        [ValidateRange(100, 3000)]
        [int]$TimeoutMs = 3000,
        [ValidatePattern('^ComConTest_[A-Za-z0-9_]{1,89}$')]
        [string]$TestPipeName
    )

    Invoke-ComConReadOnlyQuery @PSBoundParameters -Operation 'GET_STATUS'
}

function Get-ComConIpcObservations {
    [CmdletBinding()]
    param(
        [ValidateRange(1, 4)]
        [int]$ProfileId = 1,
        [ValidateRange(100, 3000)]
        [int]$TimeoutMs = 3000,
        [ValidatePattern('^(0|[1-9][0-9]{0,18})$')]
        [string]$AfterObservationId = '0',
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^ComConTest_[A-Za-z0-9_]{1,89}$')]
        [string]$TestPipeName,
        [string]$ExpectedServerInstanceId,
        [string]$CaptureId,
        [ValidatePattern('^(0|[1-9][0-9]{0,18})$')][string]$ProfileGeneration,
        [ValidatePattern('^(0|[1-9][0-9]{0,18})$')][string]$OwnershipEpoch
    )

    Invoke-ComConReadOnlyQuery @PSBoundParameters -Operation 'GET_OBSERVATIONS'
}

function Get-ComConIpcTelemetryStatus {
    [CmdletBinding()]
    param(
        [ValidateRange(1, 4)][int]$ProfileId = 1,
        [ValidateRange(100, 3000)][int]$TimeoutMs = 3000,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^ComConTest_[A-Za-z0-9_]{1,89}$')][string]$TestPipeName
    )

    Invoke-ComConReadOnlyQuery @PSBoundParameters -Operation 'GET_TELEMETRY_STATUS'
}

function Invoke-ComConReadOnlyQuery {
    [CmdletBinding()]
    param(
        [int]$ProfileId = 1,
        [int]$TimeoutMs = 3000,
        [string]$TestPipeName,
        [ValidateSet('GET_STATUS', 'GET_OBSERVATIONS', 'GET_TELEMETRY_STATUS')]
        [string]$Operation,
        [string]$AfterObservationId = '0',
        [string]$ExpectedServerInstanceId,
        [string]$CaptureId,
        [string]$ProfileGeneration,
        [string]$OwnershipEpoch
    )

    Set-StrictMode -Version Latest
    if ($Operation -ceq 'GET_TELEMETRY_STATUS' -and
        (-not $TestPipeName -or $TestPipeName -cnotmatch '^ComConTest_[A-Za-z0-9_]{1,89}$')) {
        throw 'Lifecycle simulation requires an isolated test pipe.'
    }
    $pipeName = "ComConPipe_Profile$ProfileId"
    if ($TestPipeName) { $pipeName = $TestPipeName }
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
        '.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous,
        [System.Security.Principal.TokenImpersonationLevel]::Identification)
    try {
        $connectClock = [System.Diagnostics.Stopwatch]::StartNew()
        try { $pipe.Connect($TimeoutMs) }
        catch [System.TimeoutException] {
            throw [System.TimeoutException]::new("IPC_TIMEOUT: $Operation connect exceeded ${TimeoutMs}ms.", $_.Exception)
        }
        finally { Write-Verbose "IPC_CONNECT $Operation elapsedMs=$($connectClock.ElapsedMilliseconds)" }
        $hello = Invoke-ComConStatusExchange -Pipe $pipe -TimeoutMs $TimeoutMs -Id '1' -Request @{
            v = 1; kind = 'request'; id = '1'; op = 'HELLO'
            args = @{ clientName = 'Ham Shack Console'; supportedVersions = @(1) }
        }
        if (-not $hello.ok) { throw "ComCon HELLO rejected: $($hello.error.code): $($hello.error.message)" }
        $result = $hello.result
        if ($result.protocolVersion -isnot [int] -or $result.protocolVersion -ne 1 -or
            $result.profileId -isnot [int] -or $result.profileId -ne $ProfileId -or
            $result.sessionId -isnot [string] -or $result.serverInstanceId -isnot [string] -or
            [string]::IsNullOrWhiteSpace($result.sessionId) -or
            [string]::IsNullOrWhiteSpace($result.serverInstanceId)) {
            throw 'ComCon HELLO identity/version is invalid.'
        }
        $arguments = @{}
        if ($Operation -ceq 'GET_TELEMETRY_STATUS' -and
            ($result.implementation -cne 'offlinePassiveLifecycleSimulation' -or
             'GET_TELEMETRY_STATUS' -cnotin $result.operations)) {
            throw 'ComCon does not offer simulated lifecycle status.'
        }
        if ($Operation -ceq 'GET_OBSERVATIONS') {
            if ($ExpectedServerInstanceId -and $result.serverInstanceId -cne $ExpectedServerInstanceId) {
                throw 'IDENTITY_CHANGED: observation server changed; do not submit the previous cursor.'
            }
            if (-not $TestPipeName -or $TestPipeName -cnotmatch '^ComConTest_[A-Za-z0-9_]{1,89}$') {
                throw 'Simulated observations require an isolated test pipe.'
            }
            [void](ConvertFrom-ComConDecimalId $AfterObservationId)
            if ('GET_OBSERVATIONS' -cnotin $result.operations) { throw 'ComCon does not offer passive observations.' }
            $arguments.afterObservationId = $AfterObservationId
            if ($result.implementation -ceq 'offlinePassiveLifecycleSimulation') {
                if ([string]::IsNullOrWhiteSpace($CaptureId) -or
                    [string]::IsNullOrWhiteSpace($ProfileGeneration) -or
                    [string]::IsNullOrWhiteSpace($OwnershipEpoch)) {
                    throw 'Lifecycle observations require matching capture identity/generation/epoch.'
                }
                [void](ConvertFrom-ComConDecimalId $ProfileGeneration)
                [void](ConvertFrom-ComConDecimalId $OwnershipEpoch)
                $arguments.captureId = $CaptureId
                $arguments.profileGeneration = $ProfileGeneration
                $arguments.ownershipEpoch = $OwnershipEpoch
            }
            elseif ($CaptureId -or $ProfileGeneration -or $OwnershipEpoch) {
                throw 'IDENTITY_CHANGED: lifecycle observations cannot downgrade to a legacy feed.'
            }
        }
        $exchangeClock = [System.Diagnostics.Stopwatch]::StartNew()
        $status = Invoke-ComConStatusExchange -Pipe $pipe -TimeoutMs $TimeoutMs -Id '2' -Request @{
            v = 1; kind = 'request'; id = '2'; op = $Operation
            sessionId = $result.sessionId; args = $arguments
        }
        if (-not $status.ok) { throw "ComCon $Operation rejected: $($status.error.code): $($status.error.message)" }
        if ($status.result.profileId -isnot [int] -or $status.result.profileId -ne $ProfileId -or
            $status.result.serverInstanceId -isnot [string] -or
            $status.result.serverInstanceId -cne $result.serverInstanceId) {
            throw 'ComCon response identity is invalid.'
        }
        if ($Operation -ceq 'GET_STATUS') {
            if ($status.result.ready -isnot [bool] -or $status.result.state -isnot [string] -or
                [string]::IsNullOrWhiteSpace($status.result.state)) {
                throw 'ComCon status readiness is invalid.'
            }
        }
        elseif ($Operation -ceq 'GET_TELEMETRY_STATUS') {
            if ($status.result.implementation -cne 'offlinePassiveLifecycleSimulation' -or
                $status.result.hardwareAccess -isnot [bool] -or $status.result.hardwareAccess -or
                $status.result.safetyConfirmation -isnot [bool] -or $status.result.safetyConfirmation -or
                $null -eq $status.result.telemetry) {
                throw 'ComCon lifecycle simulation response is invalid.'
            }
        }
        else {
            Assert-ComConObservationBatch -Batch $status.result -AfterObservationId $AfterObservationId
            if ($CaptureId -and ($status.result.captureId -cne $CaptureId -or
                $status.result.profileGeneration -cne $ProfileGeneration -or
                $status.result.ownershipEpoch -cne $OwnershipEpoch)) {
                throw 'IDENTITY_CHANGED: observation response belongs to another capture.'
            }
            # A conservative transport-age bound; receipt of a cached frame never makes it new.
            foreach ($observation in $status.result.observations) {
                $age = [decimal]$observation.ageMs + [decimal][Math]::Ceiling($exchangeClock.Elapsed.TotalMilliseconds)
                if ($age -gt [long]::MaxValue) { throw 'ComCon observation age overflow.' }
                $observation.ageMs = [long]$age
                $observation | Add-Member -MemberType NoteProperty -Name clientReceiptTicks `
                    -Value ([System.Diagnostics.Stopwatch]::GetTimestamp())
            }
        }
        return $status.result
    }
    finally {
        $pipe.Dispose()
    }
}

function Invoke-ComConStatusExchange {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Pipes.NamedPipeClientStream]$Pipe,
        [Parameter(Mandatory = $true)]
        [hashtable]$Request,
        [Parameter(Mandatory = $true)]
        [string]$Id,
        [Parameter(Mandatory = $true)]
        [int]$TimeoutMs
    )

    Set-StrictMode -Version Latest
    if ($Request.op -cnotin @('HELLO', 'GET_STATUS', 'GET_OBSERVATIONS', 'GET_TELEMETRY_STATUS')) {
        throw 'This IPC client permits only read-only status, lifecycle and observation operations.'
    }
    $utf8 = New-Object System.Text.UTF8Encoding($false, $true)
    $bytes = $utf8.GetBytes(($Request | ConvertTo-Json -Depth 8 -Compress) + "`n")
    if ($bytes.Length -gt 16384) { throw 'ComCon request exceeds the frame byte limit.' }
    $deadline = New-Object System.Threading.CancellationTokenSource
    $frame = New-Object System.IO.MemoryStream
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $stage = 'write'
    try {
        $deadline.CancelAfter($TimeoutMs)
        $write = $Pipe.WriteAsync($bytes, 0, $bytes.Length, $deadline.Token)
        # Framework pipe reads may not interrupt pending I/O on token cancellation.
        if (-not $write.Wait([Math]::Max(0, $TimeoutMs - [int]$clock.ElapsedMilliseconds))) {
            $deadline.Cancel()
            throw [System.OperationCanceledException]::new()
        }
        [void]$write.GetAwaiter().GetResult()
        $stage = 'read'
        $buffer = New-Object byte[] 1024
        while ($true) {
            $deadline.Token.ThrowIfCancellationRequested()
            $read = $Pipe.ReadAsync($buffer, 0, $buffer.Length, $deadline.Token)
            if (-not $read.Wait([Math]::Max(0, $TimeoutMs - [int]$clock.ElapsedMilliseconds))) {
                $deadline.Cancel()
                throw [System.OperationCanceledException]::new()
            }
            $count = $read.GetAwaiter().GetResult()
            if ($count -eq 0) { throw 'ComCon closed before sending a complete response.' }
            $newline = [Array]::IndexOf($buffer, [byte]10, 0, $count)
            $length = $count
            if ($newline -ge 0) { $length = $newline }
            if ($frame.Length + $length -gt 16383) { throw 'ComCon response exceeds the frame byte limit.' }
            $frame.Write($buffer, 0, $length)
            if ($newline -ge 0) {
                if ($newline -ne $count - 1) { throw 'ComCon sent unsolicited bytes after its response.' }
                break
            }
        }
        $stage = 'validate'
        $json = $utf8.GetString($frame.ToArray())
        if ($json.Length -eq 0 -or $json[0] -eq [char]0xFEFF) { throw 'ComCon response is empty or includes a BOM.' }
        if ($json.EndsWith("`r")) { $json = $json.Substring(0, $json.Length - 1) }
        if ($json.Contains("`r") -or $json.Contains("`n")) { throw 'ComCon response has an invalid line terminator.' }
        Assert-ComConResponseJson -Bytes $utf8.GetBytes($json)
        $response = $json | ConvertFrom-Json -ErrorAction Stop
        $names = @($response.PSObject.Properties.Name)
        $allowed = @('v', 'kind', 'id', 'ok', 'result', 'error')
        if ($names.Count -ne 5 -or @($names | Where-Object { $_ -cnotin $allowed }).Count -ne 0 -or
            $response.v -isnot [int] -or $response.v -ne 1 -or $response.kind -cne 'response' -or
            $response.id -isnot [string] -or $response.id -cne $Id -or $response.ok -isnot [bool] -or
            ($response.ok -and ('result' -cnotin $names -or 'error' -cin $names)) -or
            (-not $response.ok -and ('error' -cnotin $names -or 'result' -cin $names))) {
            throw 'ComCon response envelope is invalid.'
        }
        return $response
    }
    catch {
        $failure = $_.Exception
        while ($failure.InnerException) { $failure = $failure.InnerException }
        if ($failure -is [System.OperationCanceledException] -and $deadline.IsCancellationRequested) {
            $Pipe.Dispose()
            throw [System.TimeoutException]::new(
                "IPC_TIMEOUT: $($Request.op) $stage exceeded ${TimeoutMs}ms; received $($frame.Length) bytes.", $_.Exception)
        }
        throw
    }
    finally {
        Write-Verbose "IPC_EXCHANGE $($Request.op) stage=$stage bytes=$($frame.Length) elapsedMs=$($clock.ElapsedMilliseconds)"
        $deadline.Dispose()
        $frame.Dispose()
    }
}

function ConvertFrom-ComConDecimalId([object]$Text) {
    $number = [long]0
    if ($Text -isnot [string] -or $Text -cnotmatch '^(0|[1-9][0-9]{0,18})$' -or
        -not [long]::TryParse($Text, [System.Globalization.NumberStyles]::None,
            [System.Globalization.CultureInfo]::InvariantCulture, [ref]$number)) {
        throw 'ComCon observation cursor/identity is invalid.'
    }
    return $number
}

function Assert-ComConObservationBatch {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Batch,
        [Parameter(Mandatory = $true)][string]$AfterObservationId
    )

    Set-StrictMode -Version Latest
    $after = ConvertFrom-ComConDecimalId $AfterObservationId
    $next = ConvertFrom-ComConDecimalId $Batch.nextObservationId
    $latest = ConvertFrom-ComConDecimalId $Batch.latestObservationId
    $lifecycle = $Batch.implementation -ceq 'offlinePassiveLifecycleSimulation'
    [void](ConvertFrom-ComConDecimalId $Batch.profileGeneration)
    [void](ConvertFrom-ComConDecimalId $Batch.ownershipEpoch)
    if (($lifecycle -and ($Batch.captureId -isnot [string] -or [string]::IsNullOrWhiteSpace($Batch.captureId))) -or
        (-not $lifecycle -and ($Batch.implementation -cne 'offlinePassiveRxSimulation' -or
            $Batch.profileGeneration -cne '1' -or $Batch.ownershipEpoch -cne '0')) -or
        $Batch.source -cne 'simulatedPassiveRx' -or $Batch.safetyConfirmation -isnot [bool] -or
        $Batch.safetyConfirmation -or $Batch.gap -isnot [bool] -or $Batch.hasMore -isnot [bool] -or
        $Batch.observations -isnot [System.Array] -or $Batch.observations.Count -gt 8 -or
        $next -lt $after -or $latest -lt $next -or $Batch.hasMore -ne ($next -lt $latest) -or
        $Batch.profileGeneration -isnot [string] -or $Batch.ownershipEpoch -isnot [string]) {
        throw 'ComCon observation batch metadata is invalid.'
    }
    $previous = $after
    foreach ($observation in $Batch.observations) {
        $id = ConvertFrom-ComConDecimalId $observation.observationId
        if ($id -le $previous -or $id -gt $latest -or
            $observation.hex -isnot [string] -or $observation.hex -cnotmatch '^(?:[0-9A-F]{2}){1,256}$' -or
            ($observation.ageMs -isnot [int] -and $observation.ageMs -isnot [long]) -or
            $observation.ageMs -lt 0 -or $observation.source -cne 'simulatedPassiveRx' -or
            ($previous -eq $after -and -not $Batch.gap -and $id -ne $after + 1) -or
            ($previous -ne $after -and $id -ne $previous + 1)) {
            throw 'ComCon observation identity, continuity, payload or age is invalid.'
        }
        $previous = $id
    }
    if (($Batch.observations.Count -gt 0 -and $previous -ne $next) -or
        ($Batch.observations.Count -eq 0 -and
            ($next -ne $latest -or (-not $Batch.gap -and $next -ne $after)))) {
        throw 'ComCon observation batch cursor is inconsistent.'
    }
}

function Assert-ComConResponseJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes
    )

    Set-StrictMode -Version Latest
    Add-Type -AssemblyName System.Runtime.Serialization
    $quotas = New-Object System.Xml.XmlDictionaryReaderQuotas
    $quotas.MaxDepth = 10
    $quotas.MaxStringContentLength = 16384
    $reader = [System.Runtime.Serialization.Json.JsonReaderWriterFactory]::CreateJsonReader($Bytes, $quotas)
    $levels = @{}
    try {
        while ($reader.Read()) {
            if ($reader.NodeType -eq [System.Xml.XmlNodeType]::Element) {
                $depth = $reader.Depth
                $type = $reader.GetAttribute('type')
                if ($depth -eq 0 -and $type -cne 'object') { throw 'ComCon response root must be an object.' }
                if (($type -ceq 'object' -or $type -ceq 'array') -and $depth -ge 8) {
                    throw 'ComCon response exceeds JSON depth 8.'
                }
                if ($depth -gt 0 -and $levels[$depth - 1].Type -ceq 'object') {
                    $name = $reader.LocalName
                    if ($reader.NamespaceURI -ceq 'item') { $name = $reader.GetAttribute('item') }
                    if (-not $levels[$depth - 1].Names.Add($name)) {
                        throw "ComCon response contains duplicate property '$name'."
                    }
                    if ($depth -eq 1) {
                        $expected = @{
                            v = 'number'; kind = 'string'; id = 'string'
                            ok = 'boolean'; result = 'object'; error = 'object'
                        }
                        if (-not $expected.ContainsKey($name) -or $expected[$name] -cne $type) {
                            throw "ComCon response property '$name' is unknown or has an invalid type."
                        }
                    }
                }
                $levels[$depth] = @{
                    Type = $type
                    Names = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
                }
            }
            elseif ($reader.NodeType -eq [System.Xml.XmlNodeType]::EndElement) {
                $levels.Remove($reader.Depth)
            }
        }
    }
    finally {
        $reader.Dispose()
    }
}
