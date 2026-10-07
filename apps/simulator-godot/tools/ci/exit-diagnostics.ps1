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
# The editor runtime rendered (-Godot GUI_EXE -Rendered d3d12|vulkan [-TownSmoke]): --town-statistics -Runs times,
# then --town-smoke-test under cdb, without page heap.
# Results go to OUT/exit-diagnostics.json (appended to when it exists), the logs and cdb transcripts, and a section of
# the job summary.
param(
    [string]$ReleaseExe, [string]$DebugExe, [string]$Godot, [string]$Project, [string]$Rendered,
    [Parameter(Mandatory = $true)][string]$Out, [int]$Runs = 3, [switch]$TownSmoke
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

# Runs a console program to its end (with a timeout) and returns its exit code; output to LOG.
function Invoke-Logged([string]$exe, [string[]]$arguments, [string]$log, [hashtable]$environment = @{}, [int]$timeout = 900) {
    $saved = @{}
    foreach ($k in $environment.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $environment[$k]) }
    try {
        $p = Start-Process -FilePath $exe -ArgumentList $arguments -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
        $handle = $p.Handle # keeps the exit code available after the process ends
        if (-not $p.WaitForExit($timeout * 1000)) { $p.Kill($true); return $null }
        return [long]$p.ExitCode
    } finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }
}

function Add-Result([string]$build, [string]$mode, [string]$variant, $code, [string]$log) {
    $text = if ($null -eq $code) { 'timeout' } else { Exit-Text $code }
    $results.Add([pscustomobject]@{ build = $build; mode = $mode; variant = $variant; exit = $text; log = (Split-Path -Leaf $log) })
    "{0,-8} {1,-18} {2,-16} exit {3}" -f $build, $mode, $variant, $text
}

# cdb commands: report the first fatal event, then quit.
$sosLoad = if (Test-Path $sos) { ".load $sos" } else { '.echo (SOS not installed)' }
$report = ".echo ===== EVENT; .lastevent; .exr -1; r; .echo ===== FAULTING THREAD; kn 40; " +
    ".echo ===== PAGE HEAP RECORD (rbp, rcx, rax: the block the access went to); !ext.heap -p -a @rbp; !ext.heap -p -a @rcx; !ext.heap -p -a @rax; " +
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
    # rendered town smoke under cdb (-TownSmoke; it takes about 3 minutes on WARP and 20 on lavapipe).
    $console = $Godot -replace '\.exe$', '_console.exe'
    $engine = @('--path', $Project, '--rendering-driver', $Rendered, '--audio-driver', 'Dummy', '--')
    for ($i = 1; $i -le $Runs; $i++) {
        $dir = Join-Path $Out "editor-$Rendered-town-statistics-$i"; New-Item -ItemType Directory -Force $dir | Out-Null
        $code = Invoke-Logged $console ($engine + @('--town-statistics', $dir)) "$dir.log"
        Add-Result "editor+$Rendered" 'town-statistics' "#$i" $code "$dir.log"
    }
    if ($TownSmoke) {
        $dir = Join-Path $Out "editor-$Rendered-town-smoke-test"; New-Item -ItemType Directory -Force $dir | Out-Null
        $transcript = "$dir.cdb.txt"
        $pins = @{ MARVIN_GRID_SLOTS = '0,3,1,2'; MARVIN_TOWN_DAYLIGHT = '0.2125,4.18' }
        $code = Invoke-Cdb $Godot ($engine + @('--town-smoke-test', $dir)) $transcript $pins 2400
        Add-Result "editor+$Rendered(cdb)" 'town-smoke-test' 'pinned' $code $transcript
        "---- town-smoke-test under cdb, editor runtime, $Rendered"
        Show-Transcript $transcript
    }
}

$json = Join-Path $Out 'exit-diagnostics.json'
$all = @()
if (Test-Path $json) { $all += @(Get-Content $json -Raw | ConvertFrom-Json) }
$all += $results
ConvertTo-Json -InputObject @($all) | Set-Content $json
if ($env:GITHUB_STEP_SUMMARY) {
    $md = @('## Exit diagnostics', '', '| Build | Mode | Variant | Exit |', '|---|---|---|---|')
    foreach ($r in $results) { $md += "| $($r.build) | $($r.mode) | $($r.variant) | $($r.exit) |" }
    $md += ''
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $md
}
