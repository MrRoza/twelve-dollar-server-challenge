# C# / NativeAOT / raw io_uring / staged batches / SQLite B-tree reads

| | |
|---|---|
| Language | C# 14, .NET 10 (SDK 10.0.401, pinned and SHA-512 checked in `install.sh`), **NativeAOT** |
| HTTP server | None: a hand-written HTTP/1.1 server on **io_uring**, driven through the raw syscalls from C# (no liburing) |
| SQLite | 3.53.4 amalgamation, SHA-256 checked, compiled by `build.sh` (`-O3 -march=native`) and linked statically |
| Driver | None: direct calls into the static SQLite (`DirectPInvoke`). `native/shim.c` `#include`s the **unmodified** amalgamation and adds thin wrappers around its B-tree cursor API |
| NuGet packages | None at runtime (only the ILCompiler NativeAOT needs, pinned in `packages.lock.json`) |
| Deployment | **Direct** on `HOST:PORT` (`0.0.0.0:80`), no Nginx |
| Needs | Linux 6.1+ io_uring (DEFER_TASKRUN, direct descriptors, multishot recv, provided-buffer rings; falls back to multishot poll + recv if the buffer ring is refused) |

`test/run.sh` passes 42/42.

## The idea: a staged pipeline instead of request-at-a-time

The server doesn't take a request, do all of its work and answer it. Requests flow through **stages with
buffers**, and the main loop decides when a buffer is flushed:

1. **Collect.** The loop waits in one `io_uring_enter` until **128 completions** are queued **or 2 ms** have passed.
   Then it drains every completion: accepts, readable sockets, received bytes and finished sends.
2. **Parse.** Each complete request is parsed in place and routed into the *write* buffer (likes and new posts)
   or the *read* buffer (feed, single post, health, errors).
3. **Write stage.** The writes commit together in one `BEGIN IMMEDIATE … COMMIT` once **64** are queued or the
   oldest has waited **5 ms** (group commit). Every commit rewrites the same hot pages into the WAL: the newest
   posts leaf and the index leaves. Fewer, bigger commits write far fewer frames per row. A write's response is
   built inside the transaction and only sent **after** the commit. A failed commit turns them all into 500s.
4. **Read stage.** All reads of the turn run in **one read transaction** (one snapshot), after the writes
   committed in the same turn, so they see them.
5. **Send.** All responses are queued as `SEND`s and go out with the next `io_uring_enter`.

Measured on one core with the k6 user model (below), each stage buffer paid for itself:

| Change | CPU per request |
|---|---:|
| Request-at-a-time baseline (one turn per completion, ~2 requests per batch) | 38.9 µs |
| + collect window (128 completions or 2 ms) | 22.3 µs |
| + write stage (64 writes or 5 ms) | 21.4 µs |
| + B-tree reads, `unix-excl`, small things | 18.4 µs |
| + multishot recv into a provided-buffer ring (no recv SQE per request) | **~18 µs** (60k users: 20.5 → 19.4) |

The windows only add latency while it's quiet. Under load the buffers fill before the timers fire. Locally,
p95 stays under 10 ms at 20,000 users.

## Reads go through SQLite's B-tree, not its SQL engine

`GET /feed` and `GET /posts/:id` open SQLite's own B-tree cursors inside the batch's read transaction, and C#
decodes the record bytes and writes the JSON. There's no bytecode engine and no `sqlite3_column_*`.

- **Feed:** walk `posts_created_at_id_idx` from its first entry. It is `(created_at DESC, id DESC)`, so that
  *is* `ORDER BY created_at DESC, id DESC`. Look the 20 posts up in ascending id order: neighbours land in the
  same leaf, and SQLite's `TableMoveto` steps to the next cell. Then look up the 20 authors in `users`. Then
  count the likes with one range walk of `likes_post_id_idx` when the ids are within 512 of each other, or one
  seek per post otherwise.
- **Live reads (rule 5):** cursors exist only inside a batch's read transaction. Every byte of every response
  is read from SQLite while that request is served. Only cursors' memory, two `KeyInfo`s and the four root page
  numbers (read from `sqlite_schema` at startup) are reused. `auto_vacuum` must be off, so root pages can't
  move, and `unix-excl` keeps other processes off the file.
- **Checked at startup:** the B-tree path must produce byte-for-byte what the SQL path produces for the feed
  and a few posts, or it is switched off and the SQL queries answer. If anything unexpected shows up at runtime
  (a post without a user row, an odd record), that request falls back to SQL.
- **Writes use SQL:** `INSERT … RETURNING id, created_at` for posts, `INSERT … ON CONFLICT DO NOTHING` for likes,
  and `sqlite3_changes` to tell a new like from a repeat.

## Connections: past `LimitNOFILE`, no memory when idle

- Accepted sockets are io_uring **direct descriptors** in a ring's fixed-file table, not process fds. A table
  can't be larger than `RLIMIT_NOFILE`, so rings open lazily, each with `RLIMIT_NOFILE - 64` slots, up to 16
  (over a million connections). Tested locally with 100,000 connections (2 rings): 0 errors, 67 MB RSS.
