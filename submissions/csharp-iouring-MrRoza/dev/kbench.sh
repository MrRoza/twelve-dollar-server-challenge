#!/usr/bin/env bash
# Inside the dev container: the official bench/load.js user model with think times divided by SPEED
# (open loop, like the real benchmark), VUS connections. Server pinned to CPU 0, k6 on CPUs 2-21.
# Reports server CPU µs per request over the whole run, plus k6 latency and errors.
#   bash dev/docker.sh run 'bash submissions/csharp-iouring-MrRoza/dev/kbench.sh [vus] [speed] [hold]'
#   REV=<git rev> benches a committed revision instead of the working tree.
set -euo pipefail
VUS=${1:-20000}; SPEED=${2:-8}; HOLD=${3:-20s}
SUB=csharp-iouring-MrRoza; HERE=/repo/submissions/$SUB/dev
mkdir -p /w/submissions "$HERE/out"
[ -f /w/seed.db ] || cp /repo/seed/feed.db /w/seed.db
if [ -n "${REV:-}" ]; then
  rm -rf /w/rev && mkdir -p /w/rev && git -C /repo archive "$REV" submissions/$SUB | tar -x -C /w/rev
  SRC=/w/rev/submissions/$SUB; DST=/w/submissions/rev
else SRC=/repo/submissions/$SUB; DST=/w/submissions/$SUB; fi
mkdir -p $DST; rm -rf $DST/src; cp -r $SRC/. $DST/
( cd $DST && PROFILE=${PERF:-0} LOG=${LOG:-0} bash build.sh >/w/build.log 2>&1 ) || { tail -30 /w/build.log; exit 1; }
# load.js with every think time divided by SPEED
sed -e "s|sleep(between(\([^)]*\)))|sleep(between(\1) / $SPEED)|g" /repo/bench/load.js > /w/load-fast.js
grep -c "/ $SPEED)" /w/load-fast.js >/dev/null
rm -f /w/feed.db*; cp /w/seed.db /w/feed.db
ulimit -n 1048576   # k6 needs one fd per VU
# The server gets exactly the benchmark's LimitNOFILE.
( ulimit -n 65535; SQLITE_PATH=/w/feed.db JWT_SECRET=twelve-dollar-challenge HOST=127.0.0.1 PORT=3000 exec taskset -c 0 $DST/bin/server >/w/server.log 2>&1 ) &
PID=$!
trap 'kill $PID 2>/dev/null || true' EXIT
for _ in $(seq 50); do curl -sf http://127.0.0.1:3000/health >/dev/null && break; sleep 0.1; done
read -r u0 s0 < <(awk '{print $14, $15}' /proc/$PID/stat)
if [ "${PERF:-0}" = 1 ]; then
  PERFBIN=$(ls /usr/lib/linux-tools/*/perf | head -1)
  ( sleep 12; $PERFBIN record -F 1999 -g -p $PID -o /w/perf.data -- sleep 15 >/w/perf.log 2>&1 ) &
fi
taskset -c 2-21 k6 run -q --no-color ${K6_LOCAL_IPS:+--local-ips=$K6_LOCAL_IPS} -e VUS=$VUS -e RAMP_UP=${RAMP:-10s} -e DURATION=$HOLD -e TOKENS=/repo/seed/tokens.json \
  -e BASE_URL=http://127.0.0.1:3000 --summary-export /w/k6.json /w/load-fast.js > /w/k6.txt 2>&1 || true
read -r u1 s1 < <(awk '{print $14, $15}' /proc/$PID/stat)
TICK=$(getconf CLK_TCK)
N=$(jq '.metrics.http_reqs.count' /w/k6.json); RATE=$(jq '.metrics.http_reqs.rate' /w/k6.json)
P95=$(jq '.metrics.http_req_duration["p(95)"]' /w/k6.json); P99=$(jq '.metrics.http_req_duration["p(99)"]' /w/k6.json)
ERR=$(jq '.metrics.http_req_failed.value' /w/k6.json)
awk -v vus=$VUS -v u=$((u1-u0)) -v s=$((s1-s0)) -v t=$TICK -v n=$N -v r=$RATE -v p95=$P95 -v p99=$P99 -v e=$ERR 'BEGIN{
  printf "vus %s | %d reqs, avg %.0f req/s | p95 %.1f ms p99 %.1f ms err %.3f%% | %.1f us/req (user %.1f, sys %.1f)\n",
    vus, n, r, p95, p99, e*100, (u+s)/t/n*1e6, u/t/n*1e6, s/t/n*1e6 }'
echo "server RSS: $(awk '/VmRSS/ {print $2/1024 " MB"}' /proc/$PID/status)"
if [ "${PERF:-0}" = 1 ]; then
  sleep 1
  $PERFBIN report -i /w/perf.data --no-children --call-graph=none --sort symbol --stdio 2>/dev/null | grep -E "^ +[0-9]" | head -50 | cut -c1-120 > "$HERE/out/perf-top.txt"
  $PERFBIN report -i /w/perf.data --children --sort symbol --stdio -g none 2>/dev/null | grep -E "^ +[0-9]" | head -60 | cut -c1-120 > "$HERE/out/perf-children.txt"
  echo "--- top self"; head -35 "$HERE/out/perf-top.txt"
fi
