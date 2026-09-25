# Restart ExileAPI (Loader.exe) so it recompiles Plugins\Source\AutoExile, then verify the new build is loaded.
# Steps: 1) read the running host state  2) kill Loader.exe  3) start Loader.exe  4) wait for the control API
#        5) confirm the loaded AutoExile.dll (mvid) is the freshly compiled one and that no compile errors were written.
# Usage: double-click RestartExileApi.bat, or POST {"action":"host.restart"} to http://127.0.0.1:9876/api/control
#        (AutoExile then runs this script detached). No administrator rights are requested.
# The final result is also written to RestartExileApi.last.txt next to this script.
# Exit code: 0 = new build verified, 1 = failed (details are printed).
param([int]$TimeoutSec = 300, [switch]$NoPause, [switch]$Force)

$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path -Parent $PSScriptRoot
$root = (Resolve-Path (Join-Path $pluginRoot '..\..\..')).Path
$loader = Join-Path $root 'Loader.exe'
$dll = Join-Path $root 'Plugins\Temp\AutoExile\AutoExile.dll'
$errors = Join-Path $pluginRoot 'Errors.txt'
# 2026-09-25: the Duo Aurabot's ExileAPI (another Windows session) serves its API on its own WebUiPort (9877).
$apiPort = 9876
try { $cfg = Get-Content (Join-Path $root 'config\global\AutoExile_settings.json') -Raw | ConvertFrom-Json; if ($cfg.WebUiPort.Value) { $apiPort = [int]$cfg.WebUiPort.Value } } catch { }
$api = "http://127.0.0.1:$apiPort/api/status"

$resultFile = Join-Path $PSScriptRoot 'RestartExileApi.last.txt'
function Finish([int]$code, [string]$message) {
    try { Set-Content -Path $resultFile -Value ("{0:yyyy-MM-dd HH:mm:ss} exit={1} {2}" -f (Get-Date), $code, $message) -Encoding UTF8 } catch { }
    if ($message) { Write-Host $message -ForegroundColor $(if ($code -eq 0) { 'Green' } else { 'Red' }) }
    if (-not $NoPause) { Read-Host 'Press Enter to close' | Out-Null }
    exit $code
}
function Get-Status {
    try { return Invoke-RestMethod -Uri $api -TimeoutSec 3 } catch { return $null }
}
function Get-DllMvid([string]$path) {
    try {
        $bytes = [IO.File]::ReadAllBytes($path)
        $stream = New-Object IO.MemoryStream(, $bytes)
        $pe = New-Object System.Reflection.PortableExecutable.PEReader($stream)
        $reader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        return $reader.GetGuid($reader.GetModuleDefinition().Mvid).ToString()
    } catch { return $null }
}

function Focus-Game {
    # AutoExile only ticks while the game window is in the foreground; the freshly started Loader may take focus.
    try {
        Add-Type -Namespace AeWin -Name User32 -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.UIntPtr dwExtraInfo);
"@ -ErrorAction SilentlyContinue
        $game = Get-Process | Where-Object { $_.ProcessName -like 'PathOfExile*' -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if (-not $game) { Write-Host '      game window not found'; return }
        [AeWin.User32]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)      # Alt down: lets this process move the foreground
        [AeWin.User32]::ShowWindow($game.MainWindowHandle, 9) | Out-Null  # SW_RESTORE
        $ok = [AeWin.User32]::SetForegroundWindow($game.MainWindowHandle)
        [AeWin.User32]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)      # Alt up
        Write-Host "      game window focused: $ok"
    } catch { Write-Host "      could not focus the game: $($_.Exception.Message)" }
}

if (-not (Test-Path $loader)) { Finish 1 "Loader.exe not found: $loader" }

$before = Get-Status
$oldPid = $null; $oldMvid = $null
if ($before -and $before.awakening) {
    $oldPid = $before.awakening.hostProcessId; $oldMvid = $before.awakening.loadedMvid
    Write-Host "Running host: pid=$oldPid mvid=$oldMvid mode=$($before.mode) phase=$($before.awakening.phase) bot_running=$($before.running)"
    # A frozen host (game lost the foreground, plugin tick stalled > 60 s) cannot process stop commands: restart it anyway
    # so Focus-Game brings the game back to the front.
    $stale = $false
    try { $obs = [DateTime]::Parse($before.awakening.observedUtc).ToUniversalTime(); $stale = ((Get-Date).ToUniversalTime() - $obs).TotalSeconds -gt 60 } catch { }
    if ($stale) { Write-Host "Host tick stalled since $($before.awakening.observedUtc): restarting despite running=true." }
    if ($before.running -and -not $Force -and -not $stale) { Finish 1 'Refused: the bot is running. Send awakening.stop_after_map and wait for the Hideout stop (or pass -Force).' }
} else { Write-Host 'Control API not reachable (host not running or plugin not loaded).' }

