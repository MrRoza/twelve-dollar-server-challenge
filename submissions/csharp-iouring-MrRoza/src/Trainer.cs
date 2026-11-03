// Build-time only: `server --train <dir>` is the training run for the profile-guided build of SQLite + shim
// (build.sh). It creates a synthetic database of its own random data in <dir> (none of the seed), then runs the
// load test's request mix (feed, single post, like, new post) through the same Db calls and batches as the
// server, in-process. The instrumented SQLite writes its profile when the process exits.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Twelve;

static unsafe class Trainer
{
    const int Users = 5000, Posts = 40000, Likes = 160000, Requests = 400_000, Batch = 128;

    // The challenge schema (schema.sql), for the synthetic database only.
    const string Schema = """
        CREATE TABLE users (
          id          INTEGER PRIMARY KEY,
          username    TEXT NOT NULL UNIQUE,
          created_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
        );
        CREATE TABLE posts (
          id          INTEGER PRIMARY KEY,
          user_id     INTEGER NOT NULL REFERENCES users(id),
          body        TEXT NOT NULL CHECK (length(body) BETWEEN 1 AND 500),
          created_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
        );
        CREATE TABLE likes (
          user_id     INTEGER NOT NULL REFERENCES users(id),
          post_id     INTEGER NOT NULL REFERENCES posts(id),
          created_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
          PRIMARY KEY (user_id, post_id)
        );
        CREATE INDEX posts_created_at_id_idx ON posts (created_at DESC, id DESC);
        CREATE INDEX posts_user_id_idx       ON posts (user_id);
        CREATE INDEX likes_post_id_idx       ON likes (post_id);
        """;

    static string Data => $"""
        PRAGMA journal_mode=WAL;
        BEGIN;
        WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {Users})
        INSERT INTO users (id, username, created_at)
          SELECT i, 'trainer_' || i, strftime('%Y-%m-%dT%H:%M:%fZ', '2025-01-01', '+' || i || ' seconds') FROM n;
        WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {Posts})
        INSERT INTO posts (id, user_id, body, created_at)
          SELECT i, abs(random()) % {Users} + 1,
                 CASE i % 7 WHEN 0 THEN 'line one' || char(10) || 'a "quoted" \ word' ELSE 'post ' || i || ' ' END
                   || substr(lower(hex(randomblob(140))), 1, abs(random()) % 260 + 1),
                 strftime('%Y-%m-%dT%H:%M:%fZ', '2025-02-01', '+' || (i * 37) || ' seconds')
          FROM n;
        WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {Likes})
        INSERT OR IGNORE INTO likes (user_id, post_id, created_at)
          SELECT abs(random()) % {Users} + 1, abs(random()) % {Posts} + 1,
                 strftime('%Y-%m-%dT%H:%M:%fZ', '2025-06-01', '+' || i || ' seconds')
          FROM n;
        COMMIT;
        """;

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "train.db");
        foreach (string f in new[] { path, path + "-wal", path + "-shm" }) File.Delete(f);
        var sw = Stopwatch.StartNew();
        Create(path);
        Log.Info($"train: synthetic database in {sw.ElapsedMilliseconds} ms");

        var db = new Db(path);
        Db.StartCheckpointer(path);
        Out.Pool = new BufPool(Out.PoolSize);
        Conn* c = (Conn*)NativeMemory.AllocZeroed((nuint)sizeof(Conn));
        db.SelfCheck(c);

        var rng = new Random(12345);
        byte* name = (byte*)NativeMemory.Alloc(64);
        byte* body = (byte*)NativeMemory.Alloc(600);
        long maxPost = Posts;
        int[] kinds = new int[Batch];
        long[] ids = new long[Batch];
        long ok = 0;
        sw.Restart();
        for (int done = 0; done < Requests; done += Batch)
        {
            // The load test's mix per user loop: feed, one post, a like 15% of the time, a new post 2%.
            for (int i = 0; i < Batch; i++)
            {
                double r = rng.NextDouble();
                kinds[i] = r < 0.46 ? 0 : r < 0.92 ? 1 : r < 0.99 ? 2 : 3;
                ids[i] = rng.Next(100) < 2 ? maxPost + 1000 : rng.NextInt64(1, maxPost + 1); // ~2% unknown ids
            }
            if (db.BeginImmediate())
            {
                for (int i = 0; i < Batch; i++)
                {
                    if (kinds[i] < 2) continue;
                    long user = rng.Next(1, Users + 1);
                    Out.Begin(c);
                    if (kinds[i] == 2) { if (db.Like(user, ids[i]) >= 0) ok++; }
                    else
                    {
                        int nl = Encoding.ASCII.GetBytes("trainer_" + user, new Span<byte>(name, 64));
                        int bl = Encoding.UTF8.GetBytes(i % 3 == 0 ? "hello \"world\"\nline two" : "a new post " + done,
                                                        new Span<byte>(body, 600));
                        if (db.InsertPost(user, body, bl, name, nl)) { ok++; maxPost++; }
                    }
                }
                if (!db.Commit()) db.Rollback();
            }
            bool txn = db.Begin();
            for (int i = 0; i < Batch; i++)
            {
                Out.Begin(c);
                if (kinds[i] == 0) { if (db.Feed()) ok++; }
                else if (kinds[i] == 1) { if (db.Post(ids[i]) >= 0) ok++; }
            }
            if (txn) db.EndRead();
            if (done % (Batch * 512) == 0) db.Health();
        }
        Log.Info($"train: {Requests} requests ({ok} ok) in {sw.ElapsedMilliseconds} ms");
        return ok > Requests / 2 ? 0 : 1;
    }

    static void Create(string path)
    {
        nint h;
        byte[] p = Encoding.UTF8.GetBytes(path + "\0");
        fixed (byte* pp = p)
            if (Sqlite.sqlite3_open_v2(pp, &h, Sqlite.OPEN_READWRITE | Sqlite.OPEN_CREATE, null) != Sqlite.OK)
                throw new Exception("train: cannot create " + path);
        foreach (string sql in new[] { Schema, Data })
        {
            byte[] s = Encoding.UTF8.GetBytes(sql + "\0");
            byte* err = null;
            fixed (byte* sp = s)
                if (Sqlite.sqlite3_exec(h, sp, 0, 0, &err) != Sqlite.OK)
                    throw new Exception("train: " + Marshal.PtrToStringUTF8((nint)err));
        }
        Sqlite.sqlite3_close(h);
    }
}
