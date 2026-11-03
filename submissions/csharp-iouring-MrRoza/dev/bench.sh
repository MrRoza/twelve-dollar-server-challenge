#!/usr/bin/env bash
# Inside the dev container: build our submission in /w, start it pinned to CPU 0 on a fresh DB, drive it with
# wrk (k6 request mix, closed loop) from other CPUs, and report req/s and server CPU µs per request.
#   bash dev/docker.sh run 'bash submissions/csharp-iouring-MrRoza/dev/bench.sh [conns] [seconds] [perf]'
# perf=1 also records a CPU profile of the server (perf report saved to /repo/.../dev/out/).
set -euo pipefail
CONNS=${1:-1000}; SECS=${2:-15}; PERF=${3:-0}
SUB=csharp-iouring-MrRoza; HERE=/repo/submissions/$SUB/dev
mkdir -p /w/submissions "$HERE/out"
[ -f /w/seed.db ] || cp /repo/seed/feed.db /w/seed.db
[ -f /w/tokens.txt ] || jq -r '.[0:5000][].token' /repo/seed/tokens.json > /w/tokens.txt
SRC=${SRC:-/repo/submissions/$SUB}; DST=/w/submissions/${SUBDIR:-$SUB}
mkdir -p $DST; rm -rf $DST/src; cp -r $SRC/. $DST/
( cd $DST && DEV=1 PROFILE=$PERF LOG=${LOG:-0} bash build.sh >/w/build.log 2>&1 ) || { tail -30 /w/build.log; exit 1; }
rm -f /w/feed.db*; cp /w/seed.db /w/feed.db
ulimit -n 65535
SQLITE_PATH=/w/feed.db JWT_SECRET=twelve-dollar-challenge HOST=127.0.0.1 PORT=3000 \
  taskset -c 0 $DST/bin/server >/w/server.log 2>&1 &
PID=$!
trap 'kill $PID 2>/dev/null || true' EXIT
for _ in $(seq 50); do curl -sf http://127.0.0.1:3000/health >/dev/null && break; sleep 0.1; done
# warm-up
taskset -c 2-9 wrk -t8 -c$CONNS -d3s -s $HERE/mix.lua http://127.0.0.1:3000 >/dev/null
read -r u0 s0 < <(awk '{print $14, $15}' /proc/$PID/stat)
if [ "$PERF" = 1 ]; then
  PERFBIN=$(ls /usr/lib/linux-tools/*/perf | head -1)
  $PERFBIN record -F 2999 -g -p $PID -o /w/perf.data -- sleep $SECS >/w/perf.log 2>&1 &
  PP=$!
fi
( [ -n "${STRACE:-}" ] && timeout 5 strace -c -f -p $PID -o /w/strace.txt ) & 
taskset -c 2-9 wrk -t8 -c$CONNS -d${SECS}s -s $HERE/mix.lua --latency http://127.0.0.1:3000 > /w/wrk.txt
read -r u1 s1 < <(awk '{print $14, $15}' /proc/$PID/stat)
echo "minflt $(awk '{print $10}' /proc/$PID/stat) ctxsw $(grep -h ctxt /proc/$PID/task/*/status | awk '{s+=$2} END {print s}')"
[ "$PERF" = 1 ] && wait $PP || true
TICK=$(getconf CLK_TCK)
RPS=$(awk '/Requests\/sec/ {print $2}' /w/wrk.txt)
awk -v u0=$u0 -v s0=$s0 -v u1=$u1 -v s1=$s1 -v t=$TICK -v r=$RPS -v d=$SECS 'BEGIN{u=(u1-u0)/t; s=(s1-s0)/t; n=r*d; printf "req/s %.0f | cpu %.0f%% (user %.0f%% sys %.0f%%) | %.1f us/req (user %.1f, sys %.1f)\n", r, (u+s)/d*100, u/d*100, s/d*100, (u+s)/n*1e6, u/n*1e6, s/n*1e6}'
grep -E "Latency|50%|99%|Non-2xx|Socket errors" /w/wrk.txt || true
echo "server RSS: $(awk '/VmRSS/ {print $2/1024 " MB"}' /proc/$PID/status)"
if [ "$PERF" = 1 ]; then
  $PERFBIN report -i /w/perf.data --no-children --call-graph=none --sort dso,symbol --stdio 2>/dev/null | grep -E "^ +[0-9]" | head -60 > "$HERE/out/perf-top.txt"
  $PERFBIN report -i /w/perf.data --no-children --call-graph=none --sort dso --stdio 2>/dev/null | grep -E "^ +[0-9]" | head -15 > "$HERE/out/perf-dso.txt"
  echo "--- by dso"; cat "$HERE/out/perf-dso.txt"; echo "--- top symbols"; head -40 "$HERE/out/perf-top.txt"
fi
