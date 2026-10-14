[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CatPipeName,
    [Parameter(Mandatory = $true)][string]$ControlPipeName,
    [Parameter(Mandatory = $true)][string]$RigProfileUuid,
    [string]$CommandList = 'Frequency,Mode,Ptt,RfPower',
    [ValidateRange(50, 30000)][int]$ConnectTimeoutMs = 3000,
    [ValidateRange(50, 30000)][int]$SetupTimeoutMs = 3000,
    [ValidateRange(50, 30000)][int]$CatReplyTimeoutMs = 4000,
    [ValidateRange(50, 30000)][int]$NotificationAckTimeoutMs = 1000
)

$ErrorActionPreference = 'Stop'
if ($null -eq ('HscWindowsMonotonicClock' -as [type])) {
    Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
public static class HscWindowsMonotonicClock {
    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern ulong GetTickCount64();
    public static Task ConnectAsync(NamedPipeClientStream stream, int timeoutMs) {
        return Task.Factory.StartNew(
            () => stream.Connect(timeoutMs),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }
}
'@
}
if ($null -eq ('HscComConJsonScanner' -as [type])) {
    # Compiled port of the strict property scan: rejects bad strings/escapes, unpaired surrogates and
    # duplicate keys per object. A per-character PowerShell loop cost ~6 ms per envelope.
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Text;
public static class HscComConJsonScanner {
    private sealed class Context {
        public bool IsObject; public bool ExpectKey; public HashSet<string> Keys;
    }
    private static bool IsHex(char c) {
        return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }
    private static string DecodeKey(string json, int start, int end) {
        StringBuilder sb = new StringBuilder(end - start);
        int i = start;
        while (i < end) {
            char c = json[i];
            if (c != '\\') { sb.Append(c); i++; continue; }
            char e = json[i + 1];
            switch (e) {
                case 'u': sb.Append((char)Convert.ToInt32(json.Substring(i + 2, 4), 16)); i += 6; continue;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                default: sb.Append(e); break;
            }
            i += 2;
        }
        return sb.ToString();
    }
    public static string Validate(string json) {
        List<Context> contexts = new List<Context>();
        int i = 0;
        int length = json.Length;
        while (i < length) {
            char c = json[i];
            if (c == '"') {
                int tokenStart = i;
                i++;
                bool closed = false;
                while (i < length) {
                    c = json[i];
                    if (c == '"') { i++; closed = true; break; }
                    if (c < 0x20) return "INVALID_JSON: Unescaped control character.";
                    if (c != '\\') { i++; continue; }
                    if (i + 1 >= length) return "INVALID_JSON: Incomplete JSON escape.";
                    char e = json[i + 1];
                    if (e == 'u') {
                        if (i + 5 >= length) return "INVALID_JSON: Incomplete Unicode escape.";
                        if (!IsHex(json[i + 2]) || !IsHex(json[i + 3]) || !IsHex(json[i + 4]) || !IsHex(json[i + 5]))
                            return "INVALID_JSON: Invalid Unicode escape.";
                        int code = Convert.ToInt32(json.Substring(i + 2, 4), 16);
                        if (code >= 0xD800 && code <= 0xDBFF) {
                            if (i + 11 >= length || json[i + 6] != '\\' || json[i + 7] != 'u')
                                return "INVALID_JSON: Unpaired high surrogate.";
                            char l0 = json[i + 8], l1 = json[i + 9];
                            if ((l0 != 'd' && l0 != 'D') || !((l1 >= 'c' && l1 <= 'f') || (l1 >= 'C' && l1 <= 'F')) ||
                                !IsHex(json[i + 10]) || !IsHex(json[i + 11]))
                                return "INVALID_JSON: Invalid low surrogate.";
                            i += 12;
                        } else if (code >= 0xDC00 && code <= 0xDFFF) {
                            return "INVALID_JSON: Unpaired low surrogate.";
                        } else { i += 6; }
                        continue;
                    }
                    if (e != '"' && e != '\\' && e != '/' && e != 'b' && e != 'f' && e != 'n' && e != 'r' && e != 't')
                        return "INVALID_JSON: Invalid JSON escape.";
                    i += 2;
                }
                if (!closed) return "INVALID_JSON: Unterminated JSON string.";
                if (contexts.Count > 0) {
                    Context context = contexts[contexts.Count - 1];
                    if (context.IsObject && context.ExpectKey) {
                        string key = DecodeKey(json, tokenStart + 1, i - 1);
                        if (!context.Keys.Add(key)) return "DUPLICATE_FIELD: Duplicate JSON property '" + key + "'.";
                        context.ExpectKey = false;
                    }
                }
                continue;
            }
            if (c == '{' || c == '[') {
                if (contexts.Count > 0) {
                    Context parent = contexts[contexts.Count - 1];
                    if (parent.IsObject) parent.ExpectKey = false;
                }
                Context next = new Context();
                next.IsObject = c == '{';
                next.ExpectKey = next.IsObject;
                if (next.IsObject) next.Keys = new HashSet<string>(StringComparer.Ordinal);
                contexts.Add(next);
            } else if ((c == '}' || c == ']') && contexts.Count > 0) {
                contexts.RemoveAt(contexts.Count - 1);
            } else if (c == ',' && contexts.Count > 0) {
                Context context = contexts[contexts.Count - 1];
                if (context.IsObject) context.ExpectKey = true;
            }
            i++;
        }
        return null;
    }
}
'@
}
function Get-ComConClientTimeouts {
    return @{
        ConnectTimeoutMs = 3000
        SetupTimeoutMs = 3000
        CatReplyTimeoutMs = 4000
        NotificationAckTimeoutMs = 1000
    }
}

$script:ContractVersion = 'comcon-ipc-v1'
$script:StrictUtf8 = New-Object System.Text.UTF8Encoding($false, $true)
$script:CatCommands = [ordered]@{
    Frequency = [byte[]]@(0xFE, 0xFE, 0x7A, 0xE1, 0x03, 0xFD)
    Mode      = [byte[]]@(0xFE, 0xFE, 0x7A, 0xE1, 0x04, 0xFD)
    Ptt       = [byte[]]@(0xFE, 0xFE, 0x7A, 0xE1, 0x1C, 0x00, 0xFD)
    RfPower   = [byte[]]@(0xFE, 0xFE, 0x7A, 0xE1, 0x14, 0x0A, 0xFD)
}

function Assert-ComConRecordOutcome($Notification, [int]$ExpectedSequence) {
    $messageType = Get-ComConProperty $Notification 'messageType'
    $sequence = Get-ComConProperty $Notification 'recordSequence'
    $delivery = Get-ComConProperty $Notification 'deliveryStatus'
    if ($sequence -isnot [string] -or $sequence -cne ([string]$ExpectedSequence)) {
        throw 'CAT_RECORD_SEQUENCE_MISMATCH: CAT write outcome did not match the current request.'
    }
    if ($messageType -cne 'record.outcome' -or $delivery -cne 'WRITE_SUCCEEDED') {
        $reason = Get-ComConProperty $Notification 'reason'
        if ($delivery -ceq 'UNKNOWN_DELIVERY') {
            throw "UNKNOWN_DELIVERY: ComCon could not determine whether CAT record $sequence was written. $reason"
        }
        throw "CAT_RECORD_NOT_SENT: ComCon did not successfully write CAT record $sequence. $reason"
    }
}

function Wait-ComConRecordOutcome(
    [System.IO.Stream]$ControlPipe,
    [int]$ProfileId,
    [string]$SessionId,
    [int]$AckMs,
    $Result,
    $PendingControl,
    [int]$ExpectedSequence,
    [long]$TransactionDeadlineTick64 = 0,
    $HeartbeatState = $null) {
    foreach ($notification in @($Result.notifications)) {
        if ($notification.messageType -like 'record.*' -and
            $notification.recordSequence -ceq ([string]$ExpectedSequence)) {
            Assert-ComConRecordOutcome $notification $ExpectedSequence
            return $PendingControl
        }
    }

    while ($true) {
        $notification = Get-ComConNotification $ControlPipe $PendingControl $AckMs `
            $TransactionDeadlineTick64 $HeartbeatState
        if (Send-ComConNotificationAck $ControlPipe $notification $ProfileId $SessionId $AckMs `
            $Result $TransactionDeadlineTick64 $HeartbeatState) {
            throw 'SESSION_ENDED: ComCon ended the session before confirming the CAT write.'
        }
        if ((Get-ComConProperty $notification 'messageType') -like 'record.*') {
            Assert-ComConRecordOutcome $notification $ExpectedSequence
            return (Start-ComConControlRead $ControlPipe $PendingControl)
        }
        $PendingControl = Start-ComConControlRead $ControlPipe $PendingControl
    }
}

