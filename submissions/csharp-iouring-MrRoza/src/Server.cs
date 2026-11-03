// The event loop: one io_uring, one thread. Each loop turn drains every completion, collects the
// connections that now hold a complete request, runs that whole batch inside ONE SQLite transaction
// (one snapshot for the reads, one commit for the writes = group commit), and only then queues the sends.
// So a 201 is always sent after its row is committed.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Twelve;

enum Route : byte { NotFound, Health, Feed, GetPost, CreatePost, Like }

[StructLayout(LayoutKind.Sequential)]
unsafe struct Conn
{
    public byte* In; public int InLen, InCap;
    public byte* Out; public int OutCap, OutStart, OutEnd;
    public int ReqLen;          // bytes of In used by the request being served
    public byte Open, CloseAfter, Wrote;
    public Route Route;
    public int SegOff, SegLen;  // the :id path segment
    public int AuthOff, AuthLen; // -1 = no Authorization header
    public int BodyOff, BodyLen;
}

unsafe sealed class Server
{
    const ulong OpAccept = 1, OpRecv = 2, OpSend = 3;
    const int MaxFd = 65536, InInitial = 4096, InMax = 65536;

    readonly Uring _ring;
    readonly Db _db;
    readonly int _listen;
    readonly Conn* _conns;
    readonly int* _ready;
    int _readyCount;
    readonly long _startTicks = Environment.TickCount64;
    long _now; // unix seconds, refreshed per batch
    readonly byte* _scratch, _name;

    public Server(int listenFd, Db db)
    {
        _listen = listenFd;
        _db = db;
        _ring = new Uring(4096, 65536);
        _conns = (Conn*)NativeMemory.AllocZeroed((nuint)MaxFd, (nuint)sizeof(Conn));
        _ready = (int*)NativeMemory.Alloc((nuint)MaxFd, sizeof(int));
        _scratch = (byte*)NativeMemory.Alloc(InMax);
        _name = (byte*)NativeMemory.Alloc(1024);
    }

    public void Run()
    {
        _ring.PrepAcceptMultishot(_listen, OpAccept);
        while (true)
        {
            _ring.Submit(1);
            while (_ring.TryPeek(out Cqe* cqe))
            {
                ulong ud = cqe->UserData;
                int res = cqe->Res;
                uint flags = cqe->Flags;
                _ring.Advance();
                int fd = (int)(ud >> 8);
                switch (ud & 0xFF)
                {
                    case OpAccept: OnAccept(res, flags); break;
                    case OpRecv: OnRecv(fd, res); break;
                    case OpSend: OnSend(fd, res); break;
                }
            }
            if (_readyCount > 0) RunBatch();
        }
    }

    void OnAccept(int res, uint flags)
    {
        if ((flags & Uring.CQE_F_MORE) == 0) _ring.PrepAcceptMultishot(_listen, OpAccept);
        if (res < 0) { Log.Trace("accept error " + res); return; }
        if (res >= MaxFd) { Libc.close(res); return; }
        Conn* c = &_conns[res];
        if (c->In == null) { c->InCap = InInitial; c->In = (byte*)NativeMemory.Alloc((nuint)c->InCap); }
        c->InLen = 0; c->ReqLen = 0; c->Open = 1; c->CloseAfter = 0;
        Log.Trace("accept fd " + res);
        PostRecv(res, c);
    }

    void PostRecv(int fd, Conn* c)
    {
        if (c->InLen == c->InCap)
        {
            if (c->InCap >= InMax) { CloseConn(fd, c); return; }
            c->InCap *= 2;
            c->In = (byte*)NativeMemory.Realloc(c->In, (nuint)c->InCap);
        }
        _ring.PrepRecv(fd, c->In + c->InLen, c->InCap - c->InLen, (ulong)fd << 8 | OpRecv);
    }

