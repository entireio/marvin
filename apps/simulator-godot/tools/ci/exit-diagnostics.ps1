# How the Windows builds end: exit codes over repeated runs, and the exact faulting access under full page heap.
#
#   tools/ci/exit-diagnostics.ps1 -ReleaseExe build/windows/MarvinSimulator.exe [-DebugExe build/windows-debug/MarvinSimulator.exe]
#        -Out OUT [-Runs 3]
#   tools/ci/exit-diagnostics.ps1 -Godot GODOT_GUI_EXE -Project apps/simulator-godot -Rendered d3d12 -Out OUT [-TownSmoke]
#
# Exported builds (headless, Dummy audio): --town-statistics and the minimal --exit-leak-probe (a MeshInstance3D
# outside the tree, leaked or freed: MARVIN_EXIT_PROBE=free) with the release build, -Runs times each, and once with the
# debug build; then the same under cdb with full page heap enabled for MarvinSimulator.exe (gflags /p /enable /full): a
# freed block is decommitted, so a use after free faults at the access itself. At a second-chance access violation, a
# heap corruption (0xC0000374) or a page heap stop, cdb prints the exception, the faulting stack, the page heap record of
# the block the faulting thread was reading (!heap -p -a: where it was allocated and freed), every thread's native stack,
# and with SOS the managed threads and their stacks; then the modules (lm) for the offsets. The official templates have
# no symbols; frames are module+offset, named in PORTING.md from the template's own strings.
# The editor runtime rendered (-Godot GUI_EXE -Rendered d3d12|vulkan [-TownSmoke [-Debugger] [-SmokeTimeout S]]):
# --town-statistics -Runs times, then --town-smoke-test (under cdb with -Debugger, without page heap). Every run is
# timed, also from the mode's last line to the process's end (the quit).
# Results go to OUT/exit-diagnostics.json (appended to when it exists), the logs and cdb transcripts, and a section of
# the job summary.
param(
    [string]$ReleaseExe, [string]$DebugExe, [string]$Godot, [string]$Project, [string]$Rendered,
    [Parameter(Mandatory = $true)][string]$Out, [int]$Runs = 3, [switch]$TownSmoke, [switch]$Debugger, [int]$SmokeTimeout = 2400
)
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$kits = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64'
$cdb = Join-Path $kits 'cdb.exe'
$gflags = Join-Path $kits 'gflags.exe'
$sos = Join-Path $env:USERPROFILE '.dotnet\sos\sos.dll'
$results = [System.Collections.Generic.List[object]]::new()

function Exit-Text([long]$code) {
    $c = $code -band 4294967295 # (0xFFFFFFFF is the Int32 -1 in PowerShell)
    if ($c -ge 0xC0000000) { return ('0x{0:X8}' -f $c) }
    return "$code"
}

# The line a mode prints when its work is done (its reports written): the time from it to the process's end is the quit.
$doneLine = 'Town smoke: PASS|Town smoke: FAIL|Town statistics: .*\.json|exit-leak-probe: '
# Runs a console program to its end (with a timeout) and returns its exit code (null: killed at the timeout); output
# to LOG. $script:runSeconds and $script:quitSeconds (from the mode's last line, $doneLine, to the end) time it.
function Invoke-Logged([string]$exe, [string[]]$arguments, [string]$log, [hashtable]$environment = @{}, [int]$timeout = 900) {
    $saved = @{}
    foreach ($k in $environment.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $environment[$k]) }
    $script:runSeconds = $null; $script:quitSeconds = $null
    try {
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $p = Start-Process -FilePath $exe -ArgumentList $arguments -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
        $handle = $p.Handle # keeps the exit code available after the process ends
        $doneAt = $null
        while (-not $p.WaitForExit(2000)) {
            if ($null -eq $doneAt -and (Test-Path $log) -and (Select-String -Path $log -Pattern $doneLine -Quiet)) { $doneAt = $clock.Elapsed.TotalSeconds }
            if ($clock.Elapsed.TotalSeconds -gt $timeout) { $p.Kill($true); break }
        }
        $script:runSeconds = [math]::Round($clock.Elapsed.TotalSeconds)
        if ($null -eq $doneAt -and (Select-String -Path $log -Pattern $doneLine -Quiet)) { $doneAt = $clock.Elapsed.TotalSeconds }
        if ($null -ne $doneAt) { $script:quitSeconds = [math]::Round($clock.Elapsed.TotalSeconds - $doneAt) }
        if (-not $p.HasExited) { return $null }
        return [long]$p.ExitCode
    } finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }
}

