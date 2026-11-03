#!/usr/bin/env bash
# Bench a committed revision of our submission (default HEAD) the same way bench.sh benches the working tree.
#   bash dev/docker.sh run 'bash submissions/csharp-iouring-MrRoza/dev/bench-rev.sh <rev> [conns] [secs]'
set -euo pipefail
REV=${1:-HEAD}; shift || true
SUB=csharp-iouring-MrRoza
rm -rf /w/rev && mkdir -p /w/rev
git -C /repo archive "$REV" submissions/$SUB | tar -x -C /w/rev
SRC=/w/rev/submissions/$SUB SUBDIR=rev bash /repo/submissions/$SUB/dev/bench.sh "$@"