    void CloseConn(int fd, Conn* c)
    {
        Log.Trace("close fd " + fd);
        c->Open = 0; c->InLen = 0;
        // Shrink big buffers back so a few large requests don't pin memory forever.
        if (c->InCap > InInitial) { NativeMemory.Free(c->In); c->In = null; c->InCap = 0; }
        if (c->OutCap > 16384) { NativeMemory.Free(c->Out); c->Out = null; c->OutCap = 0; }
        Libc.close(fd);
    }

    void OnRecv(int fd, int res)
    {
        Conn* c = &_conns[fd];
        if (res <= 0) { CloseConn(fd, c); return; }
        c->InLen += res;
        Parse(fd, c);
    }

    /// <summary>Parses the buffered bytes: queues the connection for the batch, or receives more, or closes.</summary>
    void Parse(int fd, Conn* c)
    {
        int r = Http.TryParse(c);
        if (r > 0) _ready[_readyCount++] = fd;
        else if (r == 0) PostRecv(fd, c);
        else CloseConn(fd, c);
    }

    void OnSend(int fd, int res)
    {
        Conn* c = &_conns[fd];
        if (res <= 0) { CloseConn(fd, c); return; }
        c->OutStart += res;
        if (c->OutStart < c->OutEnd)
        {
            _ring.PrepSend(fd, c->Out + c->OutStart, c->OutEnd - c->OutStart, (ulong)fd << 8 | OpSend);
            return;
        }
        if (c->CloseAfter != 0) { CloseConn(fd, c); return; }
        // Drop the served request; anything after it is the next (pipelined) request.
        int rest = c->InLen - c->ReqLen;
        if (rest > 0) Buffer.MemoryCopy(c->In + c->ReqLen, c->In, c->InCap, rest);
        c->InLen = rest; c->ReqLen = 0;
        if (rest > 0) Parse(fd, c);
        else PostRecv(fd, c);
    }

    void RunBatch()
    {
        _now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool txn = _db.Begin();
        bool anyWrite = false;
        for (int i = 0; i < _readyCount; i++)
        {
            Conn* c = &_conns[_ready[i]];
            c->Wrote = 0;
            Handle(c);
            anyWrite |= c->Wrote != 0;
        }
        if (txn && !_db.Commit())
        {
            _db.Rollback();
            if (anyWrite)
                for (int i = 0; i < _readyCount; i++)
                {
                    Conn* c = &_conns[_ready[i]];
                    if (c->Wrote != 0) { Out.Begin(c); Out.Error(500, "internal server error"u8); }
                }
        }
        for (int i = 0; i < _readyCount; i++)
        {
            int fd = _ready[i];
            Conn* c = &_conns[fd];
            _ring.PrepSend(fd, c->Out + c->OutStart, c->OutEnd - c->OutStart, (ulong)fd << 8 | OpSend);
        }
        _readyCount = 0;
    }

    void Handle(Conn* c)
    {
        Out.Begin(c);
        switch (c->Route)
        {
            case Route.Health: Health(); break;
            case Route.Feed:
                if (_db.Feed()) Out.End(200);
                else { Out.Begin(c); Out.Error(500, "internal server error"u8); }
                break;
            case Route.GetPost: GetPost(c); break;
            case Route.CreatePost: CreatePost(c); break;
            case Route.Like: Like(c); break;
            default: Out.Error(404, "not found"u8); break;
        }
    }

    void Health()
    {
        string? err = _db.Health();
        if (err == null)
        {
            Out.Raw("{\"status\":\"ok\",\"db\":\"ok\",\"uptime_s\":"u8);
            Out.Int((Environment.TickCount64 - _startTicks) / 1000);
            Out.Raw("}"u8);
            Out.End(200);
        }
        else
        {
            byte[] e = System.Text.Encoding.UTF8.GetBytes(err);
            Out.Raw("{\"status\":\"degraded\",\"db\":\"unreachable\",\"error\":"u8);
            fixed (byte* ep = e) Out.Str(ep, e.Length);
            Out.Raw("}"u8);
            Out.End(503);
        }
    }

