// SQLite access: one connection for the event loop (reads + group-committed writes) and one for the
// checkpoint thread. Prepared statements are reused (they cache the plan, not data).
using System.Runtime.InteropServices;
using System.Text;

namespace Twelve;

unsafe sealed class Db
{
    readonly nint _db;
    readonly nint _begin, _beginImmediate, _commit, _rollback, _health, _feed, _post, _insertPost, _postExists, _insertLike;
    readonly nint _feedIds, _feedLikes, _feedRows;
    readonly byte* _feedScratch = (byte*)NativeMemory.Alloc(FeedScratchSize);
    const int FeedScratchSize = 1 << 18; // 20 posts x 500 chars x 6 bytes worst-case escaping, with room to spare
    const int FeedRangeMax = 512;

    const string PostCols =
        "SELECT p.id, p.body, p.created_at, u.username, (SELECT count(*) FROM likes l WHERE l.post_id = p.id) " +
        "FROM posts p JOIN users u ON u.id = p.user_id ";

    public Db(string path)
    {
        _db = Open(path);
        Exec(_db, "PRAGMA journal_mode=WAL");
        Exec(_db, "PRAGMA synchronous=NORMAL");
        Exec(_db, "PRAGMA wal_autocheckpoint=0"); // the checkpoint thread does it, off the request path
        Exec(_db, "PRAGMA cache_size=-262144");    // 256 MB page cache
        Exec(_db, "PRAGMA mmap_size=536870912");
        Exec(_db, "PRAGMA temp_store=MEMORY");
        Exec(_db, "PRAGMA busy_timeout=0"); // never block the event loop: a busy write retries next turn
        _begin = Prep("BEGIN");
        _beginImmediate = Prep("BEGIN IMMEDIATE");
        _commit = Prep("COMMIT");
        _rollback = Prep("ROLLBACK");
        _health = Prep("SELECT 1");
        _feed = Prep(PostCols + "ORDER BY p.created_at DESC, p.id DESC LIMIT 20");
        _post = Prep(PostCols + "WHERE p.id = ?");
        // The fast feed: three range scans instead of 20 x (post seek + user seek + likes count).
        _feedIds = Prep("SELECT id FROM posts ORDER BY created_at DESC, id DESC LIMIT 20");
        _feedLikes = Prep("SELECT post_id, count(*) FROM likes WHERE post_id BETWEEN ?1 AND ?2 GROUP BY post_id");
        _feedRows = Prep("SELECT p.id, p.body, p.created_at, u.username FROM posts p JOIN users u ON u.id = p.user_id " +
                         "WHERE p.id BETWEEN ?1 AND ?2");
        _insertPost = Prep("INSERT INTO posts (user_id, body) VALUES (?, ?) RETURNING id, created_at");
        _postExists = Prep("SELECT 1 FROM posts WHERE id = ?");
        _insertLike = Prep("INSERT INTO likes (user_id, post_id) VALUES (?, ?) ON CONFLICT (user_id, post_id) DO NOTHING");
    }

    static nint Open(string path)
    {
        nint db;
        byte[] p = Encoding.UTF8.GetBytes(path + "\0");
        fixed (byte* pp = p)
            if (Sqlite.sqlite3_open_v2(pp, &db, Sqlite.OPEN_READWRITE | Sqlite.OPEN_NOMUTEX, null) != Sqlite.OK)
                throw new Exception("cannot open " + path);
        return db;
    }

    static void Exec(nint db, string sql)
    {
        byte[] s = Encoding.UTF8.GetBytes(sql + "\0");
        fixed (byte* sp = s)
            if (Sqlite.sqlite3_exec(db, sp, 0, 0, null) != Sqlite.OK)
                throw new Exception(sql + ": " + Marshal.PtrToStringUTF8((nint)Sqlite.sqlite3_errmsg(db)));
    }

    nint Prep(string sql)
    {
        byte[] s = Encoding.UTF8.GetBytes(sql + "\0");
        nint st;
        fixed (byte* sp = s)
            if (Sqlite.sqlite3_prepare_v3(_db, sp, -1, 1 /*PERSISTENT*/, &st, null) != Sqlite.OK)
                throw new Exception(sql + ": " + Marshal.PtrToStringUTF8((nint)Sqlite.sqlite3_errmsg(_db)));
        return st;
    }

    static bool Run(nint st)
    {
        int rc = Sqlite.sqlite3_step(st);
        Sqlite.sqlite3_reset(st);
        return rc == Sqlite.DONE || rc == Sqlite.ROW;
    }

    public bool Begin() => Run(_begin);
    /// <summary>False when the write lock is taken (SQLITE_BUSY): never waits.</summary>
    public bool BeginImmediate() => Run(_beginImmediate);
    public bool InTransaction => Sqlite.sqlite3_get_autocommit(_db) == 0;
    public bool Commit() => Run(_commit);
    public void Rollback() => Run(_rollback);

