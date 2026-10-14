# Ham Shack Console - shared Icom CI-V read-only definitions and decoders.
# Dot-sourced by CIV-Read.ps1 (one command) and CIV-TelemetryReader.ps1 (polling).

# Read-only commands: command/sub-command bytes -> description. No data bytes.
$ReadCommands = [ordered]@{
    '03'    = 'Operating frequency'
    '04'    = 'Operating mode'
    '14 01' = 'AF gain setting'
    '14 02' = 'RF gain setting'
    '14 03' = 'Squelch setting'
    '14 0A' = 'RF power setting'
    '14 0B' = 'Mic gain setting'
    '14 15' = 'Monitor gain setting'
    '15 02' = 'S-meter'
    '15 11' = 'Po (RF output) meter'
    '15 12' = 'SWR meter'
    '15 13' = 'ALC meter'
    '15 14' = 'COMP meter'
    '16 45' = 'Monitor on/off'
    '1C 00' = 'Transmit/receive status'
}

# Set commands the telemetry reader may send (only when enabled per radio in the
# INI <code>SetCommands). Command -> allowed data (BCD level 0..255 or fixed bytes).
$SetWhitelist = [ordered]@{
    '14 01' = 'LEVEL'    # AF gain 0000..0255 (MUTE = 0000)
    '16 45' = '00|01'    # Monitor off/on
}

# Validates a set request '<cmd> <data>' against $SetWhitelist; returns the
# normalized command and data, or throws.
function Get-SetRequest([string]$text) {
    $bytes = ConvertTo-ByteArray $text
    if ($bytes.Length -lt 3) { throw "Set request too short: '$text'." }
    $command = ConvertTo-HexText ([byte[]]$bytes[0..1])
    if (-not $SetWhitelist.Contains($command)) { throw "Command '$command' is not a whitelisted set command." }
    $data = [byte[]]$bytes[2..($bytes.Length - 1)]
    $rule = $SetWhitelist[$command]
    if ($rule -eq 'LEVEL') {
        if ($data.Length -ne 2) { throw 'Level needs 2 data bytes.' }
        if ((Get-Bcd $data) -gt 255) { throw 'Level above 0255.' }
    } elseif ($data.Length -ne 1 -or ($rule -split '\|') -notcontains $data[0].ToString('X2')) {
        throw "Invalid data for ${command}: $(ConvertTo-HexText $data)."
    }
    return @{ Command = $command; Data = $data; Text = "$command $(ConvertTo-HexText $data)" }
}

# Separate from the TCVR whitelist: Main can request RX only, never TX.
function Get-CivRxRequest($definition) {
    if ($null -eq $definition -or $definition.name -cne 'pttOff' -or
        $definition.direction -cne 'write' -or $definition.command -cne '1C 00' -or
        $definition.dataPolicy -cne '00' -or
        $definition.enabled -ne $true -or $definition.gate -cne 'main_safety') {
        throw 'The enabled catalog safety definition must be RX only.'
    }
    return @{ Command = $definition.command; Data = [byte[]]@(0x00); Text = "$($definition.command) 00" }
}

function ConvertTo-ByteArray([string]$hexText) {
    $clean = ($hexText -replace '[^0-9A-Fa-f]', '')
    if ($clean.Length -eq 0 -or ($clean.Length % 2) -ne 0) { throw "Invalid hex: '$hexText'" }
    $bytes = New-Object byte[] ($clean.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) { $bytes[$i] = [Convert]::ToByte($clean.Substring($i * 2, 2), 16) }
    return ,$bytes
}

function ConvertTo-HexText([byte[]]$bytes) {
    return (($bytes | ForEach-Object { $_.ToString('X2') }) -join ' ')
}

function Get-NormalizedCommand([string]$text) {
    return ConvertTo-HexText (ConvertTo-ByteArray $text)
}

function Get-Bcd([byte[]]$bytes) {
    $digits = ''
    foreach ($b in $bytes) {
        if (($b -shr 4) -gt 9 -or ($b -band 0x0F) -gt 9) { throw "Reply data is not BCD: $(ConvertTo-HexText $bytes)" }
        $digits += $b.ToString('X2')
    }
    return [int64]$digits
}

