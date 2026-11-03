#!/usr/bin/env bash
# Run a command inside the long-lived dev container (Ubuntu 24.04, repo mounted at /repo, scratch at /w).
#   bash dev/docker.sh build                  # build the image once
#   bash dev/docker.sh run 'bash submissions/csharp-iouring-MrRoza/dev/test.sh'
#   bash dev/docker.sh stop                   # remove the container
# io_uring needs seccomp=unconfined (Docker's default profile blocks it); perf needs SYS_ADMIN + perf_event.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../../.." && pwd)"
IMG=twelve-csharp-dev; CT=twelve-dev
export MSYS_NO_PATHCONV=1
case "${1:-}" in
  build) docker build -t $IMG -f "$HERE/Dockerfile" "$HERE/.." ;;
  run)   shift
         if [ -z "$(docker ps -q -f name=^$CT$)" ]; then
           docker rm -f $CT >/dev/null 2>&1 || true
           docker run -d --name $CT --security-opt seccomp=unconfined --cap-add SYS_ADMIN --privileged \
             --ulimit nofile=65535:65535 -v "$REPO:/repo" -v twelve-nuget:/root/.nuget -w /repo $IMG sleep infinity >/dev/null
         fi
         docker exec -i -w /repo $CT bash -lc "$*" ;;
  stop)  docker rm -f $CT ;;
  *) echo "usage: dev/docker.sh build | run '<cmd>' | stop"; exit 1 ;;
esac