function Add-Result([string]$build, [string]$mode, [string]$variant, $code, [string]$log) {
    $text = if ($null -eq $code) { 'timeout' } else { Exit-Text $code }
    $results.Add([pscustomobject]@{ build = $build; mode = $mode; variant = $variant; exit = $text; seconds = $script:runSeconds;
        quitSeconds = $script:quitSeconds; log = (Split-Path -Leaf $log) })
    "{0,-8} {1,-18} {2,-16} exit {3} ({4} s, {5} s after its last line)" -f $build, $mode, $variant, $text, $script:runSeconds, $script:quitSeconds
}

# cdb commands: report the first fatal event, then quit.
$sosLoad = if (Test-Path $sos) { ".load $sos" } else { '.echo (SOS not installed)' }
# ext.heap takes a literal address, so the register goes through an alias (as /x, then .block).
$heapRecord = ''
foreach ($register in 'rbp', 'rcx', 'rax') {
    $heapRecord += ".echo ----- $register; as /x Block_$register @$register; " + '.block { !ext.heap -p -a ${Block_' + $register + '}; !address ${Block_' + $register + '} }; '
}
$report = ".echo ===== EVENT; .lastevent; .exr -1; r; .echo ===== FAULTING THREAD; kn 40; " +
    ".echo ===== PAGE HEAP RECORD (the block at rbp, rcx and rax, where the faulting access may have gone); $heapRecord" +
    ".echo ===== ALL THREADS; ~*kn 30; .echo ===== MODULES; lm; $sosLoad; .echo ===== MANAGED THREADS; !threads; " +
    ".echo ===== MANAGED STACKS; !clrstack -all; q"