    /// <summary>Positive integer id, digits only, at most 2^53. -1 if invalid.</summary>
    static long ParseId(byte* s, int n)
    {
        if (n <= 0 || n > 16) return -1;
        long v = 0;
        for (int i = 0; i < n; i++)
        {
            uint d = (uint)(s[i] - '0');
            if (d > 9) return -1;
            v = v * 10 + d;
        }
        return v > 0 && v <= 1L << 53 ? v : -1;
    }

    void GetPost(Conn* c)
    {
        long id = ParseId(c->In + c->SegOff, c->SegLen);
        if (id < 0) { Out.Error(400, "invalid post id"u8); return; }
        int r = _db.Post(id);
        if (r > 0) Out.End(200);
        else { Out.Begin(c); if (r == 0) Out.Error(404, "post not found"u8); else Out.Error(500, "internal server error"u8); }
    }

    /// <summary>Checks the bearer token. Writes the 401 and returns false when it fails.</summary>
    bool Auth(Conn* c, out long userId, out int nameLen)
    {
        userId = 0; nameLen = 0;
        byte* a = c->In + c->AuthOff;
        if (c->AuthOff < 0 || c->AuthLen < 7 || !new ReadOnlySpan<byte>(a, 7).SequenceEqual("Bearer "u8))
        {
            Out.Error(401, "missing bearer token"u8);
            return false;
        }
        switch (Jwt.Verify(a + 7, c->AuthLen - 7, _now, out userId, _name, 1024, out nameLen))
        {
            case Jwt.Result.Ok: return true;
            case Jwt.Result.BadPayload: Out.Error(401, "invalid token payload"u8); return false;
            default: Out.Error(401, "invalid or expired token"u8); return false;
        }
    }

    void CreatePost(Conn* c)
    {
        if (!Auth(c, out long userId, out int nameLen)) return;
        var f = new JsonFields();
        if (!Json.ParseObject(c->In + c->BodyOff, c->BodyLen, ref f, _scratch, InMax))
        {
            Out.Error(400, "malformed JSON body"u8);
            return;
        }
        if (!f.TryGetString("body"u8, out byte* body, out int bodyLen)) { Out.Error(400, "body is required"u8); return; }
        Json.Trim(ref body, ref bodyLen);
        if (bodyLen == 0) { Out.Error(400, "body is required"u8); return; }
        if (Json.CodePoints(body, bodyLen) > 500) { Out.Error(400, "body must be at most 500 characters"u8); return; }
        c->Wrote = 1;
        if (_db.InsertPost(userId, body, bodyLen, _name, nameLen)) Out.End(201);
        else { Out.Begin(c); Out.Error(500, "internal server error"u8); }
    }

    void Like(Conn* c)
    {
        if (!Auth(c, out long userId, out _)) return;
        long id = ParseId(c->In + c->SegOff, c->SegLen);
        if (id < 0) { Out.Error(400, "invalid post id"u8); return; }
        c->Wrote = 1;
        int r = _db.Like(userId, id);
        switch (r)
        {
            case 0: Out.Error(404, "post not found"u8); break;
            case 1: Out.Raw("{\"liked\":true,\"already_liked\":false,\"post_id\":"u8); Out.Int(id); Out.Raw("}"u8); Out.End(201); break;
            case 2: Out.Raw("{\"liked\":true,\"already_liked\":true,\"post_id\":"u8); Out.Int(id); Out.Raw("}"u8); Out.End(200); break;
            default: Out.Error(500, "internal server error"u8); break;
        }
    }
}