- Accepts are **one-shot direct accepts**, up to 64 in flight, never more than the ring has free slots. A burst
  can't overrun a table.
- An idle connection holds **no buffer**: just a 72-byte record and a **multishot `RECV`** on the ring's
  provided-buffer ring (4096 buffers of 2 KB, shared by all connections). Arriving bytes are copied into a
  pooled 4 KB buffer and the provided buffer goes straight back, so receiving costs no SQE per request.
  Responses are written into pooled 16 KB buffers, with the headers right-aligned in front of the body (no
  copy). Both pooled buffers go back once the response is sent.
- Ubuntu's 6.8.0-136+ kernels refuse a buffer-ring registration whose reserved words are zero (Launchpad
  #2162843), so a refused registration is retried with `resv[0]=1`. If that's refused too, the ring falls back
  to a multishot `POLLIN` per connection and a `RECV` with `MSG_DONTWAIT` when it fires.
- Closing is `SHUTDOWN` hard-linked to `CLOSE` (the shutdown ends the multishot recv). A generation counter in
  `user_data` drops late completions of a slot's previous connection.
- With one ring the loop sleeps in `io_uring_enter` (`EXT_ARG` timeout = the collect window). With several,
  it polls the rings' completion queues and sleeps in short steps. A `DEFER_TASKRUN` ring's eventfd proved an
  unreliable wake-up: hundreds of missed signals per second on Linux 6.18.

## Durability and checkpoints

- WAL, `synchronous=NORMAL`, every write committed before its response is sent (rule 6).
- `wal_autocheckpoint=0`; a second connection on its own thread runs a `PASSIVE` checkpoint every 250 ms, so
  the fsyncs never block the event loop. Past 20,000 WAL frames it runs a `RESTART`, so the WAL starts over.
  Without restarts the WAL index's hash chains for hot pages grow with every commit. Before the checkpointer
  worked (a connection opens the WAL lazily, so its first checkpoints were silent no-ops), `walFindFrame` cost
  20% of the CPU.
- During a `RESTART` the write lock is taken. The event loop's `BEGIN IMMEDIATE` has `busy_timeout=0`, so it
  fails at once and the writes wait for the next turn (a 1 ms io_uring timeout guarantees that turn). The loop
  never blocks on SQLite.
- `unix-excl` VFS: the WAL index lives in heap memory shared by the two connections. No `-shm` file, no `fcntl`
  lock syscalls per transaction.

## Smaller things

- **JWT:** HS256 with a hand-written SHA-256. The benchmark Xeon has AVX-512 but no SHA-NI. The HMAC key pads
  are hashed once at startup (key schedule); every token's signature is computed and compared, and `alg`,
  `exp`, `sub` and `username` are checked as SPEC.md says.
- **JSON:** a strict validating reader for request bodies and JWT payloads. Output escaping finds the next
  byte to escape with one vectorized `SearchValues` scan.
- **No allocations on the hot path,** so the GC never runs. Short native calls (cursor ops, column reads,
  binds) are `[SuppressGCTransition]`. The runtime costs well under 1% in a profile, so going runtime-less
  (zerosharp style) wouldn't buy anything measurable.
- **Trace logging** is compiled in only with `LOG=1 bash build.sh` (`[Conditional("TRACE_LOG")]`: without it,
  the calls *and their arguments* don't exist in the binary). It prints per-second loop stats and checkpoints.

## Layout

```
install.sh          .NET SDK (pinned, SHA-512) + build-essential
build.sh            SQLite (pinned, SHA-256) + native/shim.c -> static lib; dotnet publish (NativeAOT)
start.sh            exec bin/server
native/shim.c       #include "sqlite3.c" + B-tree cursor wrappers
src/Program.cs      env config, listen socket
src/Server.cs       rings, accept/poll/recv/send, the batch stages
src/Api.cs          the five endpoints
src/Http.cs         request parsing
src/Db.cs           SQL statements, checkpointer, fast/SQL paths, startup self-check
src/Bt.cs           B-tree cursor reads + SQLite record decoding
src/Jwt.cs          SHA-256, HMAC, JWT verification
src/Json.cs, Out.cs JSON in and out
src/Uring.cs        io_uring syscalls and SQE builders
dev/                Docker dev image, test/bench scripts (k6 model and wrk), not used by the benchmark
```

## Dev loop (not part of the submission's runtime)

```bash
bash dev/docker.sh build                                                      # Ubuntu 24.04 image
bash dev/docker.sh run 'bash submissions/csharp-iouring-MrRoza/dev/test.sh'    # test/run.sh, 42 checks
bash dev/docker.sh run 'bash submissions/csharp-iouring-MrRoza/dev/kbench.sh 20000 8 20s'  # k6 model, 1 core
```

`kbench.sh` runs the official `bench/load.js` with every think time divided by a speed factor. That's an open
loop with one connection per user, like the real run. The server is pinned to one core and the script reports
its CPU µs per request. `TWELVE_*` environment variables are read only by a `DEV=1` build for these experiments. The submission
build has the values above compiled in.