function Get-ComConProperty($Object, [string]$Name) {
    $property = @($Object.PSObject.Properties | Where-Object { $_.Name -ceq $Name })
    if ($property.Count -ne 1) { return $null }
    return $property[0].Value
}

function Invoke-ComConSafetyPttOff {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RadioAddress,
        [Parameter(Mandatory = $true)][string]$ControllerAddress,
        [Parameter(Mandatory = $true)][string]$RigProfileUuid,
        [ValidateRange(50, 30000)][int]$ConnectTimeoutMs = (Get-ComConClientTimeouts).ConnectTimeoutMs,
        [ValidateRange(50, 30000)][int]$SetupTimeoutMs = (Get-ComConClientTimeouts).SetupTimeoutMs,
        [ValidateRange(50, 30000)][int]$CatReplyTimeoutMs = (Get-ComConClientTimeouts).CatReplyTimeoutMs,
        [ValidateRange(50, 30000)][int]$NotificationAckTimeoutMs = (Get-ComConClientTimeouts).NotificationAckTimeoutMs,
        [Parameter(Mandatory = $true)][long]$TransactionDeadlineTick64,
        [scriptblock]$OnHeartbeat,
        [scriptblock]$OnStage
    )
    try {
        if (-not (Test-ComConCanonicalUuid $RigProfileUuid)) {
            throw 'INVALID_RIG_PROFILE_ID: RigProfileUuid must be canonical lowercase UUID.'
        }
        if ($RadioAddress -cnotmatch '^[0-9A-Fa-f]{2}$' -or
            $ControllerAddress -cnotmatch '^[0-9A-Fa-f]{2}$') {
            throw 'INVALID_ADDRESS: Radio and controller addresses must be two hexadecimal digits.'
        }
        $radio = [Convert]::ToByte($RadioAddress, 16)
        $controller = [Convert]::ToByte($ControllerAddress, 16)
        if ($radio -in @(0x00, 0xE0) -or $controller -in @(0x00, 0xE0) -or $radio -eq $controller) {
            throw 'INVALID_ADDRESS: Safety command addresses are reserved or identical.'
        }
        $remainingTransactionMs = $TransactionDeadlineTick64 - (Get-ComConMonotonicMilliseconds)
        if ($remainingTransactionMs -le 0) {
            throw 'TRANSACTION_DEADLINE: The shared safety transaction deadline expired before dispatch.'
        }
        if ($remainingTransactionMs -gt 5000) {
            throw 'INVALID_TRANSACTION_DEADLINE: Safety transaction deadline exceeds the 5,000 ms contract.'
        }
        $result = Read-ComConSession $ConnectTimeoutMs $SetupTimeoutMs $CatReplyTimeoutMs `
            $NotificationAckTimeoutMs $RigProfileUuid @() $OnStage -SafetyPttOff -Radio $radio -Controller $controller `
            -TransactionDeadlineTick64 $TransactionDeadlineTick64 -OnHeartbeat $OnHeartbeat
        return [pscustomobject]$result
    } catch {
        $message = ($_.Exception.Message -replace '[\r\n]+', ' ').Trim()
        $code = if ($message -match '^([A-Z_]+):') { $Matches[1] } else { 'COMMAND_REFUSED' }
        return [pscustomobject]@{
            status = 'ERROR'; profileId = $null; sessionId = ''; commands = [ordered]@{}
            safety = $null; notifications = @(); errorCode = $code; error = $message
        }
    }
}

function Invoke-ComConStage([scriptblock]$OnStage, [string]$Stage) {
    if ($null -ne $OnStage) { & $OnStage $Stage }
}

function Get-ComConMonotonicMilliseconds {
    return [long][HscWindowsMonotonicClock]::GetTickCount64()
}

function Assert-ComConTransactionActive([long]$TransactionDeadlineTick64) {
    if ($TransactionDeadlineTick64 -gt 0 -and
        (Get-ComConMonotonicMilliseconds) -ge $TransactionDeadlineTick64) {
        throw [System.TimeoutException]::new('TRANSACTION_DEADLINE: The shared safety transaction deadline expired.')
    }
}

function Get-ComConPhaseDeadline([int]$PhaseTimeoutMs, [long]$TransactionDeadlineTick64 = 0) {
    Assert-ComConTransactionActive $TransactionDeadlineTick64
    $deadline = (Get-ComConMonotonicMilliseconds) + $PhaseTimeoutMs
    if ($TransactionDeadlineTick64 -gt 0) {
        $deadline = [Math]::Min($deadline, $TransactionDeadlineTick64)
    }
    return [long]$deadline
}

function Assert-ComConWaitActive(
    [long]$PhaseDeadlineTick64,
    [long]$TransactionDeadlineTick64,
    [string]$PhaseTimeoutMessage) {
    $now = Get-ComConMonotonicMilliseconds
    if ($TransactionDeadlineTick64 -gt 0 -and $now -ge $TransactionDeadlineTick64) {
        Assert-ComConTransactionActive $TransactionDeadlineTick64
    }
    if ($now -ge $PhaseDeadlineTick64) {
        throw [System.TimeoutException]::new($PhaseTimeoutMessage)
    }
}

function Invoke-ComConHeartbeatIfDue($HeartbeatState) {
    if ($null -eq $HeartbeatState -or $null -eq $HeartbeatState.Callback) { return }
    $now = Get-ComConMonotonicMilliseconds
    if (($now - [long]$HeartbeatState.LastTick64) -ge 500) {
        & $HeartbeatState.Callback
        $HeartbeatState.LastTick64 = $now
    }
}

function Get-ComConAsyncWaitSlice([int]$RemainingMs, $HeartbeatState) {
    $slice = [Math]::Min(500, $RemainingMs)
    if ($null -ne $HeartbeatState -and $null -ne $HeartbeatState.Callback) {
        Invoke-ComConHeartbeatIfDue $HeartbeatState
        $untilHeartbeat = 500 - (Get-ComConMonotonicMilliseconds - [long]$HeartbeatState.LastTick64)
        $slice = [Math]::Min($slice, [Math]::Max(1, $untilHeartbeat))
    }
    return [int]$slice
}

function Wait-ComConAsyncOperation(
    [System.IAsyncResult]$Async,
    [long]$PhaseDeadlineTick64,
    [long]$TransactionDeadlineTick64,
    $HeartbeatState,
    [string]$PhaseTimeoutMessage = 'IPC operation deadline expired.') {
    while ($true) {
        Assert-ComConWaitActive $PhaseDeadlineTick64 $TransactionDeadlineTick64 $PhaseTimeoutMessage
        $now = Get-ComConMonotonicMilliseconds
        if ($now -ge $PhaseDeadlineTick64) {
            throw [System.TimeoutException]::new($PhaseTimeoutMessage)
        }
        $remaining = [int][Math]::Min([long]::MaxValue, $PhaseDeadlineTick64 - $now)
        if ($TransactionDeadlineTick64 -gt 0) {
            $remaining = [int][Math]::Min($remaining, $TransactionDeadlineTick64 - $now)
        }
        $slice = Get-ComConAsyncWaitSlice $remaining $HeartbeatState
        if ($Async.AsyncWaitHandle.WaitOne($slice)) {
            Assert-ComConWaitActive $PhaseDeadlineTick64 $TransactionDeadlineTick64 $PhaseTimeoutMessage
            return
        }
        Invoke-ComConHeartbeatIfDue $HeartbeatState
    }
}