$started = Get-Date
Start-Sleep -Milliseconds 800  # let the HTTP response that triggered us reach the caller
Write-Host '[1/4] Killing Loader.exe ...'
# 2026-09-23: a second ExileAPI (Duo follower) runs in another Windows session; only this session's Loader is ours.
$mySession = (Get-Process -Id $PID).SessionId
function MyLoaders { @(Get-Process -Name 'Loader' -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $mySession }) }
$procs = MyLoaders
foreach ($p in $procs) { try { Stop-Process -Id $p.Id -Force } catch { Finish 1 "Could not kill Loader.exe pid $($p.Id): $($_.Exception.Message)" } }
$deadline = (Get-Date).AddSeconds(20)
while ((MyLoaders).Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
if ((MyLoaders).Count -gt 0) { Finish 1 'Loader.exe is still running after 20 seconds.' }
Write-Host "      killed $($procs.Count) process(es)."

Write-Host '[2/4] Starting Loader.exe ...'
Start-Process -FilePath $loader -WorkingDirectory $root | Out-Null

# 2026-09-24 (user): the new Loader asks "Hud process is already running" -> answer はい (Yes); "Failed to create mutex"
# exception boxes -> OK. Answered by posting WM_COMMAND to the dialogs of this session's Loader (works without focus).
try {
    Add-Type -Namespace AeDlg -Name W -MemberDefinition @"
public delegate bool EnumProc(System.IntPtr h, System.IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, System.IntPtr l);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(System.IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);
"@ -ErrorAction SilentlyContinue
} catch { }
function Answer-LoaderDialogs {
    $ids = @(MyLoaders | ForEach-Object { [uint32]$_.Id })
    if ($ids.Count -eq 0) { return }
    $found = New-Object System.Collections.ArrayList
    $cb = [AeDlg.W+EnumProc]{ param($h, $l)
        $procId = [uint32]0; [AeDlg.W]::GetWindowThreadProcessId($h, [ref]$procId) | Out-Null
        if ($ids -contains $procId -and [AeDlg.W]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256; [AeDlg.W]::GetWindowText($h, $sb, 256) | Out-Null
            [void]$found.Add(@($h, $sb.ToString()))
        }
        return $true }
    try { [AeDlg.W]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null } catch { return }
    foreach ($w in $found) {
        if ($w[1] -like 'Hud process is already running*') { [AeDlg.W]::PostMessage($w[0], 0x0111, [IntPtr]6, [IntPtr]::Zero) | Out-Null; Write-Host '      answered Yes: Hud process is already running' }
        elseif ($w[1] -eq 'Exception') { [AeDlg.W]::PostMessage($w[0], 0x0111, [IntPtr]1, [IntPtr]::Zero) | Out-Null; Write-Host '      answered OK: Exception dialog' }
    }
}

Write-Host '[3/4] Waiting for the plugin control API (compile + load) ...'
$status = $null; $deadline = (Get-Date).AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    try { Answer-LoaderDialogs } catch { }
    $status = Get-Status
    if ($status -and $status.awakening -and $status.awakening.loadedMvid -and $status.awakening.hostProcessId -ne $oldPid) { break }
    $status = $null
}
if (-not $status) {
    $tail = if ((Test-Path $errors) -and (Get-Item $errors).LastWriteTime -gt $started) { "`nCompile errors were written:`n" + (Get-Content $errors -Tail 20 -Raw) } else { '' }
    Finish 1 "Timed out after $TimeoutSec s: AutoExile did not load.$tail"
}

Focus-Game
Write-Host '[4/4] Verifying the loaded build ...'
$loaded = $status.awakening.loadedMvid
$dllMvid = Get-DllMvid $dll
$dllTime = (Get-Item $dll).LastWriteTime
Write-Host "      loaded mvid : $loaded"
Write-Host "      dll mvid    : $dllMvid   (AutoExile.dll written $dllTime)"
$problems = @()
$unchanged = $oldMvid -and $oldMvid -eq $loaded
if ($dllMvid -and $dllMvid -ne $loaded) { $problems += 'loaded mvid differs from the compiled AutoExile.dll' }
if (-not $unchanged -and -not $dllMvid -and $dllTime -lt $started) { $problems += 'AutoExile.dll was not rebuilt by this start (older than the restart)' }
if ((Test-Path $errors) -and (Get-Item $errors).LastWriteTime -gt $started) { $problems += "compile errors written to $errors" }
Write-Host "      host pid=$($status.awakening.hostProcessId) area='$($status.area)' mode=$($status.mode) phase=$($status.awakening.phase)"
if ($problems.Count -gt 0) { Finish 1 ('FAILED: ' + ($problems -join '; ')) }
if ($unchanged) { Finish 0 "OK: host restarted; build unchanged (mvid $loaded)." }
Finish 0 "OK: new AutoExile build is loaded (mvid $loaded)."
