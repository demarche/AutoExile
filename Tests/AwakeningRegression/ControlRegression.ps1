# All HTTP is replaced by an in-process fixture. Never contact the running plugin.
$ErrorActionPreference = 'Stop'
$scriptPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../Tools/AwakeningIteration.ps1'))
$checks = 0
function Check([bool]$Condition, [string]$Name) { if (-not $Condition) { throw $Name }; $script:checks++ }
$parseErrors = $null
$null = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$null, [ref]$parseErrors)
Check ($parseErrors.Count -eq 0) 'Control script parses'
$global:AwakeningControlTestHttpCount = 0
$global:AwakeningControlTestPosted = $null
$global:AwakeningControlTestFixture = [pscustomobject]@{
    running = $false
    awakening = [pscustomobject]@{
        generation = 'fixture-generation'; loadedMvid = 'fixture-mvid'; observedUtc = [DateTime]::UtcNow
        phaseStartedUtc = [DateTime]::UtcNow; commandRequestId = ''; commandResult = ''
        run = [pscustomobject]@{ attemptNumber = 1; outcome = 'None'; enteredUtc = $null; recoveryRequired = $false; returnedToHideout = $false }
    }
}
function Invoke-RestMethod {
    param($Uri, $Method, $Body, $ContentType, $TimeoutSec)
    $global:AwakeningControlTestHttpCount++
    if ($Uri -notmatch '^http://offline-fixture.invalid/api/(status|control)$') { throw "Unexpected test URI: $Uri" }
    if ($Method -eq 'Post') {
        $global:AwakeningControlTestPosted = $Body | ConvertFrom-Json
        $global:AwakeningControlTestFixture.awakening.commandRequestId = $global:AwakeningControlTestPosted.requestId
        $global:AwakeningControlTestFixture.awakening.commandResult = $global:AwakeningControlTestPosted.requestId + ':armed'
        return [pscustomobject]@{ ok=$true }
    }
    return $global:AwakeningControlTestFixture
}
foreach ($action in @('Arm','Begin','Inspect','Evidence','Review','Return','Recover','Timeout','Stop','Shutdown','Reload')) {
    $guarded = (& $scriptPath -Action $action -BaseUrl 'http://offline-fixture.invalid') | ConvertFrom-Json
    Check (-not $guarded.executed) "$action defaults to no execution"
}
Check ($global:AwakeningControlTestHttpCount -eq 0) 'Guarded operations do not even send HTTP'
$build = (& $scriptPath -Action VerifyBuild) | ConvertFrom-Json
Check ([Guid]::Parse($build.mvid) -ne [Guid]::Empty -and $build.sha256.Length -eq 64) 'Read-only build identity inspection'
$arm = (& $scriptPath -Action Arm -Execute -BaseUrl 'http://offline-fixture.invalid') | ConvertFrom-Json
Check ($global:AwakeningControlTestPosted.action -eq 'awakening.arm' -and $global:AwakeningControlTestPosted.generation -eq 'fixture-generation') 'Control includes observed generation'
Check ($global:AwakeningControlTestPosted.expectedMvid -eq $build.mvid) 'Control includes disk build identity'
Check ($arm.awakening.commandRequestId -eq $global:AwakeningControlTestPosted.requestId) 'Queued HTTP response is followed by matching frame acknowledgement'
$ended = (& $scriptPath -Action Wait -BaseUrl 'http://offline-fixture.invalid') | ConvertFrom-Json
Check ($ended.needsAttention -and -not $ended.terminal) 'Stopped unfinished attempt is not success'
$global:AwakeningControlTestFixture.running = $true
$global:AwakeningControlTestFixture.awakening.run.enteredUtc = [DateTime]::UtcNow.AddSeconds(-301)
$expired = (& $scriptPath -Action Wait -BaseUrl 'http://offline-fixture.invalid') | ConvertFrom-Json
Check ($expired.deadlineExpired -and $expired.needsAttention -and -not $expired.terminal) 'Outer 300 second watchdog detects expired observation'
$global:AwakeningControlTestFixture.awakening.run.outcome = 'Death'
$global:AwakeningControlTestFixture.awakening.run.recoveryRequired = $true
$global:AwakeningControlTestFixture.awakening.phaseStartedUtc = [DateTime]::UtcNow.AddSeconds(-66)
$recovery = (& $scriptPath -Action Wait -BaseUrl 'http://offline-fixture.invalid') | ConvertFrom-Json
Check ($recovery.recoveryStalled -and -not $recovery.terminal) 'Recovery stall is distinct from finished return'
$global:AwakeningControlTestFixture.awakening.run.recoveryRequired = $false
$global:AwakeningControlTestFixture.awakening.run.returnedToHideout = $true
$dead = (& $scriptPath -Action Wait -BaseUrl 'http://offline-fixture.invalid') | ConvertFrom-Json
Check ($dead.terminal -and $dead.outcome -eq 'Death' -and $dead.returnedToHideout) 'Returned death closes one attempt'
$global:AwakeningControlTestFixture.awakening.run.outcome = 'ManualIntervention'
$manual = (& $scriptPath -Action Wait -BaseUrl 'http://offline-fixture.invalid') | ConvertFrom-Json
Check ($manual.terminal -and $manual.feedbackRequired) 'Manual stop explicitly requires user feedback'
$changed = (& $scriptPath -Action Wait -ExpectedGeneration 'other-generation' -BaseUrl 'http://offline-fixture.invalid') | ConvertFrom-Json
Check ($changed.needsAttention -and $changed.reason -eq 'observed_identity_changed' -and -not $changed.terminal) 'Reload cannot masquerade as watched attempt completion'
$changedAttempt = (& $scriptPath -Action Wait -ExpectedAttemptId 'another-attempt' -BaseUrl 'http://offline-fixture.invalid') | ConvertFrom-Json
Check ($changedAttempt.needsAttention -and $changedAttempt.reason -eq 'observed_identity_changed') 'Wait binds to the requested attempt'
$global:AwakeningControlTestFixture.awakening | Add-Member -NotePropertyName telemetryError -NotePropertyValue 'fixture write failure'
$storage = (& $scriptPath -Action Wait -BaseUrl 'http://offline-fixture.invalid') | ConvertFrom-Json
Check ($storage.needsAttention -and $storage.reason -eq 'evidence_storage_error' -and -not $storage.terminal) 'Evidence write failure prevents successful supervisory completion'
Write-Output "Awakening control regression: $checks checks passed. All HTTP mocked; no game or host actions."