function Wait-ComConTaskOperation(
    [System.Threading.Tasks.Task]$Task,
    [long]$PhaseDeadlineTick64,
    [long]$TransactionDeadlineTick64,
    $HeartbeatState,
    [string]$PhaseTimeoutMessage = 'IPC operation deadline expired.') {
    while ($true) {
        Assert-ComConWaitActive $PhaseDeadlineTick64 $TransactionDeadlineTick64 $PhaseTimeoutMessage
        $now = Get-ComConMonotonicMilliseconds
        if ($now -ge $PhaseDeadlineTick64) {
            throw [System.TimeoutException]::new($PhaseTimeoutMessage)
        }
        $remaining = [int]($PhaseDeadlineTick64 - $now)
        if ($TransactionDeadlineTick64 -gt 0) {
            $remaining = [int][Math]::Min($remaining, $TransactionDeadlineTick64 - $now)
        }
        $slice = Get-ComConAsyncWaitSlice $remaining $HeartbeatState
        $completed = $false
        try { $completed = $Task.Wait($slice) }
        catch [System.AggregateException] {
            Assert-ComConWaitActive $PhaseDeadlineTick64 $TransactionDeadlineTick64 $PhaseTimeoutMessage
            $Task.GetAwaiter().GetResult()
            return
        }
        if ($completed) {
            Assert-ComConWaitActive $PhaseDeadlineTick64 $TransactionDeadlineTick64 $PhaseTimeoutMessage
            $Task.GetAwaiter().GetResult()
            return
        }
        Invoke-ComConHeartbeatIfDue $HeartbeatState
    }
}

function Wait-ComConAnyOperation(
    [System.Threading.WaitHandle[]]$Handles,
    [long]$PhaseDeadlineTick64,
    [long]$TransactionDeadlineTick64,
    $HeartbeatState,
    [string]$PhaseTimeoutMessage = 'IPC operation deadline expired.') {
    while ($true) {
        Assert-ComConWaitActive $PhaseDeadlineTick64 $TransactionDeadlineTick64 $PhaseTimeoutMessage
        $now = Get-ComConMonotonicMilliseconds
        $remaining = [int]($PhaseDeadlineTick64 - $now)
        if ($TransactionDeadlineTick64 -gt 0) {
            $remaining = [int][Math]::Min($remaining, $TransactionDeadlineTick64 - $now)
        }
        $slice = Get-ComConAsyncWaitSlice $remaining $HeartbeatState
        $index = [System.Threading.WaitHandle]::WaitAny($Handles, $slice)
        if ($index -ne [System.Threading.WaitHandle]::WaitTimeout) {
            Assert-ComConWaitActive $PhaseDeadlineTick64 $TransactionDeadlineTick64 $PhaseTimeoutMessage
            return $index
        }
        Invoke-ComConHeartbeatIfDue $HeartbeatState
    }
}

function Assert-ComConFields($Object, [string[]]$Required, [string[]]$Optional = @()) {
    if ($null -eq $Object -or $Object -isnot [System.Management.Automation.PSCustomObject]) {
        throw 'INVALID_JSON: ComCon envelope root must be an object.'
    }
    $allowed = @('messageType', 'contractVersion') + $Required + $Optional
    foreach ($property in $Object.PSObject.Properties) {
        if ($property.Name -cnotin $allowed) { throw "UNKNOWN_FIELD: Unexpected field '$($property.Name)'." }
        if ($property.Name -ceq 'profileId') {
            if ($property.Value -isnot [int] -or $property.Value -lt 1 -or $property.Value -gt 4) {
                throw 'INVALID_PROFILE_ID: profileId must be an integer from 1 through 4.'
            }
        } elseif ($property.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($property.Value)) {
            throw "INVALID_FIELD: '$($property.Name)' must be a nonempty string."
        }
    }
    foreach ($field in @('messageType', 'contractVersion') + $Required) {
        if (@($Object.PSObject.Properties | Where-Object { $_.Name -ceq $field }).Count -ne 1) {
            throw "MISSING_FIELD: Missing '$field'."
        }
    }
}

function Test-ComConJsonProperties([string]$Json) {
    $failure = [HscComConJsonScanner]::Validate($Json)
    if ($null -ne $failure) { throw $failure }
}

function Read-ComConJsonStrict([byte[]]$Payload) {
    try { $json = $script:StrictUtf8.GetString($Payload) }
    catch { throw 'INVALID_UTF8: ComCon envelope is not strict UTF-8.' }
    Test-ComConJsonProperties $json
    try { $value = ConvertFrom-Json -InputObject $json -ErrorAction Stop }
    catch { throw 'INVALID_JSON: ComCon envelope is invalid JSON.' }
    if ($value -isnot [System.Management.Automation.PSCustomObject]) {
        throw 'INVALID_JSON: ComCon envelope root must be an object.'
    }
    return $value
}

function Read-ComConExact([System.IO.Stream]$Stream, [int]$Count, [long]$Deadline,
    [long]$TransactionDeadlineTick64 = 0, $HeartbeatState = $null,
    [string]$PhaseTimeoutMessage = 'IPC read deadline expired.') {
    $bytes = New-Object byte[] $Count
    $offset = 0
    while ($offset -lt $Count) {
        $async = $Stream.BeginRead($bytes, $offset, $Count - $offset, $null, $null)
        Wait-ComConAsyncOperation $async $Deadline $TransactionDeadlineTick64 $HeartbeatState $PhaseTimeoutMessage
        $read = $Stream.EndRead($async)
        if ($read -le 0) { throw [System.IO.EndOfStreamException]::new('ComCon closed the IPC stream.') }
        $offset += $read
    }
    return ,$bytes
}

