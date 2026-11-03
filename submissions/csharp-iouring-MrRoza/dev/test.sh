#!/usr/bin/env bash
# Inside the dev container: copy the repo (minus other submissions) to /w (native FS, fast), then run
# the official test/run.sh against our submission. Usage (from Windows/Git Bash):
#   bash dev/docker.sh run 'bash submissions/csharp-iouring-MrRoza/dev/test.sh'
set -euo pipefail
SUB=csharp-iouring-MrRoza
mkdir -p /w/submissions
cp -r /repo/test /repo/seed /repo/schema.sql /w/
rm -rf /w/submissions/$SUB && cp -r /repo/submissions/$SUB /w/submissions/
cd /w && bash test/run.sh submissions/$SUB
