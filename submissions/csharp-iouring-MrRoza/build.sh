#!/usr/bin/env bash
# Builds as a normal user: SQLite from checksummed source (static), then the NativeAOT server.
#   LOG=1 bash build.sh   -> compiles the trace logging in (off by default: the calls don't exist in the binary)
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1

SQLITE=sqlite-autoconf-3530400
SQLITE_SHA256=0e9483900e92cd5de8fd48d16bf9200145a61f7fd5be542a5ac81d8a9516eb9c
OUT=obj/sqlite
if [ ! -f "$OUT/libsqlite3.a" ]; then
  rm -rf "$OUT" && mkdir -p "$OUT"
  curl -fsSL --retry 3 -o "$OUT/$SQLITE.tar.gz" "https://www.sqlite.org/2026/$SQLITE.tar.gz"
  echo "$SQLITE_SHA256  $OUT/$SQLITE.tar.gz" | sha256sum -c -
  tar -xzf "$OUT/$SQLITE.tar.gz" -C "$OUT" --strip-components=1 "$SQLITE/sqlite3.c" "$SQLITE/sqlite3.h"
  gcc -O3 -march=native -fno-plt -DNDEBUG \
    -DSQLITE_THREADSAFE=2 -DSQLITE_DEFAULT_MEMSTATUS=0 -DSQLITE_DQS=0 -DSQLITE_OMIT_DEPRECATED \
    -DSQLITE_OMIT_SHARED_CACHE -DSQLITE_OMIT_LOAD_EXTENSION -DSQLITE_OMIT_PROGRESS_CALLBACK \
    -DSQLITE_USE_ALLOCA -DHAVE_FDATASYNC=1 -DHAVE_USLEEP=1 \
    -c "$OUT/sqlite3.c" -o "$OUT/sqlite3.o"
  ar rcs "$OUT/libsqlite3.a" "$OUT/sqlite3.o"
fi

LOGFLAG=false; [ "${LOG:-0}" = 1 ] && LOGFLAG=true
dotnet publish Server.csproj -c Release -p:RestoreLockedMode=true -p:Log=$LOGFLAG -o bin
