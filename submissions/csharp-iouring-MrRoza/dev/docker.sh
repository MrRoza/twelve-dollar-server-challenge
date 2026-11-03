#!/usr/bin/env bash
# Run a command inside the dev container with the repo mounted at /repo.
#   bash dev/docker.sh build                  # build the image once
#   bash dev/docker.sh run 'bash test/run.sh submissions/csharp-iouring-MrRoza'
# io_uring needs seccomp=unconfined (Docker's default profile blocks it); perf needs SYS_ADMIN.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../../.." && pwd)"
IMG=twelve-csharp-dev
case "${1:-}" in
  build) docker build -t $IMG -f "$HERE/Dockerfile" "$HERE/.." ;;
  run)   shift; MSYS_NO_PATHCONV=1 docker run --rm -i --security-opt seccomp=unconfined --cap-add SYS_ADMIN \
           -v "$REPO:/repo" -v twelve-nuget:/root/.nuget -w /repo $IMG bash -lc "$*" ;;
  *) echo "usage: dev/docker.sh build | run '<cmd>'"; exit 1 ;;
esac