    public string? Health()
    {
        int rc = Sqlite.sqlite3_step(_health);
        Sqlite.sqlite3_reset(_health);
        return rc == Sqlite.ROW ? null : Marshal.PtrToStringUTF8((nint)Sqlite.sqlite3_errmsg(_db)) ?? "error";
    }

    /// <summary>Post object from a row (id, body, created_at, username).</summary>
    static void WritePost(nint st, long likeCount)
    {
        Out.Raw("{\"id\":"u8); Out.Int(Sqlite.sqlite3_column_int64(st, 0));
        Out.Raw(",\"body\":"u8);
        byte* b = Sqlite.sqlite3_column_text(st, 1); Out.Str(b, Sqlite.sqlite3_column_bytes(st, 1));
        Out.Raw(",\"created_at\":\""u8);
        byte* t = Sqlite.sqlite3_column_text(st, 2); Out.Raw(t, Sqlite.sqlite3_column_bytes(st, 2));
        Out.Raw("\",\"author\":"u8);
        byte* u = Sqlite.sqlite3_column_text(st, 3); Out.Str(u, Sqlite.sqlite3_column_bytes(st, 3));
        Out.Raw(",\"like_count\":"u8); Out.Int(likeCount);
        Out.Raw("}"u8);
    }

    static void WritePost(nint st) => WritePost(st, Sqlite.sqlite3_column_int64(st, 4));