function Write-ComConBytes([System.IO.Stream]$Stream, [byte[]]$Bytes, [int]$TimeoutMs,
    [string]$Stage = 'frame', [long]$TransactionDeadlineTick64 = 0,
    $HeartbeatState = $null, [long]$PhaseDeadlineTick64 = 0) {
    Assert-ComConTransactionActive $TransactionDeadlineTick64
    if ($PhaseDeadlineTick64 -le 0) {
        $PhaseDeadlineTick64 = Get-ComConPhaseDeadline $TimeoutMs $TransactionDeadlineTick64
    }
    Assert-ComConTransactionActive $TransactionDeadlineTick64
    $async = $Stream.BeginWrite($Bytes, 0, $Bytes.Length, $null, $null)
    Wait-ComConAsyncOperation $async $PhaseDeadlineTick64 $TransactionDeadlineTick64 `
        $HeartbeatState "IPC write deadline expired at ${Stage}."
    try {
        $Stream.EndWrite($async)
        if ($TransactionDeadlineTick64 -gt 0) {
            $flushTask = $Stream.FlushAsync()
            Wait-ComConTaskOperation $flushTask $PhaseDeadlineTick64 $TransactionDeadlineTick64 `
                $HeartbeatState "IPC flush deadline expired at ${Stage}."
        } else {
            $Stream.Flush()
        }
    }
    catch {
        if ($_.Exception.Message -like 'TRANSACTION_DEADLINE:*') { throw }
        throw "IPC write failed at ${Stage}: $($_.Exception.Message)"
    }
}
function Write-ComConEnvelope([System.IO.Stream]$Stream, $Envelope, [int]$TimeoutMs,
    [long]$TransactionDeadlineTick64 = 0, $HeartbeatState = $null,
    [long]$PhaseDeadlineTick64 = 0) {
    $json = ConvertTo-Json -InputObject $Envelope -Compress -Depth 8
    $payload = $script:StrictUtf8.GetBytes($json)
    if ($payload.Length -lt 1 -or $payload.Length -gt 16384) { throw 'FRAME_TOO_LARGE: IPC envelope payload length is invalid.' }
    $prefix = [System.BitConverter]::GetBytes([uint32]$payload.Length)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($prefix) }
    $frame = New-Object byte[] (4 + $payload.Length)
    [Array]::Copy($prefix, 0, $frame, 0, 4)
    [Array]::Copy($payload, 0, $frame, 4, $payload.Length)
    Write-ComConBytes $Stream $frame $TimeoutMs ("envelope " + $Envelope.messageType) `
        $TransactionDeadlineTick64 $HeartbeatState $PhaseDeadlineTick64
}

function Read-ComConEnvelope([System.IO.Stream]$Stream, [long]$Deadline, [byte]$FirstPrefixByte = 255,
    [long]$TransactionDeadlineTick64 = 0, $HeartbeatState = $null,
    [string]$PhaseTimeoutMessage = 'IPC read deadline expired.') {
    $prefix = New-Object byte[] 4
    if ($FirstPrefixByte -ne 255) { $prefix[0] = $FirstPrefixByte; $start = 1 } else { $start = 0 }
    for ($i = $start; $i -lt 4; $i++) {
        $prefix[$i] = (Read-ComConExact $Stream 1 $Deadline $TransactionDeadlineTick64 `
            $HeartbeatState $PhaseTimeoutMessage)[0]
    }
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($prefix) }
    $length = [BitConverter]::ToUInt32($prefix, 0)
    if ($length -lt 1 -or $length -gt 16384) { throw 'FRAME_TOO_LARGE: Invalid ComCon envelope length.' }
    return Read-ComConJsonStrict (Read-ComConExact $Stream ([int]$length) $Deadline `
        $TransactionDeadlineTick64 $HeartbeatState $PhaseTimeoutMessage)
}

function Assert-ComConBase($Envelope, [string]$MessageType) {
    $messageType = Get-ComConProperty $Envelope 'messageType'
    $version = Get-ComConProperty $Envelope 'contractVersion'
    if ($messageType -isnot [string] -or $messageType -cne $MessageType) { throw "INVALID_MESSAGE_TYPE: Expected '$MessageType'." }
    if ($version -isnot [string] -or $version -cne $script:ContractVersion) { throw 'UNSUPPORTED_VERSION: ComCon contract version mismatch.' }
}

function Test-ComConCanonicalUuid([string]$Value) {
    return $Value -cmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
}

function Limit-ComConSessionNotifications([System.Collections.IDictionary]$Session) {
    $notifications = @($Session.Result.notifications)
    if ($notifications.Count -gt 64) {
        $Session.Result.notifications = @($notifications[($notifications.Count - 64)..($notifications.Count - 1)])
    }
}

function Invoke-ComConSessionReadOnlyBatch(
    [System.Collections.IDictionary]$Session, [string[]]$Commands,
    [scriptblock]$OnStage = $null, [long]$TransactionDeadlineTick64 = 0,
    [scriptblock]$OnHeartbeat = $null) {
    if ($Session.Closed) { throw 'SESSION_CLOSED: ComCon session is already closed.' }
    $batch = [ordered]@{ status = 'ERROR'; commands = [ordered]@{}; errorCode = ''; error = '' }
    $heartbeatState = if ($null -ne $OnHeartbeat) {
        @{ Callback = $OnHeartbeat; LastTick64 = Get-ComConMonotonicMilliseconds }
    } else { $null }
    foreach ($commandName in $Commands) {
        if ($commandName -cnotin $script:CatCommands.Keys) {
            throw "COMMAND_REFUSED: '$commandName' is not in the read-only command allow-list."
        }
        $command = $script:CatCommands[$commandName]
        $stageName = $commandName.ToUpperInvariant()
        Invoke-ComConStage $OnStage ('CAT_WRITE_START_' + $stageName)
        Write-ComConBytes $Session.CatPipe $command $Session.ReplyMs 'CAT command' `
            $TransactionDeadlineTick64 $heartbeatState
        Invoke-ComConStage $OnStage ('CAT_WRITE_COMPLETE_' + $stageName)
        Invoke-ComConStage $OnStage ('CAT_REPLY_WAIT_' + $stageName)
        $Session.RecordSequence++
        $frame = Read-ComConCatFrameWithNotifications $Session.CatPipe $Session.ControlPipe `
            $Session.ProfileId $Session.SessionId $command $Session.ReplyMs $Session.AckMs `
            $Session.Result $Session.PendingControl 'Reply' $Session.RecordSequence `
            $TransactionDeadlineTick64 $heartbeatState
        $Session.PendingControl = $frame.PendingControl
        $Session.PendingControl = Wait-ComConRecordOutcome $Session.ControlPipe `
            $Session.ProfileId $Session.SessionId $Session.AckMs $Session.Result `
            $Session.PendingControl $Session.RecordSequence $TransactionDeadlineTick64 $heartbeatState
        Limit-ComConSessionNotifications $Session
        $batch.commands[$commandName] = [ordered]@{
            status = 'OK'
            replyHex = (($frame.Bytes | ForEach-Object { $_.ToString('X2') }) -join ' ')
            receivedUnixMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        }
        Invoke-ComConStage $OnStage ('CAT_REPLY_RECEIVED_' + $stageName)
    }
    $batch.status = 'OK'
    return [pscustomobject]$batch
}

function Invoke-ComConSessionSafetyPttOff(
    [System.Collections.IDictionary]$Session, [byte]$Radio, [byte]$Controller,
    [scriptblock]$OnStage = $null, [long]$TransactionDeadlineTick64 = 0,
    [scriptblock]$OnHeartbeat = $null) {
    if ($Session.Closed) { throw 'SESSION_CLOSED: ComCon session is already closed.' }
    $heartbeatState = if ($null -ne $OnHeartbeat) {
        @{ Callback = $OnHeartbeat; LastTick64 = Get-ComConMonotonicMilliseconds }
    } else { $null }
    $pttOff = [byte[]]@(0xFE, 0xFE, $Radio, $Controller, 0x1C, 0x00, 0x00, 0xFD)
    $pttRead = [byte[]]@(0xFE, 0xFE, $Radio, $Controller, 0x1C, 0x00, 0xFD)
    Invoke-ComConStage $OnStage 'CAT_WRITE_START_PTT_OFF'
    Write-ComConBytes $Session.CatPipe $pttOff $Session.ReplyMs 'PTT_OFF CAT command' `
        $TransactionDeadlineTick64 $heartbeatState
    Invoke-ComConStage $OnStage 'CAT_WRITE_COMPLETE_PTT_OFF'
    Invoke-ComConStage $OnStage 'CAT_REPLY_WAIT_PTT_OFF_ACK'
    $Session.RecordSequence++
    $offAck = Read-ComConCatFrameWithNotifications $Session.CatPipe $Session.ControlPipe `
        $Session.ProfileId $Session.SessionId $pttOff $Session.ReplyMs $Session.AckMs `
        $Session.Result $Session.PendingControl 'Acknowledgement' $Session.RecordSequence `
        $TransactionDeadlineTick64 $heartbeatState
    $Session.PendingControl = $offAck.PendingControl
    $Session.PendingControl = Wait-ComConRecordOutcome $Session.ControlPipe `
        $Session.ProfileId $Session.SessionId $Session.AckMs $Session.Result `
        $Session.PendingControl $Session.RecordSequence $TransactionDeadlineTick64 $heartbeatState
    Limit-ComConSessionNotifications $Session

    Invoke-ComConStage $OnStage 'CAT_WRITE_START_PTT_READBACK'
    Write-ComConBytes $Session.CatPipe $pttRead $Session.ReplyMs 'PTT readback CAT command' `
        $TransactionDeadlineTick64 $heartbeatState
    Invoke-ComConStage $OnStage 'CAT_WRITE_COMPLETE_PTT_READBACK'
    Invoke-ComConStage $OnStage 'CAT_REPLY_WAIT_PTT_READBACK'
    $Session.RecordSequence++
    $readback = Read-ComConCatFrameWithNotifications $Session.CatPipe $Session.ControlPipe `
        $Session.ProfileId $Session.SessionId $pttRead $Session.ReplyMs $Session.AckMs `
        $Session.Result $Session.PendingControl 'Reply' $Session.RecordSequence `
        $TransactionDeadlineTick64 $heartbeatState
    $Session.PendingControl = $readback.PendingControl
    $Session.PendingControl = Wait-ComConRecordOutcome $Session.ControlPipe `
        $Session.ProfileId $Session.SessionId $Session.AckMs $Session.Result `
        $Session.PendingControl $Session.RecordSequence $TransactionDeadlineTick64 $heartbeatState
    Limit-ComConSessionNotifications $Session
    if ($readback.Bytes.Length -ne 8) {
        throw 'INVALID_PTT_READBACK: PTT readback frame did not contain exactly one state byte.'
    }
    if ($readback.Bytes[6] -notin @(0x00, 0x01)) {
        throw "INVALID_PTT_READBACK: Radio PTT readback value was $($readback.Bytes[6].ToString('X2'))."
    }
    if ($readback.Bytes[6] -eq 0x01) {
        throw "PTT_REMAINS_ACTIVE: Radio PTT readback was $($readback.Bytes[6].ToString('X2'))."
    }
    Invoke-ComConStage $OnStage 'CAT_REPLY_RECEIVED_PTT_READBACK'
    return [pscustomobject]@{
        status = 'OK'
        pttOffAckHex = (($offAck.Bytes | ForEach-Object { $_.ToString('X2') }) -join ' ')
        pttReadbackHex = (($readback.Bytes | ForEach-Object { $_.ToString('X2') }) -join ' ')
        pttReadbackReceivedUnixMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    }
}

function Read-ComConSession(
    [int]$ConnectMs, [int]$SetupMs, [int]$ReplyMs, [int]$AckMs, [string]$RigUuid, [string[]]$Commands,
    [scriptblock]$OnStage, [switch]$SafetyPttOff, [byte]$Radio = 0, [byte]$Controller = 0,
    [long]$TransactionDeadlineTick64 = 0, [scriptblock]$OnHeartbeat = $null,
    [scriptblock]$OnPersistentSession = $null, $PersistentContext = $null) {
    $result = [ordered]@{
        status = 'ERROR'; profileId = $null; sessionId = ''; commands = [ordered]@{}
        safety = $null; notifications = @(); errorCode = ''; error = ''
    }
    $catPipe = $null; $controlPipe = $null; $pendingControl = $null; $catClosed = $false
    $cleanupErrors = New-Object System.Collections.Generic.List[string]
    $connectTasks = New-Object 'System.Collections.Generic.List[System.Threading.Tasks.Task]'
    $heartbeatState = if ($null -ne $OnHeartbeat) {
        @{ Callback = $OnHeartbeat; LastTick64 = Get-ComConMonotonicMilliseconds }
    } else { $null }
    try {
        Assert-ComConTransactionActive $TransactionDeadlineTick64
        if (-not (Test-ComConCanonicalUuid $RigUuid)) { throw 'INVALID_RIG_PROFILE_ID: RigProfileUuid must be canonical lowercase UUID.' }
        foreach ($name in @($CatPipeName, $ControlPipeName)) {
            if ($name -cnotmatch '^[A-Za-z0-9_.-]{1,128}$') { throw 'INVALID_PIPE_NAME: Pipe name contains invalid characters.' }
        }
        if ($CatPipeName -ceq $ControlPipeName) { throw 'INVALID_PIPE_NAME: CAT and control pipes must differ.' }

        $options = [System.IO.Pipes.PipeOptions]::Asynchronous
        $catPipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $CatPipeName,
            [System.IO.Pipes.PipeDirection]::InOut, $options,
            [System.Security.Principal.TokenImpersonationLevel]::Identification)
        Invoke-ComConStage $OnStage 'CAT_CONNECTING'
        $connectDeadline = Get-ComConPhaseDeadline $ConnectMs $TransactionDeadlineTick64
        try {
            if ($TransactionDeadlineTick64 -gt 0 -or $null -ne $heartbeatState) {
                $connectRemaining = $connectDeadline - (Get-ComConMonotonicMilliseconds)
                if ($connectRemaining -le 0) {
                    Assert-ComConWaitActive $connectDeadline $TransactionDeadlineTick64 `
                        'CAT_CONNECT_TIMEOUT: CAT pipe connection deadline expired.'
                }
                $connectTimeout = [int]$connectRemaining
                $connectTask = [HscWindowsMonotonicClock]::ConnectAsync($catPipe, $connectTimeout)
                $connectTasks.Add($connectTask)
                Wait-ComConTaskOperation $connectTask $connectDeadline $TransactionDeadlineTick64 `
                    $heartbeatState 'CAT_CONNECT_TIMEOUT: CAT pipe connection deadline expired.'
            } else {
                $catPipe.Connect($ConnectMs)
            }
        }
        catch {
            Invoke-ComConStage $OnStage 'CAT_CONNECT_FAILED'
            if ($_.Exception.Message -notlike 'TRANSACTION_DEADLINE:*' -and
                $_.Exception.Message -notlike 'CAT_CONNECT_TIMEOUT:*') {
                throw "CAT_CONNECT_TIMEOUT: CAT pipe connection failed: $($_.Exception.Message)"
            }
            throw
        }
        Invoke-ComConStage $OnStage 'CAT_CONNECTED'
        $setupDeadline = Get-ComConPhaseDeadline $SetupMs $TransactionDeadlineTick64
        Invoke-ComConStage $OnStage 'CAT_HANDSHAKE'
        Write-ComConEnvelope $catPipe ([ordered]@{
            messageType = 'cat.handshake'; contractVersion = $script:ContractVersion; rigProfileId = $RigUuid
        }) $SetupMs $TransactionDeadlineTick64 $heartbeatState $setupDeadline
        $accepted = Read-ComConEnvelope $catPipe $setupDeadline 255 `
            $TransactionDeadlineTick64 $heartbeatState 'CONTROL_BIND_TIMEOUT: CAT handshake deadline expired.'
        if ((Get-ComConProperty $accepted 'messageType') -ceq 'cat.rejected') {
            Assert-ComConFields $accepted @('errorCode', 'reason')
            Assert-ComConBase $accepted 'cat.rejected'
            throw "$(Get-ComConProperty $accepted 'errorCode'): $(Get-ComConProperty $accepted 'reason')"
        }
        Assert-ComConFields $accepted @('profileId', 'sessionId')
        Assert-ComConBase $accepted 'cat.accepted'
        $profileId = Get-ComConProperty $accepted 'profileId'
        $sessionId = Get-ComConProperty $accepted 'sessionId'
        if ($profileId -isnot [int] -or $profileId -lt 1 -or $profileId -gt 4) { throw 'INVALID_PROFILE_ID: ComCon profileId is invalid.' }
        if (-not (Test-ComConCanonicalUuid $sessionId)) { throw 'INVALID_SESSION_ID: ComCon sessionId is invalid.' }
        $result.profileId = $profileId; $result.sessionId = $sessionId

        $controlPipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $ControlPipeName,
            [System.IO.Pipes.PipeDirection]::InOut, $options,
            [System.Security.Principal.TokenImpersonationLevel]::Identification)
        Invoke-ComConStage $OnStage 'CONTROL_CONNECTING'
        $remainingSetup = [int][Math]::Max(0, $setupDeadline - (Get-ComConMonotonicMilliseconds))
        if ($remainingSetup -le 0) {
            Assert-ComConTransactionActive $TransactionDeadlineTick64
            throw 'CONTROL_BIND_TIMEOUT: control pipe connection exceeded setup deadline.'
        }
        $controlConnectDeadline = [Math]::Min($setupDeadline,
            (Get-ComConMonotonicMilliseconds) + $ConnectMs)
        try {
            if ($TransactionDeadlineTick64 -gt 0 -or $null -ne $heartbeatState) {
                $controlConnectRemaining = $controlConnectDeadline - (Get-ComConMonotonicMilliseconds)
                if ($controlConnectRemaining -le 0) {
                    Assert-ComConWaitActive $controlConnectDeadline $TransactionDeadlineTick64 `
                        'CONTROL_BIND_TIMEOUT: control pipe connection exceeded setup deadline.'
                }
                $controlConnectTimeout = [int]$controlConnectRemaining
                $controlConnectTask = [HscWindowsMonotonicClock]::ConnectAsync(
                    $controlPipe, $controlConnectTimeout)
                $connectTasks.Add($controlConnectTask)
                Wait-ComConTaskOperation $controlConnectTask $controlConnectDeadline `
                    $TransactionDeadlineTick64 $heartbeatState `
                    'CONTROL_BIND_TIMEOUT: control pipe connection exceeded setup deadline.'
            } else {
                $controlPipe.Connect([Math]::Min($ConnectMs, $remainingSetup))
            }
        }
        catch {
            if ($_.Exception.Message -like 'TRANSACTION_DEADLINE:*') { throw }
            throw "CONTROL_BIND_TIMEOUT: control pipe connection failed: $($_.Exception.Message)"
        }
        Invoke-ComConStage $OnStage 'CONTROL_BIND'
        Write-ComConEnvelope $controlPipe ([ordered]@{
            messageType = 'control.bind'; contractVersion = $script:ContractVersion
            profileId = $profileId; sessionId = $sessionId
        }) $SetupMs $TransactionDeadlineTick64 $heartbeatState $setupDeadline
        try {
            $bound = Read-ComConEnvelope $controlPipe $setupDeadline 255 `
                $TransactionDeadlineTick64 $heartbeatState `
                'CONTROL_BIND_TIMEOUT: control.bound was not received before the setup deadline.'
        }
        catch [System.TimeoutException] {
            if ($_.Exception.Message -like 'TRANSACTION_DEADLINE:*') { throw }
            throw 'CONTROL_BIND_TIMEOUT: control.bound was not received before the setup deadline.'
        }
        if ((Get-ComConProperty $bound 'messageType') -ceq 'control.rejected') {
            Assert-ComConFields $bound @('errorCode', 'reason') @('profileId', 'sessionId')
            Assert-ComConBase $bound 'control.rejected'
            $rejectedProfile = Get-ComConProperty $bound 'profileId'
            $rejectedSession = Get-ComConProperty $bound 'sessionId'
            if (($null -eq $rejectedProfile) -ne ($null -eq $rejectedSession)) { throw 'INVALID_FIELD: control.rejected identity must include both fields or neither.' }
            if ($null -ne $rejectedProfile -and ($rejectedProfile -ne $profileId -or $rejectedSession -cne $sessionId)) {
                throw 'PROFILE_SESSION_MISMATCH: control.rejected identity differs.'
            }
            throw "$(Get-ComConProperty $bound 'errorCode'): $(Get-ComConProperty $bound 'reason')"
        }
        Assert-ComConFields $bound @('profileId', 'sessionId')
        Assert-ComConBase $bound 'control.bound'
        if ((Get-ComConProperty $bound 'profileId') -ne $profileId -or
            (Get-ComConProperty $bound 'sessionId') -cne $sessionId) { throw 'PROFILE_SESSION_MISMATCH: control.bound identity differs.' }
        Invoke-ComConStage $OnStage 'CAT_READY'
        try {
            Write-ComConEnvelope $catPipe ([ordered]@{
                messageType = 'cat.ready'; contractVersion = $script:ContractVersion
                profileId = $profileId; sessionId = $sessionId
            }) $SetupMs $TransactionDeadlineTick64 $heartbeatState $setupDeadline
        } catch {
            if ($_.Exception.Message -like 'TRANSACTION_DEADLINE:*') { throw }
            throw "CAT_READY_TIMEOUT: cat.ready could not be written: $($_.Exception.Message)"
        }

        $pendingControl = Start-ComConControlRead $controlPipe
        $session = [ordered]@{
            CatPipe = $catPipe; ControlPipe = $controlPipe; PendingControl = $pendingControl
            ProfileId = $profileId; SessionId = $sessionId; RecordSequence = 0
            ReplyMs = $ReplyMs; AckMs = $AckMs; Result = $result; Closed = $false
        }
        try {
            if ($null -ne $OnPersistentSession) {
                $null = & $OnPersistentSession $session ${function:Invoke-ComConSessionReadOnlyBatch} `
                    ${function:Invoke-ComConSessionSafetyPttOff} $PersistentContext
            } elseif ($SafetyPttOff) {
                $safety = Invoke-ComConSessionSafetyPttOff $session $Radio $Controller $OnStage `
                    $TransactionDeadlineTick64 $OnHeartbeat
                $result.safety = [ordered]@{
                    pttOffAckHex = $safety.pttOffAckHex
                    pttReadbackHex = $safety.pttReadbackHex
                    pttReadbackReceivedUnixMs = $safety.pttReadbackReceivedUnixMs
                }
            } else {
                $batch = Invoke-ComConSessionReadOnlyBatch $session $Commands $OnStage `
                    $TransactionDeadlineTick64 $OnHeartbeat
                foreach ($commandName in $Commands) {
                    $result.commands[$commandName] = $batch.commands[$commandName]
                }
            }
        } finally {
            $pendingControl = $session.PendingControl
        }
        $session.Closed = $true
        $catPipe.Dispose(); $catPipe = $null; $catClosed = $true
        $closeDeadline = if ($TransactionDeadlineTick64 -gt 0) {
            Get-ComConPhaseDeadline $AckMs $TransactionDeadlineTick64
        } else { 0 }
        Invoke-ComConStage $OnStage 'SESSION_END_WAIT'
        do {
            $notification = Read-ComConPendingNotification $controlPipe $profileId $sessionId $AckMs `
                $result $pendingControl $TransactionDeadlineTick64 $heartbeatState $closeDeadline
            $pendingControl = $notification.PendingControl
        } until ($notification.Ended)
        $result.status = 'OK'
        $result | Add-Member -NotePropertyName errorCode -NotePropertyValue '' -Force
        $result | Add-Member -NotePropertyName error -NotePropertyValue '' -Force
    } catch {
        $message = ($_.Exception.Message -replace '[\r\n]+', ' ').Trim()
        if ($message -match '^([A-Z_]+):') { $result.errorCode = $Matches[1] } else { $result.errorCode = 'IPC_ERROR' }
        $result.error = $message
        if ($null -ne $pendingControl -and -not $catClosed) {
            try {
                $catPipe.Dispose(); $catPipe = $null; $catClosed = $true
                if ($pendingControl.Consumed -and -not $pendingControl.FrameComplete) {
                    throw 'CONTROL_FRAMING_LOST: Cannot drain an incomplete control envelope.'
                }
                if (-not (@($result.notifications) | Where-Object {
                    $_.messageType -ceq 'session.notification' -and $_.event -ceq 'session.ended'
                })) {
                    $closeDeadline = Get-ComConPhaseDeadline $AckMs $TransactionDeadlineTick64
                    if ($pendingControl.Consumed) {
                        $pendingControl = Start-ComConControlRead $controlPipe $pendingControl
                    }
                    do {
                        $notification = Read-ComConPendingNotification $controlPipe $profileId $sessionId $AckMs `
                            $result $pendingControl $TransactionDeadlineTick64 $heartbeatState $closeDeadline
                        $pendingControl = $notification.PendingControl
                    } until ($notification.Ended)
                }
            } catch {
                $cleanupErrors.Add('Orderly error closure failed: ' + $_.Exception.Message)
            }
        }
    } finally {
        if ($null -ne $catPipe) {
            try { $catPipe.Dispose() }
            catch { $cleanupErrors.Add('CAT pipe disposal failed: ' + $_.Exception.Message) }
        }
        if ($null -ne $controlPipe) {
            try { $controlPipe.Dispose() }
            catch { $cleanupErrors.Add('control pipe disposal failed: ' + $_.Exception.Message) }
        }
        foreach ($task in $connectTasks) {
            if (-not $task.IsCompleted) {
                try {
                    if (-not $task.Wait(100)) {
                        $cleanupErrors.Add('A pipe-connect operation did not stop within 100 ms after disposal.')
                    }
                } catch { }
            }
            if ($task.IsCompleted) {
                try { $task.GetAwaiter().GetResult() } catch { }
            }
        }
        if ($cleanupErrors.Count -gt 0) {
            $cleanupMessage = $cleanupErrors -join '; '
            if ($result.errorCode) {
                $result.error = "$($result.error) CLEANUP_FAILED: $cleanupMessage"
            } else {
                $result.errorCode = 'CLEANUP_FAILED'
                $result.error = $cleanupMessage
            }
            $result.status = 'ERROR'
        }
    }
    return [pscustomobject]$result
}