static unsafe class Http
{
    /// <summary>1 = complete request parsed into <paramref name="c"/>, 0 = need more bytes, -1 = garbage.</summary>
    public static int TryParse(Conn* c)
    {
        byte* p = c->In;
        int n = c->InLen;
        var span = new ReadOnlySpan<byte>(p, n);
        int hdrEnd = span.IndexOf("\r\n\r\n"u8);
        if (hdrEnd < 0) return n >= 65536 ? -1 : 0;

        // Request line: METHOD SP target SP version
        int lineEnd = span.Slice(0, hdrEnd + 2).IndexOf("\r\n"u8);
        var line = span.Slice(0, lineEnd);
        int sp1 = line.IndexOf((byte)' ');
        if (sp1 <= 0) return -1;
        int sp2rel = line.Slice(sp1 + 1).IndexOf((byte)' ');
        if (sp2rel <= 0) return -1;
        int pathOff = sp1 + 1, pathLen = sp2rel;
        int q = line.Slice(pathOff, pathLen).IndexOf((byte)'?');
        if (q >= 0) pathLen = q;
        bool isGet = sp1 == 3 && line[0] == 'G' && line[1] == 'E' && line[2] == 'T';
        bool isPost = sp1 == 4 && line[0] == 'P' && line[1] == 'O' && line[2] == 'S' && line[3] == 'T';
        bool http10 = line.Slice(pathOff + sp2rel + 1).SequenceEqual("HTTP/1.0"u8);

        // Headers
        long contentLength = 0;
        c->AuthOff = -1; c->AuthLen = 0;
        c->CloseAfter = (byte)(http10 ? 1 : 0);
        int pos = lineEnd + 2;
        while (pos < hdrEnd + 2)
        {
            int eol = span.Slice(pos).IndexOf("\r\n"u8) + pos;
            var h = span.Slice(pos, eol - pos);
            int colon = h.IndexOf((byte)':');
            if (colon > 0)
            {
                var name = h.Slice(0, colon);
                int vs = colon + 1;
                while (vs < h.Length && (h[vs] == ' ' || h[vs] == '\t')) vs++;
                int ve = h.Length;
                while (ve > vs && (h[ve - 1] == ' ' || h[ve - 1] == '\t')) ve--;
                var val = h.Slice(vs, ve - vs);
                if (name.Length == 14 && Ascii.EqualsIgnoreCase(name, "content-length"u8))
                {
                    contentLength = 0;
                    foreach (byte b in val)
                    {
                        uint d = (uint)(b - '0');
                        if (d > 9 || contentLength > 1 << 20) return -1;
                        contentLength = contentLength * 10 + d;
                    }
                }
                else if (name.Length == 13 && Ascii.EqualsIgnoreCase(name, "authorization"u8))
                {
                    c->AuthOff = pos + vs; c->AuthLen = ve - vs;
                }
                else if (name.Length == 10 && Ascii.EqualsIgnoreCase(name, "connection"u8))
                {
                    if (Ascii.EqualsIgnoreCase(val, "close"u8)) c->CloseAfter = 1;
                    else if (Ascii.EqualsIgnoreCase(val, "keep-alive"u8)) c->CloseAfter = 0;
                }
            }
            pos = eol + 2;
        }

        int bodyOff = hdrEnd + 4;
        if (contentLength > 65536 - bodyOff) return -1;
        if (n < bodyOff + contentLength) return 0;
        c->BodyOff = bodyOff; c->BodyLen = (int)contentLength;
        c->ReqLen = bodyOff + (int)contentLength;

        // Route
        var path = span.Slice(pathOff, pathLen);
        c->Route = Route.NotFound;
        if (isGet)
        {
            if (path.SequenceEqual("/feed"u8)) c->Route = Route.Feed;
            else if (path.SequenceEqual("/health"u8)) c->Route = Route.Health;
            else if (path.StartsWith("/posts/"u8))
            {
                var seg = path.Slice(7);
                if (seg.Length > 0 && seg.IndexOf((byte)'/') < 0) { c->Route = Route.GetPost; c->SegOff = pathOff + 7; c->SegLen = seg.Length; }
            }
        }
        else if (isPost)
        {
            if (path.SequenceEqual("/posts"u8)) c->Route = Route.CreatePost;
            else if (path.StartsWith("/posts/"u8) && path.EndsWith("/like"u8) && path.Length > 12)
            {
                int segLen = path.Length - 12;
                var seg = path.Slice(7, segLen);
                if (seg.IndexOf((byte)'/') < 0) { c->Route = Route.Like; c->SegOff = pathOff + 7; c->SegLen = segLen; }
            }
        }
        return 1;
    }
}
