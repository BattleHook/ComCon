param(
    [ValidatePattern('^ComConTest_[A-Za-z0-9_]{1,89}$')][string]$TestPipeName,
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
    [ValidateSet('Available', 'Off', 'Faulted')][string]$SelfTestExpectedState = 'Available',
    [ValidateRange(1, 1000000000)][long]$SelfTestExpectedFrequencyHz,
    [switch]$SelfTest,
    [switch]$SelfTestReset
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($SelfTestReset -and (-not $SelfTest -or -not $LifecycleBound -or -not $TestPipeName)) {
    throw 'Reset self-test requires -SelfTest, -LifecycleBound and an isolated test pipe.'
}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
. (Join-Path $PSScriptRoot 'ComCon-CivObservations.ps1')

function Test-PreviewPosition {
    param([int]$X, [int]$Y)
    if ($X -lt -10000 -or $Y -lt -10000) { return $false }
    foreach ($screen in [System.Windows.Forms.Screen]::AllScreens) {
        if ($screen.WorkingArea.Contains($X + 60, $Y + 10)) { return $true }
    }
    return $false
}

function Set-PreviewSnapshot {
    param($View, $Snapshot, [long]$ReceiptTicks, [int]$AgeLimitMs)
    $elapsed = [Math]::Ceiling(
        ([System.Diagnostics.Stopwatch]::GetTimestamp() - $ReceiptTicks) * 1000.0 /
        [System.Diagnostics.Stopwatch]::Frequency)
    $lines = @()
    foreach ($name in @($Snapshot.readings.Keys | Sort-Object)) {
        $reading = $Snapshot.readings[$name]
        $age = [decimal]$reading.ageMs + $elapsed
        if ($age -lt $AgeLimitMs) {
            $lines += "$name = $($reading.value)  (age $([long]$age) ms)"
        }
    }
    $status = $Snapshot.status
    $reason = $Snapshot.reason
    if ($status -ceq 'Available' -and $lines.Count -eq 0) {
        $status = 'Unavailable'
        $reason = 'STALE'
    }
    $View.Status.Text = "$status - $reason"
    $View.Values.Text = $(if ($lines.Count) { $lines -join [Environment]::NewLine } else { 'No fresh readings' })
    $View.Identity.Text = "Server: $($Snapshot.serverInstanceId) | Cursor: $($Snapshot.nextObservationId)"
    if ($Snapshot.PSObject.Properties['lifecycleBound'] -and $Snapshot.lifecycleBound) {
        $View.Identity.Text = "Pipe: $($Snapshot.serverInstanceId) | Cursor: $($Snapshot.nextObservationId)" +
            "`r`nCapture: $($Snapshot.captureId)" +
            "`r`nGeneration: $($Snapshot.profileGeneration) | Epoch: $($Snapshot.ownershipEpoch)"
    }
    if ($Snapshot.requiresReset) {
        $View.Identity.Text += " | Reset required; suggested cursor: $($Snapshot.resynchronizeAfterObservationId)"
    }
}

function New-PreviewView {
    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'HSC - OFFLINE ComCon Preview'
    $form.ClientSize = New-Object System.Drawing.Size(700, 410)
    $form.MinimumSize = New-Object System.Drawing.Size(500, 380)
    $form.StartPosition = 'CenterScreen'
    $banner = New-Object System.Windows.Forms.Label
    $banner.Text = 'SIMULATION ONLY - passive display; NOT safety confirmation'
    $banner.SetBounds(12, 12, 660, 30)
    $banner.ForeColor = [System.Drawing.Color]::DarkRed
    $status = New-Object System.Windows.Forms.Label
    $status.SetBounds(12, 48, 660, 70)
    $values = New-Object System.Windows.Forms.TextBox
    $values.Multiline = $true
    $values.ReadOnly = $true
    $values.ScrollBars = 'Vertical'
    $values.SetBounds(12, 125, 660, 140)
    $values.Anchor = 'Top, Left, Right, Bottom'
    $identity = New-Object System.Windows.Forms.Label
    $identity.SetBounds(12, 275, 660, 85)
    $identity.Anchor = 'Left, Right, Bottom'
    $reset = New-Object System.Windows.Forms.Button
    $reset.Text = 'Reset display...'
    $reset.SetBounds(12, 370, 150, 28)
    $reset.Anchor = 'Left, Bottom'
    $reset.Visible = [bool]$LifecycleBound
    $form.Controls.AddRange(@($banner, $status, $values, $identity, $reset))
    return [pscustomobject]@{ Form = $form; Status = $status; Values = $values; Identity = $identity; Reset = $reset }
}

function Request-PreviewDisplayReset {
    param($Context, $View, [int]$AgeLimitMs)
    $Context.ResetRequested = $true
    $Context.Failed = $false
    $Context.Snapshot = [pscustomobject]@{
        status = 'Unavailable'; reason = 'DISPLAY_RESET_PENDING'; readings = @{}
        serverInstanceId = $null; captureId = $null; profileGeneration = $null; ownershipEpoch = $null
        lifecycleBound = $true; telemetryEnabled = $false; nextObservationId = '0'; hasMore = $false
        requiresReset = $false; resynchronizeAfterObservationId = $null
    }
    $Context.ReceiptTicks = [System.Diagnostics.Stopwatch]::GetTimestamp()
    $View.Reset.Enabled = $false
    Set-PreviewSnapshot $View $Context.Snapshot $Context.ReceiptTicks $AgeLimitMs
}

if ($SelfTest) {
    $view = New-PreviewView
    try {
        $snapshot = [pscustomobject]@{
            status = 'Available'; reason = 'PASSIVE_DISPLAY_ONLY'
            serverInstanceId = 'offline-test'; nextObservationId = '1'
            requiresReset = $false; resynchronizeAfterObservationId = $null
            readings = @{ pttState = [pscustomobject]@{ value = 'RX'; ageMs = 0 } }
        }
        Set-PreviewSnapshot $view $snapshot ([System.Diagnostics.Stopwatch]::GetTimestamp()) 1000
        if ($view.Values.Text -notmatch 'pttState = RX') { throw 'Fresh preview rendering failed.' }
        $snapshot.readings.pttState.ageMs = 1000
        Set-PreviewSnapshot $view $snapshot ([System.Diagnostics.Stopwatch]::GetTimestamp()) 1000
        if ($view.Values.Text -cne 'No fresh readings' -or $view.Status.Text -notmatch 'STALE') {
            throw 'Exact stale boundary failed to clear preview.'
        }
        $snapshot.readings.pttState.ageMs = 0
        $past = [long]([System.Diagnostics.Stopwatch]::GetTimestamp() - [System.Diagnostics.Stopwatch]::Frequency)
        Set-PreviewSnapshot $view $snapshot $past 1000
        if ($view.Values.Text -cne 'No fresh readings') { throw 'UI-local aging failed.' }
        $snapshot.readings.Clear()
        $snapshot.status = 'Unavailable'
        $snapshot.reason = 'RECEIVE_FAULT'
        $snapshot.requiresReset = $true
        Set-PreviewSnapshot $view $snapshot ([System.Diagnostics.Stopwatch]::GetTimestamp()) 1000
        if ($view.Status.Text -notmatch 'RECEIVE_FAULT' -or $view.Identity.Text -notmatch 'Reset required') {
            throw 'Fault/reset rendering failed.'
        }
        if (Test-PreviewPosition -10001 0) { throw 'Invalid coordinates accepted.' }
        if (Test-PreviewPosition 100000 100000) { throw 'Off-screen coordinates accepted.' }
        $area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
        if (-not (Test-PreviewPosition $area.Left $area.Top)) { throw 'Valid position rejected.' }
        'offline-preview-ok'
    }
    finally { $view.Form.Dispose() }
    if (-not $TestPipeName) { return }
}

if (-not $TestPipeName) { throw 'Specify an isolated ComConTest_* pipe. Station pipes are not permitted.' }
if ([System.Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run the offline preview using powershell.exe -STA.'
}
$faultLogPath = $(if ($SelfTest) { $null } else { Join-Path $PSScriptRoot '..\Logging\ComCon-development-faults.jsonl' })
$state = New-ComConCivDisplayState -ProfileId $ProfileId -MaximumAgeMs $MaximumAgeMs `
    -RadioAddress $RadioAddress -ControllerAddress $ControllerAddress `
    -AfterObservationId $AfterObservationId -ServerInstanceId $ServerInstanceId -FaultLogPath $faultLogPath `
    -LifecycleBound:$LifecycleBound -CaptureId $CaptureId -ProfileGeneration $ProfileGeneration -OwnershipEpoch $OwnershipEpoch
$view = New-PreviewView
$positionPath = Join-Path $PSScriptRoot '..\Logging\ComCon-OfflinePreview-position.json'
$runspace = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace()
$pipeline = [powershell]::Create()
$timer = New-Object System.Windows.Forms.Timer
$context = @{ Pending = $null; Snapshot = Get-ComConCivDisplaySnapshot $state
    ReceiptTicks = [System.Diagnostics.Stopwatch]::GetTimestamp(); NextPollTicks = 0L; Failed = $false
    ResetRequested = $false; ResetInProgress = $false; ResetCompleted = $false; ConfirmingReset = $false }
try {
    if (-not $SelfTest -and (Test-Path -LiteralPath $positionPath)) {
        $position = Get-Content -LiteralPath $positionPath -Raw | ConvertFrom-Json
        if ($position.x -isnot [int] -or $position.y -isnot [int]) { throw 'Preview position file has invalid coordinate types.' }
        if (Test-PreviewPosition $position.x $position.y) {
            $view.Form.StartPosition = 'Manual'
            $view.Form.Location = New-Object System.Drawing.Point($position.x, $position.y)
        }
    }
    $runspace.Open()
    $runspace.SessionStateProxy.SetVariable('displayState', $state)
    $runspace.SessionStateProxy.SetVariable('helperPath', (Join-Path $PSScriptRoot 'ComCon-CivObservations.ps1'))
    $runspace.SessionStateProxy.SetVariable('testPipe', $TestPipeName)
    $pipeline.Runspace = $runspace
    $resetScript = {
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        . $helperPath
        try { $displayState = Reset-ComConCivDisplayState -State $displayState -TestPipeName $testPipe -TimeoutMs 1000 }
        catch { Clear-ComConCivDisplayState -State $displayState -Reason $_.Exception.Message }
        [pscustomobject]@{
            Snapshot = Get-ComConCivDisplaySnapshot $displayState
            ReceiptTicks = [System.Diagnostics.Stopwatch]::GetTimestamp()
        }
    }.ToString()
    $pollScript = {
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        . $helperPath
        try {
            if ($displayState.lifecycleBound) {
                $null = Receive-ComConCivDisplayLifecycle -State $displayState -TestPipeName $testPipe -TimeoutMs 1000
            }
            if (-not $displayState.lifecycleBound -or $displayState.telemetryEnabled) {
                $null = Receive-ComConCivDisplayState -State $displayState -TestPipeName $testPipe -TimeoutMs 1000
            }
        }
        catch { Clear-ComConCivDisplayState -State $displayState -Reason $_.Exception.Message }
        $ticks = [System.Diagnostics.Stopwatch]::GetTimestamp()
        [pscustomobject]@{
            Snapshot = Get-ComConCivDisplaySnapshot $displayState
            ReceiptTicks = $ticks
        }
    }.ToString()
    $timer.Interval = 100
    $timer.Add_Tick({
        try {
            if ($null -ne $context.Pending -and $context.Pending.IsCompleted) {
                $result = @($pipeline.EndInvoke($context.Pending))
                $context.Pending = $null
                if ($pipeline.Streams.Error.Count -gt 0) { throw $pipeline.Streams.Error[0] }
                if ($result.Count -ne 1) { throw 'Preview worker returned an invalid result count.' }
                if (-not $context.ResetRequested) {
                    $context.Snapshot = $result[0].Snapshot
                    $context.ReceiptTicks = $result[0].ReceiptTicks
                }
                if ($context.ResetInProgress) {
                    $context.ResetInProgress = $false
                    $context.ResetCompleted = $true
                }
                $context.NextPollTicks = [System.Diagnostics.Stopwatch]::GetTimestamp() +
                    $(if ($context.Snapshot.hasMore) { 0L } else { [long]([System.Diagnostics.Stopwatch]::Frequency / 4) })
            }
            Set-PreviewSnapshot $view $context.Snapshot $context.ReceiptTicks $MaximumAgeMs
            $view.Reset.Enabled = -not $context.ResetRequested -and -not $context.ResetInProgress
            if (-not $context.Failed -and -not $context.ConfirmingReset -and
                ($context.ResetRequested -or -not $context.Snapshot.requiresReset) -and
                $null -eq $context.Pending -and
                ($context.ResetRequested -or [System.Diagnostics.Stopwatch]::GetTimestamp() -ge $context.NextPollTicks)) {
                $pipeline.Commands.Clear()
                if ($context.ResetRequested) {
                    $pipeline.Streams.Error.Clear()
                    $context.ResetRequested = $false
                    $context.ResetInProgress = $true
                    $context.ResetCompleted = $false
                    [void]$pipeline.AddScript($resetScript)
                }
                else { [void]$pipeline.AddScript($pollScript) }
                $context.Pending = $pipeline.BeginInvoke()
            }
        }
        catch {
            if ($null -ne $context.Pending -and $context.Pending.IsCompleted) { $context.Pending = $null }
            $context.Failed = $true
            $context.ResetRequested = $false
            $context.ResetInProgress = $false
            $view.Reset.Enabled = $true
            $context.Snapshot = [pscustomobject]@{
                status = 'Unavailable'; reason = "Preview worker failure: $($_.Exception.Message)"
                readings = @{}; serverInstanceId = $null; nextObservationId = '0'
                requiresReset = $true; resynchronizeAfterObservationId = $null
            }
            $context.ReceiptTicks = [System.Diagnostics.Stopwatch]::GetTimestamp()
            Set-PreviewSnapshot $view $context.Snapshot $context.ReceiptTicks $MaximumAgeMs
        }
    })
    $view.Reset.Add_Click({
        $context.ConfirmingReset = $true
        try {
            $choice = [System.Windows.Forms.MessageBox]::Show($view.Form,
                "Discard this display's readings and cursor, and reconnect to the current simulation capture?`r`n`r`n" +
                "Any missing or retained history will be skipped. Readings stay unavailable until new frames arrive.`r`n`r`n" +
                "This does not start a host or bridge, enable capture, or send radio commands.",
                'Reset offline display', [System.Windows.Forms.MessageBoxButtons]::YesNo,
                [System.Windows.Forms.MessageBoxIcon]::Warning, [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
            if ($choice -eq [System.Windows.Forms.DialogResult]::Yes) {
                Request-PreviewDisplayReset $context $view $MaximumAgeMs
            }
        }
        finally { $context.ConfirmingReset = $false }
    })
    $view.Form.Add_FormClosing({
        if ($SelfTest) { return }
        if ($view.Form.WindowState -ne 'Normal') { return }
        $point = $view.Form.Location
        if (-not (Test-PreviewPosition $point.X $point.Y)) { return }
        try {
            $directory = Split-Path -Parent $positionPath
            if (-not (Test-Path -LiteralPath $directory)) { [void](New-Item -ItemType Directory -Path $directory) }
            @{ x = $point.X; y = $point.Y } | ConvertTo-Json |
                Set-Content -LiteralPath $positionPath -Encoding UTF8
        }
        catch {
            [void][System.Windows.Forms.MessageBox]::Show($view.Form,
                "Cannot save preview position: $($_.Exception.Message)", 'Offline preview',
                [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error)
        }
    })
    Set-PreviewSnapshot $view $context.Snapshot $context.ReceiptTicks $MaximumAgeMs
    $timer.Start()
    if ($SelfTest) {
        $testClock = [System.Diagnostics.Stopwatch]::StartNew()
        $matched = $false
        while ($testClock.ElapsedMilliseconds -lt 5000 -and -not $matched) {
            [System.Windows.Forms.Application]::DoEvents()
            Start-Sleep -Milliseconds 10
            $matched = switch ($SelfTestExpectedState) {
                'Available' {
                    $context.Snapshot.status -ceq 'Available' -and $view.Values.Text -match 'pttState' -and
                    (-not $SelfTestExpectedFrequencyHz -or
                        ($context.Snapshot.readings.ContainsKey('frequencyHz') -and
                         $context.Snapshot.readings.frequencyHz.value -eq $SelfTestExpectedFrequencyHz))
                }
                'Off' { $context.Snapshot.reason -ceq 'TELEMETRY_OFF' -and $view.Values.Text -ceq 'No fresh readings' }
                'Faulted' { $context.Snapshot.reason -match '^RECEIVE_FAULT:' -and $view.Values.Text -ceq 'No fresh readings' }
            }
        }
        if (-not $matched) {
            throw "Background IPC/render test failed: $($view.Status.Text)"
        }
        if ($LifecycleBound -and ($view.Identity.Text -notmatch 'Capture:' -or
            $view.Identity.Text -notmatch 'Generation:')) { throw 'Capture identity rendering failed.' }
        'offline-preview-worker-ok'
        if ($SelfTestReset) {
            if ($null -eq $context.Pending) {
                $pipeline.Commands.Clear()
                [void]$pipeline.AddScript($pollScript + "`r`nStart-Sleep -Milliseconds 200")
                $context.Pending = $pipeline.BeginInvoke()
            }
            Request-PreviewDisplayReset $context $view $MaximumAgeMs
            if ($view.Values.Text -cne 'No fresh readings') { throw 'Queued reset retained readings.' }
            $testClock.Restart()
            while ($testClock.ElapsedMilliseconds -lt 5000 -and -not $context.ResetCompleted -and -not $context.Failed) {
                [System.Windows.Forms.Application]::DoEvents()
                Start-Sleep -Milliseconds 10
                if (($context.ResetRequested -or $context.ResetInProgress) -and
                    $view.Values.Text -cne 'No fresh readings') { throw 'Pending old worker restored readings during reset.' }
            }
            if (-not $context.ResetCompleted -or $context.Failed -or $context.Snapshot.readings.Count -ne 0 -or
                $view.Values.Text -cne 'No fresh readings' -or
                ($context.Snapshot.reason -cnotin @('AWAITING_FRESH_OBSERVATIONS', 'TELEMETRY_OFF') -and
                 $context.Snapshot.reason -notmatch '^RECEIVE_FAULT:')) {
                throw "Display reset worker failed: $($view.Status.Text)"
            }
            'offline-preview-reset-ok'
        }
    }
    else { [void]$view.Form.ShowDialog() }
}
finally {
    $timer.Stop()
    $timer.Dispose()
    $pipeline.Stop()
    $pipeline.Dispose()
    $runspace.Dispose()
    $view.Form.Dispose()
}