function Start-ComConControlRead([System.IO.Stream]$ControlPipe, $Pending = $null) {
    $buffer = New-Object byte[] 1
    if ($null -eq $Pending) { $Pending = @{} }
    $Pending.Buffer = $buffer
    $Pending.Async = $ControlPipe.BeginRead($buffer, 0, 1, $null, $null)
    $Pending.Consumed = $false
    $Pending.FrameComplete = $false
    return $Pending
}

function Get-ComConNotification([System.IO.Stream]$ControlPipe, $Pending, [int]$AckMs,
    [long]$TransactionDeadlineTick64 = 0, $HeartbeatState = $null,
    [long]$PhaseDeadlineTick64 = 0) {
    if ($PhaseDeadlineTick64 -le 0) {
        $PhaseDeadlineTick64 = Get-ComConPhaseDeadline $AckMs $TransactionDeadlineTick64
    } else {
        Assert-ComConTransactionActive $TransactionDeadlineTick64
    }
    Wait-ComConAsyncOperation $Pending.Async $PhaseDeadlineTick64 $TransactionDeadlineTick64 `
        $HeartbeatState 'NOTIFICATION_TIMEOUT: Notification read/ACK deadline expired.'
    $count = $ControlPipe.EndRead($Pending.Async)
    $Pending.Consumed = $true
    if ($count -ne 1) { throw 'CONTROL_DISCONNECTED: control pipe closed.' }
    $notification = Read-ComConEnvelope $ControlPipe $PhaseDeadlineTick64 $Pending.Buffer[0] `
        $TransactionDeadlineTick64 $HeartbeatState `
        'NOTIFICATION_TIMEOUT: Notification read/ACK deadline expired.'
    $Pending.FrameComplete = $true
    return $notification
}

function Send-ComConNotificationAck([System.IO.Stream]$ControlPipe, $Notification, [int]$ProfileId,
    [string]$SessionId, [int]$AckMs, $Result,
    [long]$TransactionDeadlineTick64 = 0, $HeartbeatState = $null,
    [long]$PhaseDeadlineTick64 = 0) {
    $type = Get-ComConProperty $Notification 'messageType'
    $sequence = Get-ComConProperty $Notification 'notificationSequence'
    $fields = @('profileId', 'sessionId', 'notificationSequence')
    $optional = @()
    switch -CaseSensitive ($type) {
        'record.rejected' { $fields += @('recordSequence', 'deliveryStatus', 'reason') }
        'record.outcome' { $fields += @('recordSequence', 'deliveryStatus'); $optional = @('reason') }
        'framing.notification' { $fields += @('errorCode', 'reason') }
        'session.notification' { $fields += @('event', 'reason') }
        default { throw 'INVALID_MESSAGE_TYPE: Unknown notification type.' }
    }
    Assert-ComConFields $Notification $fields $optional
    Assert-ComConBase $Notification $type
    if ((Get-ComConProperty $Notification 'profileId') -ne $ProfileId -or
        (Get-ComConProperty $Notification 'sessionId') -cne $SessionId) { throw 'PROFILE_SESSION_MISMATCH: Notification identity differs.' }
    if ($sequence -isnot [string] -or $sequence -cnotmatch '^[1-9][0-9]*$') { throw 'INVALID_FIELD: Invalid notificationSequence.' }
    if ($type -ceq 'record.outcome' -and (Get-ComConProperty $Notification 'deliveryStatus') -cne 'WRITE_SUCCEEDED' -and
        $null -eq (Get-ComConProperty $Notification 'reason')) { throw 'MISSING_FIELD: Non-success record.outcome requires reason.' }
    if ($type -like 'record.*') {
        $recordSequence = Get-ComConProperty $Notification 'recordSequence'
        $delivery = Get-ComConProperty $Notification 'deliveryStatus'
        if ($recordSequence -isnot [string] -or $recordSequence -cnotmatch '^[1-9][0-9]*$' -or
            $delivery -cnotin @('WRITE_SUCCEEDED', 'NOT_SENT', 'UNKNOWN_DELIVERY')) { throw 'INVALID_FIELD: Invalid record notification fields.' }
        if ($type -ceq 'record.rejected' -and $delivery -cne 'NOT_SENT') { throw 'INVALID_FIELD: record.rejected must be NOT_SENT.' }
    }
    if ($type -ceq 'framing.notification' -and (Get-ComConProperty $Notification 'errorCode') -cnotin @('FRAMING_ANOMALY', 'FRAMING_OVERFLOW')) {
        throw 'INVALID_FIELD: Invalid framing errorCode.'
    }
    if ($type -ceq 'session.notification' -and (Get-ComConProperty $Notification 'event') -cne 'session.ended') {
        throw 'INVALID_FIELD: Unknown session event.'
    }
    $captured = [ordered]@{ messageType = $type; notificationSequence = $sequence }
    foreach ($field in @('recordSequence', 'deliveryStatus', 'reason', 'errorCode', 'event')) {
        $value = Get-ComConProperty $Notification $field
        if ($null -ne $value) { $captured[$field] = $value }
    }
    $previous = New-Object 'System.Collections.Generic.List[object]'
    foreach ($existing in @($Result.notifications)) {
        if ($existing.notificationSequence -ceq $sequence) { $previous.Add($existing) }
    }
    if ($previous.Count -gt 0) {
        if ($previous.Count -ne 1 -or
            (ConvertTo-Json -InputObject $previous[0] -Compress -Depth 8) -cne
            (ConvertTo-Json -InputObject $captured -Compress -Depth 8)) {
            throw 'NOTIFICATION_CONFLICT: Repeated notification sequence has different content.'
        }
    }
    if ($PhaseDeadlineTick64 -le 0) {
        $ackDeadline = Get-ComConPhaseDeadline $AckMs $TransactionDeadlineTick64
    } else {
        Assert-ComConTransactionActive $TransactionDeadlineTick64
        $ackDeadline = $PhaseDeadlineTick64
    }
    Write-ComConEnvelope $ControlPipe ([ordered]@{
        messageType = 'notification.ack'; contractVersion = $script:ContractVersion
        profileId = $ProfileId; sessionId = $SessionId; notificationSequence = $sequence
    }) $AckMs $TransactionDeadlineTick64 $HeartbeatState $ackDeadline
    if ($previous.Count -eq 0) { $Result.notifications += [pscustomobject]$captured }
    return ($type -ceq 'session.notification')
}

function Test-ComConReplyMatchesCommand(
    [byte[]]$Frame,
    [byte[]]$Command,
    [ValidateSet('Reply', 'Acknowledgement')][string]$ResponseKind = 'Reply') {
    if ($Frame.Length -lt 6 -or $Frame[0] -ne 0xFE -or $Frame[1] -ne 0xFE -or
        $Frame[$Frame.Length - 1] -ne 0xFD -or $Command.Length -lt 6) { return $false }
    if ($Frame[2] -ne $Command[3] -or $Frame[3] -ne $Command[2]) { return $false }

    $bodyLength = $Frame.Length - 5
    if ($ResponseKind -ceq 'Acknowledgement') {
        if ($bodyLength -ne 1) { return $false }
        if ($Frame[4] -eq 0xFA) { throw 'CAT_COMMAND_REJECTED: Radio answered NG (FA) to PTT_OFF.' }
        return $Frame[4] -eq 0xFB
    }

    if ($bodyLength -eq 1 -and $Frame[4] -eq 0xFA) {
        $commandHex = (($Command[4..($Command.Length - 2)] | ForEach-Object { $_.ToString('X2') }) -join ' ')
        throw "CAT_COMMAND_REJECTED: Radio answered NG (FA) to $commandHex."
    }

    $commandLength = $Command.Length - 5
    if ($bodyLength -lt $commandLength) { return $false }
    for ($index = 0; $index -lt $commandLength; $index++) {
        if ($Frame[4 + $index] -ne $Command[4 + $index]) { return $false }
    }
    return $true
}

function Read-ComConCatFrameWithNotifications([System.IO.Stream]$CatPipe, [System.IO.Stream]$ControlPipe,
    [int]$ProfileId, [string]$SessionId, [byte[]]$Command, [int]$ReplyMs, [int]$AckMs, $Result,
    $PendingControl, [string]$ResponseKind = 'Reply', [int]$ExpectedRecordSequence = 0,
    [long]$TransactionDeadlineTick64 = 0, $HeartbeatState = $null) {
    $deadline = Get-ComConPhaseDeadline $ReplyMs $TransactionDeadlineTick64
    if ($null -eq $PendingControl) { $PendingControl = Start-ComConControlRead $ControlPipe }
    $frame = New-Object System.Collections.Generic.List[byte]
    $catBuffer = New-Object byte[] 1
    $catRead = @{ Buffer = $catBuffer; Async = $CatPipe.BeginRead($catBuffer, 0, 1, $null, $null) }
    while ($true) {
        $index = Wait-ComConAnyOperation @($catRead.Async.AsyncWaitHandle, $PendingControl.Async.AsyncWaitHandle) `
            $deadline $TransactionDeadlineTick64 $HeartbeatState `
            'CAT_REPLY_TIMEOUT: CAT reply deadline expired.'
        if ($index -eq 1) {
            $notification = Get-ComConNotification $ControlPipe $PendingControl $AckMs `
                $TransactionDeadlineTick64 $HeartbeatState
            if (Send-ComConNotificationAck $ControlPipe $notification $ProfileId $SessionId $AckMs `
                $Result $TransactionDeadlineTick64 $HeartbeatState) {
                throw 'SESSION_ENDED: ComCon ended the session before command reply.'
            }
            if ((Get-ComConProperty $notification 'messageType') -like 'record.*') {
                Assert-ComConRecordOutcome $notification $ExpectedRecordSequence
            }
            $PendingControl = Start-ComConControlRead $ControlPipe $PendingControl
            continue
        }
        if ($index -eq 0) {
            $count = $CatPipe.EndRead($catRead.Async)
            if ($count -ne 1) { throw 'CAT_DISCONNECTED: CAT pipe closed before reply.' }
            $frame.Add($catRead.Buffer[0])
            if ($catRead.Buffer[0] -eq 0xFD) {
                if ($frame.Count -gt 512) { throw 'CAT_FRAME_TOO_LARGE: Reply frame exceeded 512 bytes.' }
                $completeFrame = $frame.ToArray()
                if (Test-ComConReplyMatchesCommand $completeFrame $Command $ResponseKind) {
                    return @{ Bytes = $completeFrame; PendingControl = $PendingControl }
                }
                $frame.Clear()
            }
            if ($frame.Count -gt 512) { throw 'CAT_FRAME_TOO_LARGE: Reply frame exceeded 512 bytes.' }
            $catBuffer = New-Object byte[] 1
            $catRead = @{ Buffer = $catBuffer; Async = $CatPipe.BeginRead($catBuffer, 0, 1, $null, $null) }
        }
    }
}

function Read-ComConPendingNotification([System.IO.Stream]$ControlPipe, [int]$ProfileId,
    [string]$SessionId, [int]$AckMs, $Result, $PendingControl,
    [long]$TransactionDeadlineTick64 = 0, $HeartbeatState = $null,
    [long]$PhaseDeadlineTick64 = 0) {
    if ($null -eq $PendingControl) { $PendingControl = Start-ComConControlRead $ControlPipe }
    $notification = Get-ComConNotification $ControlPipe $PendingControl $AckMs `
        $TransactionDeadlineTick64 $HeartbeatState $PhaseDeadlineTick64
    $ended = Send-ComConNotificationAck $ControlPipe $notification $ProfileId $SessionId $AckMs `
        $Result $TransactionDeadlineTick64 $HeartbeatState $PhaseDeadlineTick64
    return @{ Ended = $ended; PendingControl = $(if ($ended) { $null } else { Start-ComConControlRead $ControlPipe $PendingControl }) }
}

function Invoke-ComConPersistentSession {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$CatPipeName,
        [Parameter(Mandatory = $true)][string]$ControlPipeName,
        [Parameter(Mandatory = $true)][string]$RigProfileUuid,
        [Parameter(Mandatory = $true)][scriptblock]$OnSession,
        [ValidateRange(50, 30000)][int]$ConnectTimeoutMs = (Get-ComConClientTimeouts).ConnectTimeoutMs,
        [ValidateRange(50, 30000)][int]$SetupTimeoutMs = (Get-ComConClientTimeouts).SetupTimeoutMs,
        [ValidateRange(50, 30000)][int]$CatReplyTimeoutMs = (Get-ComConClientTimeouts).CatReplyTimeoutMs,
        [ValidateRange(50, 30000)][int]$NotificationAckTimeoutMs = (Get-ComConClientTimeouts).NotificationAckTimeoutMs,
        [scriptblock]$OnStage,
        $Context = $null
    )
    return Read-ComConSession $ConnectTimeoutMs $SetupTimeoutMs $CatReplyTimeoutMs `
        $NotificationAckTimeoutMs $RigProfileUuid @() $OnStage -OnPersistentSession $OnSession `
        -PersistentContext $Context
}

function Invoke-ComConReadOnlyCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$CatPipeName,
        [Parameter(Mandatory = $true)][string]$ControlPipeName,
        [Parameter(Mandatory = $true)][string]$RigProfileUuid,
        [string]$CommandList = 'Frequency,Mode,Ptt,RfPower',
        [ValidateRange(50, 30000)][int]$ConnectTimeoutMs = (Get-ComConClientTimeouts).ConnectTimeoutMs,
        [ValidateRange(50, 30000)][int]$SetupTimeoutMs = (Get-ComConClientTimeouts).SetupTimeoutMs,
        [ValidateRange(50, 30000)][int]$CatReplyTimeoutMs = (Get-ComConClientTimeouts).CatReplyTimeoutMs,
        [ValidateRange(50, 30000)][int]$NotificationAckTimeoutMs = (Get-ComConClientTimeouts).NotificationAckTimeoutMs,
        [scriptblock]$OnStage
    )
    try {
        $requested = @($CommandList -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
        if ($requested.Count -lt 1) { throw 'INVALID_COMMAND: At least one read command is required.' }
        foreach ($command in $requested) {
            if ($command -cnotin $script:CatCommands.Keys) { throw "COMMAND_REFUSED: '$command' is not in the read-only command allow-list." }
        }
        if (@($requested | Select-Object -Unique).Count -ne $requested.Count) { throw 'INVALID_COMMAND: Duplicate command names are not allowed.' }
        return Read-ComConSession $ConnectTimeoutMs $SetupTimeoutMs $CatReplyTimeoutMs $NotificationAckTimeoutMs $RigProfileUuid $requested $OnStage
    } catch {
        $message = ($_.Exception.Message -replace '[\r\n]+', ' ').Trim()
        $code = if ($message -match '^([A-Z_]+):') { $Matches[1] } else { 'COMMAND_REFUSED' }
        return [pscustomobject]@{ status = 'ERROR'; profileId = $null; sessionId = ''; commands = @{}; notifications = @(); errorCode = $code; error = $message }
    }
}
if ($MyInvocation.InvocationName -ne '.') {
    try {
        $answer = Invoke-ComConReadOnlyCommand -CatPipeName $CatPipeName -ControlPipeName $ControlPipeName `
            -RigProfileUuid $RigProfileUuid -CommandList $CommandList -ConnectTimeoutMs $ConnectTimeoutMs `
            -SetupTimeoutMs $SetupTimeoutMs -CatReplyTimeoutMs $CatReplyTimeoutMs `
            -NotificationAckTimeoutMs $NotificationAckTimeoutMs
    } catch {
        $answer = [pscustomobject]@{ status = 'ERROR'; profileId = $null; sessionId = ''; commands = @{}; notifications = @(); errorCode = 'COMMAND_REFUSED'; error = ($_.Exception.Message -replace '[\r\n]+', ' ').Trim() }
    }
    ConvertTo-Json -InputObject $answer -Compress -Depth 8
}
