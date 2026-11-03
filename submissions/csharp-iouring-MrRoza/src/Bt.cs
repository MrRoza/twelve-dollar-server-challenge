// Reads that go through SQLite's B-tree cursors instead of its SQL engine (no bytecode, no sqlite3_column_*).
// native/shim.c compiles the unmodified amalgamation with thin wrappers around the cursor API; this file decodes
// SQLite's record format and writes the JSON. Cursors live only inside a batch's read transaction, so every
// byte is read live from SQLite (rule 5): only cursors, KeyInfos and root page numbers are reused.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Twelve;

static unsafe class BtNative
{
    [DllImport("sqlite3")] public static extern int tw_cursor_size();
    [DllImport("sqlite3")] public static extern nint tw_btree(nint db);
    [DllImport("sqlite3")] public static extern nint tw_keyinfo(nint db, int nKey, int nExtra, uint descMask);
    [DllImport("sqlite3")] public static extern int tw_open(nint bt, uint root, nint keyInfo, nint cur);
    [DllImport("sqlite3")] public static extern int tw_close(nint cur);
    [DllImport("sqlite3")] public static extern int tw_first(nint cur, int* empty);
    [DllImport("sqlite3")] public static extern int tw_next(nint cur);
    [DllImport("sqlite3")] public static extern int tw_seek_rowid(nint cur, long rowid, int* res);
    [DllImport("sqlite3")] public static extern int tw_seek_ge_int(nint cur, nint keyInfo, long v, int* eof);
    [DllImport("sqlite3")] public static extern long tw_rowid(nint cur);
    [DllImport("sqlite3")] public static extern byte* tw_payload(nint cur, uint* local, uint* total);
    [DllImport("sqlite3")] public static extern int tw_payload_copy(nint cur, uint offset, uint amt, byte* buf);
}