    /// <summary>
    /// Writes the feed body. The 20 newest ids come from the (created_at, id) index. New posts get increasing
    /// ids, so those ids nearly always sit in a narrow id range: then one grouped range scan of likes_post_id_idx
    /// counts all their likes and one rowid range scan of posts (joined to users) fetches all their rows.
    /// Rows arrive in id order and are re-ordered to feed order. Any other shape uses the reference query.
    /// Everything runs in the batch's read transaction, so all three scans see the same snapshot.
    /// </summary>
    public bool Feed()
    {
        long* ids = stackalloc long[20];
        int n = 0, rc;
        while ((rc = Sqlite.sqlite3_step(_feedIds)) == Sqlite.ROW && n < 20) ids[n++] = Sqlite.sqlite3_column_int64(_feedIds, 0);
        Sqlite.sqlite3_reset(_feedIds);
        if (rc != Sqlite.DONE && rc != Sqlite.ROW) return false;
        if (n == 0) { Out.Raw("{\"posts\":[]}"u8); return true; }
        long min = ids[0], max = ids[0];
        for (int i = 1; i < n; i++) { if (ids[i] < min) min = ids[i]; if (ids[i] > max) max = ids[i]; }
        if (max - min >= FeedRangeMax) return FeedReference();

        int range = (int)(max - min + 1);
        sbyte* slot = stackalloc sbyte[range];
        new Span<byte>(slot, range).Fill(0xFF);
        for (int i = 0; i < n; i++) slot[ids[i] - min] = (sbyte)i;
        long* likes = stackalloc long[20];
        new Span<long>(likes, 20).Clear();

        Sqlite.sqlite3_bind_int64(_feedLikes, 1, min);
        Sqlite.sqlite3_bind_int64(_feedLikes, 2, max);
        while ((rc = Sqlite.sqlite3_step(_feedLikes)) == Sqlite.ROW)
        {
            int sl = slot[Sqlite.sqlite3_column_int64(_feedLikes, 0) - min];
            if (sl >= 0) likes[sl] = Sqlite.sqlite3_column_int64(_feedLikes, 1);
        }
        Sqlite.sqlite3_reset(_feedLikes);
        if (rc != Sqlite.DONE) return false;

        // Write each post object as its row arrives, remembering where it landed; then put them in feed order.
        Out.Raw("{\"posts\":["u8);
        int baseOff = Out.Pos;
        int* fragOff = stackalloc int[20];
        int* fragLen = stackalloc int[20];
        int found = 0;
        Sqlite.sqlite3_bind_int64(_feedRows, 1, min);
        Sqlite.sqlite3_bind_int64(_feedRows, 2, max);
        while ((rc = Sqlite.sqlite3_step(_feedRows)) == Sqlite.ROW)
        {
            int sl = slot[Sqlite.sqlite3_column_int64(_feedRows, 0) - min];
            if (sl < 0) continue;
            int start = Out.Pos;
            WritePost(_feedRows, likes[sl]);
            fragOff[sl] = start - baseOff; fragLen[sl] = Out.Pos - start;
            found++;
        }
        Sqlite.sqlite3_reset(_feedRows);
        if (rc != Sqlite.DONE) return false;
        int total = Out.Pos - baseOff;
        if (found != n || total > FeedScratchSize)
        {
            Out.Pos = baseOff - 10; // drop the prefix too
            return FeedReference();
        }
        Buffer.MemoryCopy(Out.At(baseOff), _feedScratch, FeedScratchSize, total);
        Out.Pos = baseOff;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) Out.Raw(","u8);
            Out.Raw(_feedScratch + fragOff[i], fragLen[i]);
        }
        Out.Raw("]}"u8);
        return true;
    }

    /// <summary>The reference query: 20 rows, each with its own like count subquery.</summary>
    bool FeedReference()
    {
        Out.Raw("{\"posts\":["u8);
        int rc, i = 0;
        while ((rc = Sqlite.sqlite3_step(_feed)) == Sqlite.ROW)
        {
            if (i++ > 0) Out.Raw(","u8);
            WritePost(_feed);
        }
        Sqlite.sqlite3_reset(_feed);
        Out.Raw("]}"u8);
        return rc == Sqlite.DONE;
    }

    /// <summary>1 = written, 0 = not found, -1 = error.</summary>
    public int Post(long id)
    {
        Sqlite.sqlite3_bind_int64(_post, 1, id);
        int rc = Sqlite.sqlite3_step(_post);
        int r = -1;
        if (rc == Sqlite.ROW) { Out.Raw("{\"post\":"u8); WritePost(_post); Out.Raw("}"u8); r = 1; }
        else if (rc == Sqlite.DONE) r = 0;
        Sqlite.sqlite3_reset(_post);
        return r;
    }

    /// <summary>Inserts a post and writes the 201 body. False on error (nothing written).</summary>
    public bool InsertPost(long userId, byte* body, int bodyLen, byte* name, int nameLen)
    {
        Sqlite.sqlite3_bind_int64(_insertPost, 1, userId);
        Sqlite.sqlite3_bind_text(_insertPost, 2, body, bodyLen, Sqlite.STATIC);
        int rc = Sqlite.sqlite3_step(_insertPost);
        bool ok = rc == Sqlite.ROW;
        if (ok)
        {
            Out.Raw("{\"post\":{\"id\":"u8); Out.Int(Sqlite.sqlite3_column_int64(_insertPost, 0));
            Out.Raw(",\"body\":"u8); Out.Str(body, bodyLen);
            Out.Raw(",\"created_at\":\""u8);
            Out.Raw(Sqlite.sqlite3_column_text(_insertPost, 1), Sqlite.sqlite3_column_bytes(_insertPost, 1));
            Out.Raw("\",\"author\":"u8); Out.Str(name, nameLen);
            Out.Raw(",\"like_count\":0}}"u8);
            ok = Sqlite.sqlite3_step(_insertPost) == Sqlite.DONE;
        }
        Sqlite.sqlite3_reset(_insertPost);
        Sqlite.sqlite3_bind_text(_insertPost, 2, null, 0, Sqlite.STATIC);
        return ok;
    }

    /// <summary>0 = post not found, 1 = new like, 2 = already liked, -1 = error.</summary>
    public int Like(long userId, long postId)
    {
        Sqlite.sqlite3_bind_int64(_postExists, 1, postId);
        int rc = Sqlite.sqlite3_step(_postExists);
        Sqlite.sqlite3_reset(_postExists);
        if (rc == Sqlite.DONE) return 0;
        if (rc != Sqlite.ROW) return -1;
        Sqlite.sqlite3_bind_int64(_insertLike, 1, userId);
        Sqlite.sqlite3_bind_int64(_insertLike, 2, postId);
        rc = Sqlite.sqlite3_step(_insertLike);
        Sqlite.sqlite3_reset(_insertLike);
        if (rc != Sqlite.DONE) return -1;
        return Sqlite.sqlite3_changes(_db) > 0 ? 1 : 2;
    }

    /// <summary>
    /// Checkpoints on their own connection and thread, so their fsyncs never block the event loop.
    /// PASSIVE every 50 ms keeps the backlog small. Once the WAL holds more than RestartFrames frames, a RESTART
    /// checkpoint makes the next writer start the WAL over from frame 1. Without restarts the WAL index hash
    /// chains of hot pages grow with every commit, and every page lookup walks them (walFindFrame was ~20% of CPU).
    /// </summary>
    public static void StartCheckpointer(string path)
    {
        const int RestartFrames = 2000;
        var t = new Thread(() =>
        {
            nint db = Open(path);
            Exec(db, "PRAGMA busy_timeout=200");
            Exec(db, "PRAGMA wal_autocheckpoint=0");
            // A connection opens the WAL lazily on its first read; until then checkpoints are silent no-ops.
            Exec(db, "SELECT count(*) FROM sqlite_schema");
            while (true)
            {
                Thread.Sleep(50);
                int log, ckpt;
                long t0 = Environment.TickCount64;
                int rc = Sqlite.sqlite3_wal_checkpoint_v2(db, null, Sqlite.CHECKPOINT_PASSIVE, &log, &ckpt);
                Log.Trace($"ckpt passive rc={rc} log={log} ckpt={ckpt} {Environment.TickCount64 - t0}ms");
                if (log > RestartFrames)
                {
                    t0 = Environment.TickCount64;
                    rc = Sqlite.sqlite3_wal_checkpoint_v2(db, null, Sqlite.CHECKPOINT_RESTART, &log, &ckpt);
                    Log.Trace($"ckpt RESTART rc={rc} log={log} ckpt={ckpt} {Environment.TickCount64 - t0}ms");
                }
            }
        }) { IsBackground = true, Name = "checkpoint" };
        t.Start();
    }
}
