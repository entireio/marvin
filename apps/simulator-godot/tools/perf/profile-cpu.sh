#!/bin/bash
# profile-cpu.sh OUT ATTACH_AT SECONDS [--sample] -- <run-benchmark.py arguments without OUT>
# Runs tools/perf/run-benchmark.py into OUT and, ATTACH_AT seconds after launch, records SECONDS of managed stacks of the
# Godot process with dotnet-trace (dotnet-sampled-thread-time, ~100 Hz: on-CPU managed frames and time in native code
# by calling managed frame) as OUT/cpu.speedscope.json; with --sample also a native `sample` of all threads afterwards
# (OUT/sample.txt; Godot's own C++ frames are unsymbolized in the official build, thread names and system frames are).
# Summarize with: speedscope-top.py OUT/cpu.speedscope.json 40 main
set -u
OUT=$1; AT=$2; SECS=$3; shift 3
NATIVE=0; [ "${1:-}" = "--sample" ] && { NATIVE=1; shift; }
[ "${1:-}" = "--" ] && shift
here=$(cd "$(dirname "$0")" && pwd)
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet}
mkdir -p "$OUT"
python3 "$here/run-benchmark.py" "$OUT" "$@" > "$OUT/run.log" 2>&1 &
RUNNER=$!
for i in $(seq 1 100); do
  PID=""
  for c in $(pgrep -P $RUNNER 2>/dev/null); do ps -o comm= -p $c | grep -q -E 'MacOS/(Godot|MarvinSimulator|Marvin Simulator)$' && PID=$c; done
  [ -n "$PID" ] && break
  sleep 0.2
done
echo "game pid $PID"
sleep "$AT"
"$HOME/.dotnet/tools/dotnet-trace" collect -p "$PID" --profile dotnet-sampled-thread-time --duration 00:00:$(printf %02d "$SECS") \
  -o "$OUT/cpu.nettrace" --format Speedscope > "$OUT/dotnet-trace.log" 2>&1
[ -f "$OUT/cpu.speedscope.json" ] || mv "$OUT"/cpu*.speedscope.json "$OUT/cpu.speedscope.json" 2>/dev/null
if [ $NATIVE = 1 ] && kill -0 "$PID" 2>/dev/null; then sample "$PID" 8 -file "$OUT/sample.txt" > /dev/null 2>&1; fi
wait $RUNNER
command cat "$OUT/run.log"
