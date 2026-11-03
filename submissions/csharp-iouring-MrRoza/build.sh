#!/usr/bin/env bash
# Builds as a normal user: SQLite from checksummed source (static), then the NativeAOT server.
#   LOG=1 bash build.sh      -> compiles the trace logging in (off by default: the calls don't exist in the binary)
#   DEV=1 bash build.sh      -> reads TWELVE_* tunables from the environment (dev experiments only)
#   PROFILE=1 bash build.sh  -> SQLite's internal functions get global symbols so perf can name them (dev only)
#   PGO=0 bash build.sh      -> plain -O3 SQLite (default: profile-guided, trained at build time, ~1-2 extra minutes)
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1

SQLITE=sqlite-autoconf-3530400
SQLITE_SHA256=0e9483900e92cd5de8fd48d16bf9200145a61f7fd5be542a5ac81d8a9516eb9c
SRC=obj/sqlite-src; mkdir -p "$SRC"
[ -f "$SRC/$SQLITE.tar.gz" ] || curl -fsSL --retry 3 -o "$SRC/$SQLITE.tar.gz" "https://www.sqlite.org/2026/$SQLITE.tar.gz"
echo "$SQLITE_SHA256  $SRC/$SQLITE.tar.gz" | sha256sum -c -
[ -f "$SRC/sqlite3.c" ] || tar -xzf "$SRC/$SQLITE.tar.gz" -C "$SRC" --strip-components=1 "$SQLITE/sqlite3.c" "$SQLITE/sqlite3.h"

EXTRA=()
[ "${PROFILE:-0}" = 1 ] && EXTRA=(-DSQLITE_PRIVATE= -fno-omit-frame-pointer)
# native/shim.c #includes the unmodified amalgamation and adds B-tree cursor wrappers (one translation unit).
sqlite_lib() { # <dir> <extra gcc flags...>: compiles $1/sqlite3.o into $1/libsqlite3.a
  local dir=$1; shift
  mkdir -p "$dir"
  gcc -O3 -march=native -fno-plt -DNDEBUG     -DSQLITE_THREADSAFE=2 -DSQLITE_DEFAULT_MEMSTATUS=0 -DSQLITE_DQS=0 -DSQLITE_OMIT_DEPRECATED     -DSQLITE_OMIT_SHARED_CACHE -DSQLITE_OMIT_LOAD_EXTENSION -DSQLITE_OMIT_PROGRESS_CALLBACK     -DSQLITE_USE_ALLOCA -DHAVE_FDATASYNC=1 -DHAVE_USLEEP=1 "${EXTRA[@]}" "$@"     -I"$SRC" -c native/shim.c -o "$dir/sqlite3.o"
  rm -f "$dir/libsqlite3.a"; ar rcs "$dir/libsqlite3.a" "$dir/sqlite3.o"
}

LOGFLAG=false; [ "${LOG:-0}" = 1 ] && LOGFLAG=true
DEVFLAG=false; [ "${DEV:-0}" = 1 ] && DEVFLAG=true   # DEV=1: TWELVE_* env tunables (experiments only)
publish() { # <sqlite lib> <output dir> [extra msbuild args]
  local lib=$1 out=$2; shift 2
  dotnet publish Server.csproj -c Release -p:RestoreLockedMode=true -p:Log=$LOGFLAG -p:Dev=$DEVFLAG -p:SqliteLib="$lib" -o "$out" "$@"
}

if [ "${PGO:-1}" = 1 ]; then
  # Profile-guided SQLite: an instrumented build runs `server --train` (a synthetic database of its own random
  # data and the load test's request mix, in-process; see src/Trainer.cs), then SQLite + shim are compiled
  # again with the profile. Both compiles write the same object path, so the profile matches it.
  OUT=obj/sqlite-pgo; PGODIR="$PWD/obj/pgo-data"
  if [ ! -f "$OUT/libsqlite3.a" ] || [ native/shim.c -nt "$OUT/libsqlite3.a" ] || [ src/Trainer.cs -nt "$OUT/libsqlite3.a" ]; then
    rm -rf "$PGODIR" obj/pgo-train
    sqlite_lib "$OUT" -fprofile-generate -fprofile-update=single -fprofile-dir="$PGODIR"
    publish "$OUT/libsqlite3.a" obj/pgo-train -p:PgoGen=true
    if obj/pgo-train/server --train obj/pgo-train/db; then
      sqlite_lib "$OUT" -fprofile-use -fprofile-dir="$PGODIR" -fprofile-partial-training -Wno-missing-profile
    else
      echo "PGO training failed; plain build" >&2
      sqlite_lib "$OUT"
    fi
    rm -rf obj/pgo-train
  fi
else
  OUT=obj/sqlite; [ "${PROFILE:-0}" = 1 ] && OUT=obj/sqlite-prof
  if [ ! -f "$OUT/libsqlite3.a" ] || [ native/shim.c -nt "$OUT/libsqlite3.a" ]; then sqlite_lib "$OUT"; fi
fi
publish "$OUT/libsqlite3.a" bin
