# Codex's bounded control tool. No scheduled/background agent is created by this script.
[CmdletBinding()]
param(
    [ValidateSet('Status','Arm','Fresh','Continuous','Begin','Wait','Inspect','Evidence','Review','Return','Recover','Rebind','Timeout','Stop','Shutdown','Build','VerifyBuild','Reload')]
    [string]$Action = 'Status',
    [string]$BaseUrl = 'http://127.0.0.1:9876',
    [string]$ReviewFile,
    [string]$BuildDll,
    [string]$ExpectedGeneration,
    [string]$ExpectedAttemptId,
    [int]$HostProcessId = 0,
    [ValidateRange(1,50)][int]$WaitSeconds = 30,
    [switch]$Execute
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exileRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..\..\..'))
if (-not $BuildDll) { $BuildDll = Join-Path $projectRoot 'bin\Debug\net10.0-windows\AutoExile.dll' }
$BuildDll = [IO.Path]::GetFullPath($BuildDll)

function Read-Status {
    Invoke-RestMethod -Uri ($BaseUrl.TrimEnd('/') + '/api/status') -TimeoutSec 5
}
function Read-Mvid([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $module = $metadata.GetModuleDefinition()
            $metadata.GetGuid($module.Mvid).ToString()
        } finally { $pe.Dispose() }
    } finally { $stream.Dispose() }
}
function Send-Control([string]$Command, [string]$Value = '') {
    $snapshot = Read-Status
    if (-not $snapshot.awakening) { throw 'Loaded plugin does not expose Awakening. Deploy and verify the new build first.' }
    $id = [Guid]::NewGuid().ToString('N')
    $body = @{
        action = 'awakening.' + $Command; requestId = $id
        generation = $snapshot.awakening.generation; value = $Value
        expectedMvid = (Read-Mvid $BuildDll)
    } | ConvertTo-Json -Depth 5
    $null = Invoke-RestMethod -Method Post -Uri ($BaseUrl.TrimEnd('/') + '/api/control') -Body $body -ContentType 'application/json' -TimeoutSec 5
    # HTTP 200 means queued, not performed. The frame-thread response must identify this request.
    $limit = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        try { $after = Read-Status }
        catch {
            # Shutdown can close HTTP before the final polling response. Verify the exact durable
            # frame-thread acknowledgement in Verbose; never repeat an uncertain shutdown request.
            $hostLog = Join-Path $exileRoot ('Logs\Verbose' + (Get-Date -Format yyyyMMdd) + '.log')
            if ($Command -eq 'shutdown' -and $BaseUrl -match '^http://(localhost|127\.0\.0\.1):' -and
                (Test-Path -LiteralPath $hostLog) -and
                @(Get-Content -LiteralPath $hostLog -Tail 200 | Where-Object {
                    $_.Contains($snapshot.awakening.generation) -and $_.Contains($id + ':host_shutdown_scheduled')
                }).Count -gt 0) { return $snapshot }
            throw
        }
        if ($after.awakening.commandRequestId -eq $id) {
            if ($after.awakening.commandResult -match '^rejected:') { throw $after.awakening.commandResult }
            return $after
        }
    } while ([DateTime]::UtcNow -lt $limit)
    throw "Command acknowledgement timed out; inspect status before retrying request $id."
}