# Splits a byte stream into CI-V frames (FE FE ... FD).
function Get-Frames([byte[]]$stream) {
    $frames = New-Object System.Collections.Generic.List[object]
    $i = 0
    while ($i -lt $stream.Length - 1) {
        if ($stream[$i] -eq 0xFE -and $stream[$i + 1] -eq 0xFE) {
            $start = $i
            while ($start + 2 -lt $stream.Length -and $stream[$start + 2] -eq 0xFE) { $start++ }
            $end = [Array]::IndexOf($stream, [byte]0xFD, $start + 2)
            if ($end -lt 0) { break }
            $frames.Add([byte[]]$stream[$start..$end])
            $i = $end + 1
        } else {
            $i++
        }
    }
    return ,$frames
}

# Finds the radio's reply to our controller address and decodes it.
function Get-Reply([byte[]]$stream, [byte]$radio, [byte]$controller, [byte[]]$commandBytes) {
    foreach ($frame in (Get-Frames $stream)) {
        if ($frame.Length -lt 6 -or $frame[2] -ne $controller -or $frame[3] -ne $radio) { continue }
        $body = [byte[]]$frame[4..($frame.Length - 2)]
        if ($body.Length -eq 1 -and $body[0] -eq 0xFA) { throw "Radio answered NG (FA) to $(ConvertTo-HexText $commandBytes)." }
        if ($body.Length -lt $commandBytes.Length) { continue }
        $match = $true
        for ($k = 0; $k -lt $commandBytes.Length; $k++) { if ($body[$k] -ne $commandBytes[$k]) { $match = $false; break } }
        if (-not $match) { continue }
        $data = if ($body.Length -gt $commandBytes.Length) { [byte[]]$body[$commandBytes.Length..($body.Length - 1)] } else { [byte[]]@() }
        return @{ Frame = $frame; Data = $data }
    }
    return $null
}

function Get-DecodedValue([string]$command, [byte[]]$data) {
    $result = [ordered]@{}
    switch -Regex ($command) {
        '^1C 00$' {
            if ($data.Length -ne 1 -or $data[0] -notin @(0, 1)) {
                throw 'PTT status must be exactly one byte, 00 (RX) or 01 (TX).'
            }
            $result.VALUE = [int]$data[0]
            break
        }
        '^03$' {
            # 5 bytes BCD, least significant byte first, 1 Hz units.
            $reversed = [byte[]]$data.Clone(); [Array]::Reverse($reversed)
            $result.VALUE = Get-Bcd $reversed
        }
        '^04$' {
            $result.VALUE = $data[0].ToString('X2')
            if ($data.Length -gt 1) { $result.FILTER = $data[1].ToString('X2') }
        }
        '^16 ' {
            if ($data.Length -ne 1) { throw "Expected 1 data byte, got $($data.Length)." }
            $result.VALUE = [int]$data[0]
        }
        '^15 12$' {
            if ($data.Length -ne 2) { throw "Expected 2 data bytes, got $($data.Length)." }
            # CI-V SWR is a big-endian raw hexadecimal count, not packed BCD.
            $result.VALUE = ([int]$data[0] * 256) + [int]$data[1]
            break
        }
        '^1[45] ' {
            if ($data.Length -ne 2) { throw "Expected 2 data bytes, got $($data.Length)." }
            $value = Get-Bcd $data
            if ($value -gt 255) { throw "Level value out of range: $value" }
            $result.VALUE = $value
            # The radio shows levels rounded down (76 -> 29 %).
            $result.PERCENT = [int][Math]::Floor($value * 100 / 255)
        }
    }
    return $result
}


# JSON field names for the readings written by CIV-TelemetryReader.ps1.
$ReadingNames = @{
    '03'    = 'frequencyHz'
    '04'    = 'mode'
    '14 01' = 'afGain'
    '14 02' = 'rfGain'
    '14 03' = 'squelch'
    '14 0A' = 'rfPowerSetting'
    '14 0B' = 'micGain'
    '14 15' = 'monitorGain'
    '15 02' = 'sMeter'
    '15 11' = 'RFPOWER_METER'
    '15 12' = 'swrMeter'
    '15 13' = 'alcMeter'
    '15 14' = 'compMeter'
    '16 45' = 'monitorOn'
    '1C 00' = 'pttState'
}