/// <summary>SQLite record format: a header of serial types, then the column values.</summary>
unsafe ref struct Record
{
    public const int MaxCols = 4;
    byte* _p;
    fixed int _type[MaxCols];
    fixed int _off[MaxCols];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Varint(byte* p, out ulong v)
    {
        if (p[0] < 0x80) { v = p[0]; return 1; }
        ulong x = 0;
        for (int i = 0; i < 8; i++)
        {
            x = x << 7 | (uint)(p[i] & 0x7F);
            if (p[i] < 0x80) { v = x; return i + 1; }
        }
        v = x << 8 | p[8];
        return 9;
    }

    static int SerialLen(int t) => t switch
    {
        <= 4 => t, // 0..4 -> 0,1,2,3,4 bytes
        5 => 6,
        6 or 7 => 8,
        8 or 9 => 0,
        _ => (t - 12) >> 1,
    };

    /// <summary>Parses the header for the first <paramref name="cols"/> columns. False if malformed.</summary>
    public bool Parse(byte* p, int n, int cols)
    {
        _p = p;
        int pos = Varint(p, out ulong hl);
        if (hl > (ulong)n) return false;
        int hdrLen = (int)hl, data = hdrLen;
        for (int c = 0; c < cols; c++)
        {
            int t = 0; // a column past the header end reads as NULL
            if (pos < hdrLen) { pos += Varint(p + pos, out ulong st); t = (int)st; }
            _type[c] = t; _off[c] = data;
            data += SerialLen(t);
        }
        return data <= n;
    }

    public bool IsInt(int c) => _type[c] is >= 1 and <= 6 or 8 or 9;
    public bool IsText(int c) => _type[c] >= 13 && (_type[c] & 1) == 1;

    public long Int(int c)
    {
        byte* q = _p + _off[c];
        switch (_type[c])
        {
            case 1: return (sbyte)q[0];
            case 2: return (short)(q[0] << 8 | q[1]);
            case 3: return (q[0] << 16 | q[1] << 8 | q[2]) << 8 >> 8;
            case 4: return (int)((uint)q[0] << 24 | (uint)q[1] << 16 | (uint)q[2] << 8 | q[3]);
            case 5: return ((long)(short)(q[0] << 8 | q[1]) << 32) | ((uint)q[2] << 24 | (uint)q[3] << 16 | (uint)q[4] << 8 | q[5]);
            case 6: return (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(new ReadOnlySpan<byte>(q, 8));
            case 9: return 1;
            default: return 0;
        }
    }

    public byte* Text(int c, out int len) { len = (_type[c] - 13) >> 1; return _p + _off[c]; }
}

unsafe sealed class BtReader
{
    readonly nint _bt, _kiIdx, _kiLikes;
    readonly uint _rootPosts, _rootUsers, _rootLikesByPost, _rootPostsByTime;
    readonly nint _cPosts, _cUsers, _cLikes, _cIdx; // cursor memory
    bool _open;
    readonly byte* _payload;                        // a record that spills onto overflow pages is copied here
    const int PayloadCap = 1 << 16;
    readonly byte* _sc;                             // feed scratch: post fragments and author names
    const int ScratchCap = 1 << 18;

    public BtReader(nint db, uint rootPosts, uint rootUsers, uint rootLikesByPost, uint rootPostsByTime)
    {
        _bt = BtNative.tw_btree(db);
        _rootPosts = rootPosts; _rootUsers = rootUsers; _rootLikesByPost = rootLikesByPost; _rootPostsByTime = rootPostsByTime;
        _kiIdx = BtNative.tw_keyinfo(db, 2, 1, 0b011);  // posts_created_at_id_idx (created_at DESC, id DESC) + rowid
        _kiLikes = BtNative.tw_keyinfo(db, 1, 1, 0);     // likes_post_id_idx (post_id) + rowid
        int cs = BtNative.tw_cursor_size();
        _cPosts = (nint)NativeMemory.AllocZeroed((nuint)cs);
        _cUsers = (nint)NativeMemory.AllocZeroed((nuint)cs);
        _cLikes = (nint)NativeMemory.AllocZeroed((nuint)cs);
        _cIdx = (nint)NativeMemory.AllocZeroed((nuint)cs);
        _payload = (byte*)NativeMemory.Alloc(PayloadCap);
        _sc = (byte*)NativeMemory.Alloc(ScratchCap);
    }

    /// <summary>Opens the cursors. The read transaction must already be open.</summary>
    bool Open()
    {
        if (_open) return true;
        if (BtNative.tw_open(_bt, _rootPosts, 0, _cPosts) != 0) return false;
        if (BtNative.tw_open(_bt, _rootUsers, 0, _cUsers) != 0) { BtNative.tw_close(_cPosts); return false; }
        if (BtNative.tw_open(_bt, _rootLikesByPost, _kiLikes, _cLikes) != 0) { BtNative.tw_close(_cPosts); BtNative.tw_close(_cUsers); return false; }
        if (BtNative.tw_open(_bt, _rootPostsByTime, _kiIdx, _cIdx) != 0) { BtNative.tw_close(_cPosts); BtNative.tw_close(_cUsers); BtNative.tw_close(_cLikes); return false; }
        _open = true;
        return true;
    }

    /// <summary>Must run before the read transaction ends.</summary>
    public void Close()
    {
        if (!_open) return;
        BtNative.tw_close(_cPosts); BtNative.tw_close(_cUsers); BtNative.tw_close(_cLikes); BtNative.tw_close(_cIdx);
        _open = false;
    }

    /// <summary>The current entry's record bytes (copied if it spills onto overflow pages).</summary>
    byte* Payload(nint cur, out int n)
    {
        uint local, total;
        byte* p = BtNative.tw_payload(cur, &local, &total);
        n = (int)total;
        if (local >= total) return p;
        if (total > PayloadCap || BtNative.tw_payload_copy(cur, 0, total, _payload) != 0) { n = -1; return null; }
        return _payload;
    }

    /// <summary>Likes of one post: entries of likes_post_id_idx with this post_id. -1 on error.</summary>
    long CountLikes(long postId)
    {
        int eof;
        if (BtNative.tw_seek_ge_int(_cLikes, _kiLikes, postId, &eof) != 0) return -1;
        long count = 0;
        var r = new Record();
        while (eof == 0)
        {
            byte* p = Payload(_cLikes, out int n);
            if (p == null || !r.Parse(p, n, 1) || !r.IsInt(0)) return -1;
            if (r.Int(0) != postId) break;
            count++;
            int rc = BtNative.tw_next(_cLikes);
            if (rc == 101 /*DONE*/) break;
            if (rc != 0) return -1;
        }
        return count;
    }

    /// <summary>Username of a user, unescaped. False if missing or not text.</summary>
    bool User(long userId, out byte* name, out int nameLen)
    {
        name = null; nameLen = 0;
        int res;
        if (BtNative.tw_seek_rowid(_cUsers, userId, &res) != 0 || res != 0) return false;
        byte* p = Payload(_cUsers, out int n);
        var r = new Record();
        if (p == null || !r.Parse(p, n, 2) || !r.IsText(1)) return false;
        name = r.Text(1, out nameLen);
        return true;
    }

    /// <summary>
    /// GET /posts/:id body. 1 = written, 0 = no such post, -1 = take the SQL path (error or unexpected shape).
    /// </summary>
    public int Post(long id)
    {
        if (!Open()) return -1;
        int res;
        if (BtNative.tw_seek_rowid(_cPosts, id, &res) != 0) return -1;
        if (res != 0) return 0;
        byte* p = Payload(_cPosts, out int n);
        var r = new Record();
        if (p == null || !r.Parse(p, n, 4) || !r.IsInt(1) || !r.IsText(2) || !r.IsText(3)) return -1;
        long userId = r.Int(1);
        byte* body = r.Text(2, out int bodyLen);
        byte* created = r.Text(3, out int createdLen);
        // The body may sit in the overflow copy buffer, and the user lookup below doesn't touch it, but the likes
        // walk can (a huge index record): write the post's own columns out first.
        Out.Raw("{\"post\":{\"id\":"u8); Out.Int(id);
        Out.Raw(",\"body\":"u8); Out.Str(body, bodyLen);
        Out.Raw(",\"created_at\":\""u8); Out.Raw(created, createdLen);
        if (!User(userId, out byte* name, out int nameLen)) return -1; // JOIN would drop the row: let SQL answer
        Out.Raw("\",\"author\":"u8); Out.Str(name, nameLen);
        long likes = CountLikes(id);
        if (likes < 0) return -1;
        Out.Raw(",\"like_count\":"u8); Out.Int(likes);
        Out.Raw("}}"u8);
        return 1;
    }

    /// <summary>
    /// GET /feed body. Walks posts_created_at_id_idx from its first entry (it is (created_at DESC, id DESC), so
    /// this is exactly ORDER BY created_at DESC, id DESC); looks the 20 posts up in ascending id order (neighbours
    /// in the same leaf); then their authors; then their likes with one range walk of likes_post_id_idx when the
    /// ids are close together, else one seek per post. True = written; false = take the SQL path.
    /// </summary>
    public bool Feed()
    {
        if (!Open()) return false;
        long* ids = stackalloc long[20];
        int n = 0, empty, rc;
        var r = new Record();
        if (BtNative.tw_first(_cIdx, &empty) != 0) return false;
        while (empty == 0 && n < 20)
        {
            byte* p = Payload(_cIdx, out int len);
            if (p == null || !r.Parse(p, len, 2) || !r.IsInt(1)) return false;
            ids[n++] = r.Int(1);
            rc = BtNative.tw_next(_cIdx);
            if (rc == 101) break;
            if (rc != 0) return false;
        }

        // Ascending id order for the lookups.
        int* order = stackalloc int[20];
        for (int i = 0; i < n; i++) order[i] = i;
        for (int i = 1; i < n; i++)
        {
            int k = order[i]; long v = ids[k]; int j = i - 1;
            while (j >= 0 && ids[order[j]] > v) { order[j + 1] = order[j]; j--; }
            order[j + 1] = k;
        }

        // Post columns: {"id":..,"body":..,"created_at":".." as a fragment in scratch; then the author's name.
        int* fragOff = stackalloc int[20];
        int* fragLen = stackalloc int[20];
        int* nameOff = stackalloc int[20];
        int* nameLen = stackalloc int[20];
        long* userIds = stackalloc long[20];
        byte* o = _sc, end = _sc + ScratchCap;
        for (int oi = 0; oi < n; oi++)
        {
            int i = order[oi];
            int res;
            if (BtNative.tw_seek_rowid(_cPosts, ids[i], &res) != 0 || res != 0) return false;
            byte* p = Payload(_cPosts, out int len);
            if (p == null || !r.Parse(p, len, 4) || !r.IsInt(1) || !r.IsText(2) || !r.IsText(3)) return false;
            userIds[i] = r.Int(1);
            byte* body = r.Text(2, out int bodyLen);
            byte* created = r.Text(3, out int createdLen);
            if (end - o < bodyLen * 6 + createdLen + 80) return false;
            byte* start = o;
            "{\"id\":"u8.CopyTo(new Span<byte>(o, 6)); o += 6;
            o = Out.IntTo(o, ids[i]);
            ",\"body\":"u8.CopyTo(new Span<byte>(o, 8)); o += 8;
            o = Out.Escape(o, body, bodyLen);
            ",\"created_at\":\""u8.CopyTo(new Span<byte>(o, 15)); o += 15;
            Buffer.MemoryCopy(created, o, createdLen, createdLen); o += createdLen;
            *o++ = (byte)'"';
            fragOff[i] = (int)(start - _sc); fragLen[i] = (int)(o - start);
        }
        for (int i = 0; i < n; i++)
        {
            if (!User(userIds[i], out byte* name, out int nl)) return false;
            if (end - o < nl * 6 + 2) return false;
            byte* start = o;
            o = Out.Escape(o, name, nl);
            nameOff[i] = (int)(start - _sc); nameLen[i] = (int)(o - start);
        }

        long* likes = stackalloc long[20];
        new Span<long>(likes, 20).Clear();
        if (n > 0)
        {
            long min = ids[order[0]], max = ids[order[n - 1]];
            if (max - min < 512)
            {
                int range = (int)(max - min + 1);
                sbyte* slot = stackalloc sbyte[range];
                new Span<byte>(slot, range).Fill(0xFF);
                for (int i = 0; i < n; i++) slot[ids[i] - min] = (sbyte)i;
                int eof;
                if (BtNative.tw_seek_ge_int(_cLikes, _kiLikes, min, &eof) != 0) return false;
                while (eof == 0)
                {
                    byte* p = Payload(_cLikes, out int len);
                    if (p == null || !r.Parse(p, len, 1) || !r.IsInt(0)) return false;
                    long pid = r.Int(0);
                    if (pid > max) break;
                    int s = slot[pid - min];
                    if (s >= 0) likes[s]++;
                    rc = BtNative.tw_next(_cLikes);
                    if (rc == 101) break;
                    if (rc != 0) return false;
                }
            }
            else
                for (int oi = 0; oi < n; oi++)
                {
                    int i = order[oi];
                    if ((likes[i] = CountLikes(ids[i])) < 0) return false;
                }
        }

        Out.Raw("{\"posts\":["u8);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) Out.Raw(","u8);
            Out.Raw(_sc + fragOff[i], fragLen[i]);
            Out.Raw(",\"author\":"u8);
            Out.Raw(_sc + nameOff[i], nameLen[i]);
            Out.Raw(",\"like_count\":"u8);
            Out.Int(likes[i]);
            Out.Raw("}"u8);
        }
        Out.Raw("]}"u8);
        return true;
    }
}
