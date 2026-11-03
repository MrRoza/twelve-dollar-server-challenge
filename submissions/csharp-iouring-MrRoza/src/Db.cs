// SQLite access: one connection for the event loop (reads + group-committed writes) and one for the
// checkpoint thread. Prepared statements are reused (they cache the plan, not data).
using System.Runtime.InteropServices;
using System.Text;

namespace Twelve;

unsafe sealed class Db
{
    readonly nint _db;
    readonly nint _begin, _commit, _rollback, _health, _feed, _post, _insertPost, _postExists, _insertLike;

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
        Exec(_db, "PRAGMA busy_timeout=5000");
        _begin = Prep("BEGIN");
        _commit = Prep("COMMIT");
        _rollback = Prep("ROLLBACK");
        _health = Prep("SELECT 1");
        _feed = Prep(PostCols + "ORDER BY p.created_at DESC, p.id DESC LIMIT 20");
        _post = Prep(PostCols + "WHERE p.id = ?");
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
    public bool InTransaction => Sqlite.sqlite3_get_autocommit(_db) == 0;
    public bool Commit() => Run(_commit);
    public void Rollback() => Run(_rollback);

    public string? Health()
    {
        int rc = Sqlite.sqlite3_step(_health);
        Sqlite.sqlite3_reset(_health);
        return rc == Sqlite.ROW ? null : Marshal.PtrToStringUTF8((nint)Sqlite.sqlite3_errmsg(_db)) ?? "error";
    }

    static void WritePost(nint st)
    {
        Out.Raw("{\"id\":"u8); Out.Int(Sqlite.sqlite3_column_int64(st, 0));
        Out.Raw(",\"body\":"u8);
        byte* b = Sqlite.sqlite3_column_text(st, 1); Out.Str(b, Sqlite.sqlite3_column_bytes(st, 1));
        Out.Raw(",\"created_at\":\""u8);
        byte* t = Sqlite.sqlite3_column_text(st, 2); Out.Raw(t, Sqlite.sqlite3_column_bytes(st, 2));
        Out.Raw("\",\"author\":"u8);
        byte* u = Sqlite.sqlite3_column_text(st, 3); Out.Str(u, Sqlite.sqlite3_column_bytes(st, 3));
        Out.Raw(",\"like_count\":"u8); Out.Int(Sqlite.sqlite3_column_int64(st, 4));
        Out.Raw("}"u8);
    }

    /// <summary>Writes the feed body. False on a database error.</summary>
    public bool Feed()
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

    /// <summary>Runs PASSIVE checkpoints on its own connection so WAL fsyncs never block the event loop.</summary>
    public static void StartCheckpointer(string path)
    {
        var t = new Thread(() =>
        {
            nint db = Open(path);
            Exec(db, "PRAGMA busy_timeout=5000");
            Exec(db, "PRAGMA wal_autocheckpoint=0");
            while (true)
            {
                Thread.Sleep(250);
                int log, ckpt;
                Sqlite.sqlite3_wal_checkpoint_v2(db, null, Sqlite.CHECKPOINT_PASSIVE, &log, &ckpt);
            }
        }) { IsBackground = true, Name = "checkpoint" };
        t.Start();
    }
}