$mutations = @('Arm','Fresh','Continuous','Begin','Inspect','Evidence','Review','Return','Recover','Rebind','Timeout','Stop','Shutdown','Reload')
if ($Action -in $mutations -and -not $Execute) {
    [pscustomobject]@{ action=$Action; executed=$false; message='Pass -Execute only after the user starts the live phase. No command was sent.' } | ConvertTo-Json
    return
}
switch ($Action) {
    'Status' { Read-Status | ConvertTo-Json -Depth 30 }
    'VerifyBuild' { [pscustomobject]@{ path=$BuildDll; mvid=(Read-Mvid $BuildDll); sha256=(Get-FileHash -LiteralPath $BuildDll -Algorithm SHA256).Hash } | ConvertTo-Json }
    'Build' {
        & dotnet build (Join-Path $projectRoot 'AutoExile.csproj') --no-restore '-p:SkipPluginDeploy=true' "-p:ExapiPackage=$exileRoot" '-clp:ErrorsOnly'
        if ($LASTEXITCODE -ne 0) { throw 'Build failed; nothing was deployed.' }
        [pscustomobject]@{ mvid=(Read-Mvid $BuildDll); deployed=$false } | ConvertTo-Json
    }
    'Wait' {
        $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
        do {
            $s = Read-Status
            $run = $s.awakening.run
            if (-not $run) { throw 'Awakening status unavailable.' }
            if (($ExpectedGeneration -and $s.awakening.generation -ne $ExpectedGeneration) -or
                ($ExpectedAttemptId -and $run.attemptId -ne $ExpectedAttemptId)) {
                [pscustomobject]@{ terminal=$false; needsAttention=$true; reason='observed_identity_changed'; snapshot=$s.awakening } | ConvertTo-Json -Depth 30
                return
            }
            if ($s.awakening.checkpointError -or $s.awakening.telemetryError) {
                [pscustomobject]@{ terminal=$false; needsAttention=$true; reason='evidence_storage_error'; snapshot=$s.awakening } | ConvertTo-Json -Depth 30
                return
            }
            if (-not $s.awakening.inspecting -and $s.awakening.inspectionStatus) {
                [pscustomobject]@{ inspectionComplete=($s.awakening.inspectionStatus -eq 'complete'); needsAttention=($s.awakening.inspectionStatus -ne 'complete'); snapshot=$s.awakening } | ConvertTo-Json -Depth 30
                return
            }
            # This outer watchdog is read-only. Codex decides whether to send Timeout/Return;
            # a nonresponsive frame thread or loading screen never counts as success.
            $expired = $run.outcome -eq 'None' -and $run.enteredUtc -and ([DateTime]::UtcNow - [DateTime]$run.enteredUtc).TotalSeconds -ge 300
            $stale = $s.awakening.observedUtc -and ([DateTime]::UtcNow - [DateTime]$s.awakening.observedUtc).TotalSeconds -gt 15
            $recoveryStalled = $run.recoveryRequired -and $s.awakening.phaseStartedUtc -and ([DateTime]::UtcNow - [DateTime]$s.awakening.phaseStartedUtc).TotalSeconds -gt 65
            if ($expired -or $stale -or $recoveryStalled) {
                [pscustomobject]@{ terminal=$false; needsAttention=$true; deadlineExpired=[bool]$expired; observationStale=[bool]$stale; recoveryStalled=[bool]$recoveryStalled; snapshot=$s.awakening } | ConvertTo-Json -Depth 30
                return
            }
            if ($run.outcome -ne 'None' -and -not $run.recoveryRequired) {
                [pscustomobject]@{ terminal=$true; outcome=$run.outcome; feedbackRequired=($run.outcome -eq 'ManualIntervention'); returnedToHideout=$run.returnedToHideout; snapshot=$s.awakening } | ConvertTo-Json -Depth 30
                return
            }
            if (-not $s.running -and $run.attemptNumber -gt 0) {
                [pscustomobject]@{ terminal=$false; needsAttention=$true; reason='stopped_or_focus_recovery'; snapshot=$s.awakening } | ConvertTo-Json -Depth 30
                return
            }
            Start-Sleep -Milliseconds 1000
        } while ([DateTime]::UtcNow -lt $deadline)
        [pscustomobject]@{ terminal=$false; snapshot=$s.awakening } | ConvertTo-Json -Depth 30
    }
    'Review' {
        if (-not $ReviewFile -or -not (Test-Path -LiteralPath $ReviewFile)) { throw 'Provide a review JSON file with observations, diagnosis, changes, validation, and nextAction.' }
        $review = Get-Content -LiteralPath $ReviewFile -Raw
        $record = $review | ConvertFrom-Json
        foreach ($field in @('observations','diagnosis','changes','validation','nextAction')) {
            if ([string]::IsNullOrWhiteSpace([string]$record.$field)) { throw "Review missing $field" }
        }
        Send-Control 'review' $review | ConvertTo-Json -Depth 30
    }
    'Reload' {
        # Ask the verified host to kill itself: external Stop-Process may lack its integrity level.
        # Then build while the DLL is unlocked, restart, and verify the host's source compiler output.
        $before = Read-Status
        if ($before.running) { throw 'Stop the bot and finish any recovery before reloading.' }
        if ($before.awakening.run.recoveryRequired) { throw 'Recovery still pending.' }
        if ($before.awakening.run.attemptNumber -gt 0 -and -not $before.awakening.run.reviewed) { throw 'Codex evidence review is required before Reload.' }
        if ($HostProcessId -le 0) { throw 'Specify the verified Loader.exe PID with -HostProcessId.' }
        $hostInfo = Get-CimInstance Win32_Process -Filter "ProcessId=$HostProcessId"
        $loaderPath = Join-Path $exileRoot 'Loader.exe'
        if (-not $hostInfo -or $hostInfo.Name -ne 'Loader.exe' -or $before.awakening.hostProcessId -ne $HostProcessId -or
            -not [string]::Equals($before.awakening.hostExecutable, $loaderPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'PID is not the verified workspace ExileAPI Loader.exe.' }
        if (-not $BuildDll.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build artifact must be inside this project.' }
        # The host recompiles source at startup. A DLL rollback cannot recover invalid source;
        # catch compilation failures while the current host is still available.
        & dotnet build (Join-Path $projectRoot 'AutoExile.csproj') --no-restore '-p:SkipPluginDeploy=true' "-p:ExapiPackage=$exileRoot" '-clp:ErrorsOnly' | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Preflight build failed; current host was not stopped.' }
        $expected = Read-Mvid $BuildDll
        $oldGeneration = $before.awakening.generation
        $commandLine = $hostInfo.CommandLine
        $argsText = if (-not $commandLine) { '' } elseif ($commandLine.StartsWith('"')) { $commandLine.Substring($commandLine.IndexOf('"',1)+1).Trim() } else { $commandLine.Substring($loaderPath.Length).Trim() }
        $destination = [IO.Path]::GetFullPath((Join-Path $exileRoot 'Plugins\Temp\AutoExile'))
        if (-not $destination.StartsWith($exileRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid deployment target.' }
        # Validate every artifact before stopping the host.
        $artifacts = @($BuildDll, [IO.Path]::ChangeExtension($BuildDll,'pdb'), [IO.Path]::ChangeExtension($BuildDll,'deps.json'))
        foreach ($artifact in $artifacts) { if (-not (Test-Path -LiteralPath $artifact)) { throw "Missing $artifact" } }
        $null = New-Item -ItemType Directory -Path $destination -Force
        $backup = Join-Path $destination ('reload-backup-' + [Guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $backup
        foreach ($artifact in $artifacts) {
            $oldArtifact = Join-Path $destination ([IO.Path]::GetFileName($artifact))
            if (Test-Path -LiteralPath $oldArtifact) { Copy-Item -LiteralPath $oldArtifact -Destination $backup }
        }
        $launch = @{ FilePath=$loaderPath; WorkingDirectory=$exileRoot; WindowStyle='Hidden'; PassThru=$true }
        if ($argsText) { $launch.ArgumentList = $argsText }
        $requestedBuildDll = $BuildDll
        try {
            $BuildDll = Join-Path $destination 'AutoExile.dll'
            if ((Read-Mvid $BuildDll) -ne $before.awakening.loadedMvid) { throw 'Loaded DLL differs from deployment; do not request shutdown.' }
            $null = Send-Control 'shutdown'
        } finally { $BuildDll = $requestedBuildDll }
        $stopDeadline = [DateTime]::UtcNow.AddSeconds(10)
        while ((Get-Process -Id $HostProcessId -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $stopDeadline) { Start-Sleep -Milliseconds 250 }
        if (Get-Process -Id $HostProcessId -ErrorAction SilentlyContinue) { throw 'Verified host did not stop; deployment cancelled.' }
        $deployError = $null
        try {
            & dotnet build (Join-Path $projectRoot 'AutoExile.csproj') --no-restore '-p:SkipPluginDeploy=true' "-p:ExapiPackage=$exileRoot" '-clp:ErrorsOnly' | Out-Host
            if ($LASTEXITCODE -ne 0) { throw 'Build failed after host shutdown.' }
            $expected = Read-Mvid $BuildDll
            foreach ($artifact in $artifacts) { Copy-Item -LiteralPath $artifact -Destination $destination -Force }
            if ((Read-Mvid (Join-Path $destination 'AutoExile.dll')) -ne $expected) { throw 'Copied artifact MVID mismatch.' }
        } catch {
            $deployError = $_
            foreach ($oldArtifact in Get-ChildItem -LiteralPath $backup -File) { Copy-Item -LiteralPath $oldArtifact.FullName -Destination $destination -Force }
        } finally {
            # Bring back the verified host even if a copy failed. A failed build never starts a map.
            $newHost = Start-Process @launch
        }
        if ($deployError) { throw "Deployment failed; prior artifacts restored and host restarted, but plugin loading is UNVERIFIED because the host recompiles source. $deployError" }
        $limit = [DateTime]::UtcNow.AddSeconds(45)
        do {
            Start-Sleep -Milliseconds 1000
            try {
                $after = Read-Status
                $actualDll = Join-Path $destination 'AutoExile.dll'
                $actualMvid = Read-Mvid $actualDll
                $logPath = Join-Path $exileRoot ('Logs\Verbose' + (Get-Date -Format yyyyMMdd) + '.log')
                $loadLog = @(Get-Content -LiteralPath $logPath -Tail 2500 | Where-Object { $_.Contains('"event":"plugin.loaded"') -and $_.Contains($after.awakening.generation) -and $_.Contains($actualMvid) })
                if ($after.awakening.loadedMvid -eq $actualMvid -and $after.awakening.generation -ne $oldGeneration -and
                    $after.awakening.hostExecutable -eq $loaderPath -and -not $after.running -and -not $after.awakening.armed -and $loadLog.Count -gt 0) {
                    [pscustomobject]@{ reloaded=$true; hostProcessId=$after.awakening.hostProcessId; buildMvid=$expected; loadedMvid=$actualMvid;
                        hostRecompiled=($actualMvid -ne $expected); loadedArtifact=$actualDll; verifiedLoadLog=$loadLog[-1]; snapshot=$after.awakening } | ConvertTo-Json -Depth 30
                    return
                }
            } catch { }
        } while ([DateTime]::UtcNow -lt $limit)
        throw "New host not verified. Expected MVID $expected; inspect Verbose before any Begin. New PID: $($newHost.Id)"
    }
    default { Send-Control $Action.ToLowerInvariant() | ConvertTo-Json -Depth 30 }
}