$commands = @(
    '.symfix C:\symcache',
    "sxe -c `"$report`" c0000374",
    "sxd -c2 `"$report`" av",
    "sxe -c `"$report`" bpe",
    'g'
)
$commandFile = Join-Path $Out 'cdb-commands.txt'
Set-Content -Path $commandFile -Value $commands

function Invoke-Cdb([string]$exe, [string[]]$arguments, [string]$transcript, [hashtable]$environment = @{}, [int]$timeout = 1500) {
    if (-not (Test-Path $cdb)) { return $null }
    return Invoke-Logged $cdb (@('-G', '-cf', $commandFile, $exe) + $arguments) $transcript $environment $timeout
}

function Show-Transcript([string]$transcript) {
    if (-not (Test-Path $transcript)) { return }
    $lines = Get-Content $transcript
    $at = ($lines | Select-String -Pattern '===== EVENT' | Select-Object -First 1).LineNumber
    if ($at) { $lines | Select-Object -Skip ($at - 1) -First 160 } else { $lines | Select-Object -Last 40 }
}

if ($ReleaseExe) {
    $console = $ReleaseExe -replace '\.exe$', '.console.exe'
    $modes = @(
        @{ mode = 'town-statistics'; flag = '--town-statistics'; variant = 'default'; env = @{} },
        @{ mode = 'exit-leak-probe'; flag = '--exit-leak-probe'; variant = 'leak'; env = @{ MARVIN_EXIT_PROBE = 'leak' } },
        @{ mode = 'exit-leak-probe'; flag = '--exit-leak-probe'; variant = 'free'; env = @{ MARVIN_EXIT_PROBE = 'free' } }
    )
    "== Exit codes (headless, Dummy audio)"
    foreach ($m in $modes) {
        for ($i = 1; $i -le $Runs; $i++) {
            $dir = Join-Path $Out "release-$($m.mode)-$($m.variant)-$i"; New-Item -ItemType Directory -Force $dir | Out-Null
            $code = Invoke-Logged $console @('--headless', '--audio-driver', 'Dummy', $m.flag, $dir) "$dir.log" $m.env
            Add-Result 'release' $m.mode "$($m.variant) #$i" $code "$dir.log"
        }
        if ($DebugExe) {
            $dir = Join-Path $Out "debug-$($m.mode)-$($m.variant)"; New-Item -ItemType Directory -Force $dir | Out-Null
            $code = Invoke-Logged ($DebugExe -replace '\.exe$', '.console.exe') @('--headless', '--audio-driver', 'Dummy', $m.flag, $dir) "$dir.log" $m.env
            Add-Result 'debug' $m.mode $m.variant $code "$dir.log"
        }
    }
    if ((Test-Path $gflags) -and (Test-Path $cdb)) {
        "== Full page heap for MarvinSimulator.exe"
        & $gflags /p /enable MarvinSimulator.exe /full | Out-String
        try {
            foreach ($m in $modes) {
                $dir = Join-Path $Out "pageheap-release-$($m.mode)-$($m.variant)"; New-Item -ItemType Directory -Force $dir | Out-Null
                $transcript = "$dir.cdb.txt"
                $code = Invoke-Cdb $ReleaseExe @('--headless', '--audio-driver', 'Dummy', $m.flag, $dir) $transcript $m.env
                Add-Result 'release+pageheap(cdb)' $m.mode $m.variant $code $transcript
                "---- $($m.mode) ($($m.variant)) under cdb, release, full page heap"
                Show-Transcript $transcript
            }
            if ($DebugExe) {
                $dir = Join-Path $Out 'pageheap-debug-town-statistics'; New-Item -ItemType Directory -Force $dir | Out-Null
                $transcript = "$dir.cdb.txt"
                $code = Invoke-Cdb $DebugExe @('--headless', '--audio-driver', 'Dummy', '--town-statistics', $dir) $transcript
                Add-Result 'debug+pageheap(cdb)' 'town-statistics' 'default' $code $transcript
                "---- town-statistics under cdb, debug, full page heap"
                Show-Transcript $transcript
            }
        } finally {
            & $gflags /p /disable MarvinSimulator.exe | Out-String
        }
    } else { "cdb or gflags (Debugging Tools for Windows) missing on this runner: no page heap runs" }
}

if ($Godot -and $Rendered) {
    # The town built with a rendering driver (--town-statistics: the town is built, nothing drawn), -Runs times, then the
    # rendered town smoke (-TownSmoke; about 3 minutes on WARP and 20 on lavapipe, longer under cdb).
    $console = $Godot -replace '\.exe$', '_console.exe'
    $engine = @('--path', $Project, '--rendering-driver', $Rendered, '--audio-driver', 'Dummy', '--')
    for ($i = 1; $i -le $Runs; $i++) {
        $dir = Join-Path $Out "editor-$Rendered-town-statistics-$i"; New-Item -ItemType Directory -Force $dir | Out-Null
        $code = Invoke-Logged $console ($engine + @('--town-statistics', $dir)) "$dir.log"
        Add-Result "editor+$Rendered" 'town-statistics' "#$i" $code "$dir.log"
    }
    if ($TownSmoke) {
        $dir = Join-Path $Out "editor-$Rendered-town-smoke-test"; New-Item -ItemType Directory -Force $dir | Out-Null
        $pins = @{ MARVIN_GRID_SLOTS = '0,3,1,2'; MARVIN_TOWN_DAYLIGHT = '0.2125,4.18' }
        if ($Debugger) {
            $transcript = "$dir.cdb.txt"
            $code = Invoke-Cdb $Godot ($engine + @('--town-smoke-test', $dir)) $transcript $pins $SmokeTimeout
            Add-Result "editor+$Rendered(cdb)" 'town-smoke-test' 'pinned' $code $transcript
            "---- town-smoke-test under cdb, editor runtime, $Rendered"
            Show-Transcript $transcript
        } else {
            $code = Invoke-Logged $console ($engine + @('--town-smoke-test', $dir)) "$dir.log" $pins $SmokeTimeout
            Add-Result "editor+$Rendered" 'town-smoke-test' 'pinned' $code "$dir.log"
        }
    }
}

$json = Join-Path $Out 'exit-diagnostics.json'
$all = @()
if (Test-Path $json) { $all += @(Get-Content $json -Raw | ConvertFrom-Json) }
$all += $results
ConvertTo-Json -InputObject @($all) | Set-Content $json
if ($env:GITHUB_STEP_SUMMARY) {
    $md = @('## Exit diagnostics', '', '| Build | Mode | Variant | Exit | Seconds (quit) |', '|---|---|---|---|---|')
    foreach ($r in $results) { $md += "| $($r.build) | $($r.mode) | $($r.variant) | $($r.exit) | $($r.seconds) ($($r.quitSeconds)) |" }
    $md += ''
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $md
}
