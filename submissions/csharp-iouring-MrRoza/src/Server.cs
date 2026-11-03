// The event loop: one thread, up to 16 io_uring rings (each holds up to ~65k direct descriptors, so
// LimitNOFILE=65535 doesn't cap connections). Each loop turn ("batch") drains every ring's completions,
// collects the connections that now hold a complete request, runs the writes in one BEGIN IMMEDIATE
// transaction (group commit) and the reads in one read transaction, and only then queues the sends.
// So a 201 is always sent after its row is committed.
//
// Idle connections hold no buffer: each one has a multishot POLLIN armed. When it fires, a recv with
// MSG_DONTWAIT reads into a pooled buffer, which goes back as soon as the response is sent.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Twelve;

enum Route : byte { NotFound, Health, Feed, GetPost, CreatePost, Like }
enum ConnState : byte { Free, Idle, Reading, Queued, Sending, Closing }

[StructLayout(LayoutKind.Sequential)]
unsafe struct Conn
{
    public byte* In; public int InLen, InCap;
    public byte* Out; public int OutCap, OutStart, OutEnd;
    public int ReqLen;           // bytes of In used by the request being served
    public uint Gen;             // bumped per accept: stale completions of an old connection are ignored
    public ConnState State;
    public byte Readable;        // the poll fired while we were busy with this connection
    public byte CloseAfter;
    public Route Route;
    public int SegOff, SegLen;   // the :id path segment
    public int AuthOff, AuthLen; // AuthOff -1 = no Authorization header
    public int BodyOff, BodyLen;
}

sealed unsafe class Ring
{
    public Uring U = null!;
    public int Index, Free, Accepts;
}

unsafe sealed partial class Server
{
    const ulong OpAccept = 1, OpPoll = 2, OpRecv = 3, OpSend = 4, OpClose = 5, OpTimeout = 6;
    const int MaxRings = 16, MaxAccepts = 64;
    const int InPoolSize = 4096, OutPoolSize = Out.PoolSize, InMax = 65536;

    readonly int _slots;              // direct-descriptor slots per ring
    readonly Ring[] _rings = new Ring[MaxRings];
    int _ringCount, _acceptRing;
    readonly Db _db;
    readonly int _listen;
    readonly Conn* _conns;            // [ring * _slots + slot]
    readonly int* _ready, _writes;
    int _readyCount, _writeCount;
    readonly long* _timeout;          // __kernel_timespec for the write-retry timeout
    bool _timeoutArmed;
    long _writeOldest;                // Stopwatch timestamp of the oldest queued write
    readonly long _startTicks = Environment.TickCount64;
    long _now;                        // unix seconds, refreshed per batch
    readonly byte* _scratch, _name;
    readonly BufPool _inPool = new(InPoolSize), _outPool = new(OutPoolSize);

    // Dev tunables (not part of the runtime contract; start.sh never sets them).
    static readonly uint BatchMin = uint.Parse(Environment.GetEnvironmentVariable("TWELVE_BATCH_MIN") ?? "128");
    static readonly long BatchWaitNs = long.Parse(Environment.GetEnvironmentVariable("TWELVE_BATCH_WAIT_US") ?? "2000") * 1000;
    // The write stage: queued writes commit together once WriteMin are queued or the oldest waited WriteWaitUs.
    // Every commit rewrites the same hot pages (newest posts leaf, index leaves) into the WAL, so fewer,
    // bigger commits write far fewer frames per row.
    static readonly int WriteMin = int.Parse(Environment.GetEnvironmentVariable("TWELVE_WRITE_MIN") ?? "64");
    static readonly long WriteWaitTicks = long.Parse(Environment.GetEnvironmentVariable("TWELVE_WRITE_WAIT_US") ?? "5000") * Stopwatch.Frequency / 1_000_000;
    static readonly int SlotsOverride = int.Parse(Environment.GetEnvironmentVariable("TWELVE_RING_SLOTS") ?? "0");

    public Server(int listenFd, Db db)
    {
        _listen = listenFd;
        _db = db;
        // A direct-descriptor table can't be larger than RLIMIT_NOFILE.
        ulong* rl = stackalloc ulong[2];
        Libc.getrlimit(7 /*RLIMIT_NOFILE*/, rl);
        _slots = (int)Math.Min(65535, Math.Max(256, (long)rl[0] - 64));
        if (SlotsOverride > 0) _slots = SlotsOverride;
        _conns = (Conn*)NativeMemory.AllocZeroed((nuint)(MaxRings * _slots), (nuint)sizeof(Conn));
        _ready = (int*)NativeMemory.Alloc((nuint)(MaxRings * _slots), sizeof(int));
        _writes = (int*)NativeMemory.Alloc((nuint)(MaxRings * _slots), sizeof(int));
        _timeout = (long*)NativeMemory.Alloc(16);
        _timeout[0] = 0; _timeout[1] = 1_000_000; // 1 ms
        _scratch = (byte*)NativeMemory.Alloc(InMax);
        _name = (byte*)NativeMemory.Alloc(1024);
        Out.Pool = _outPool;
        _db.SelfCheck(&_conns[0]);
        ReleaseOut(&_conns[0]);
        AddRing();
        Log.Info($"{_slots} connections per ring, up to {MaxRings} rings");
    }

    void AddRing()
    {
        var r = new Ring { Index = _ringCount, Free = _slots };
        r.U = new Uring(4096, 65536);
        r.U.RegisterSparseFiles(_slots);
        _rings[_ringCount++] = r;
        Log.Info($"ring {r.Index} up ({_ringCount * _slots} connection slots)");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Ud(ulong op, int idx, uint gen) => (ulong)gen << 32 | (ulong)(uint)idx << 8 | op;

    /// <summary>
    /// Keeps up to 64 one-shot direct accepts armed on the accepting ring, never more than it has free slots
    /// (so a burst can't overrun the table). Moves on to another ring, or opens one, only when it is full.
    /// </summary>
    void TopUpAccepts()
    {
        Ring r = _rings[_acceptRing];
        if (r.Free == 0)
        {
            int next = -1;
            for (int i = 0; i < _ringCount; i++) if (_rings[i].Free > 0) { next = i; break; }
            if (next < 0)
            {
                if (_ringCount == MaxRings) return; // completely full: the kernel's backlog holds the rest
                AddRing();
                next = _ringCount - 1;
            }
            _acceptRing = next;
            r = _rings[next];
        }
        while (r.Accepts < MaxAccepts && r.Free - r.Accepts > 0)
        {
            r.U.PrepAcceptDirect(_listen, Ud(OpAccept, r.Index, 0));
            r.Accepts++;
        }
    }

    public void Run()
    {
#if TRACE_LOG
        long statT = Environment.TickCount64, enters = 0, cqes = 0, batches = 0, reqs = 0, writeBatches = 0;
#endif
        while (true)
        {
            TopUpAccepts();
            if (_ringCount == 1)
            {
                // Batch window: wait until BatchMin completions are ready or BatchWaitNs passed, so one turn
                // (one enter, one SQLite snapshot, one commit) serves many requests.
                if (BatchMin <= 1) _rings[0].U.Enter(1, 0);
                else _rings[0].U.Enter(BatchMin, BatchWaitNs);
            }
            else
            {
                // Several rings. A DEFER_TASKRUN ring only posts completions inside its own io_uring_enter, and its
                // eventfd is no reliable wakeup (hundreds of missed signals per second measured on Linux 6.18), so
                // this loop polls: enter every ring (submit + run its task work); if fewer than BatchMin completions
                // are ready and the batch window isn't over, sleep a short step and look again.
                long deadline = Stopwatch.GetTimestamp() + BatchWaitNs * Stopwatch.Frequency / 1_000_000_000;
                while (true)
                {
                    uint ready = 0;
                    for (int i = 0; i < _ringCount; i++) { Uring u = _rings[i].U; u.Enter(0, 0); ready += u.ReadyCount; }
                    if (ready >= BatchMin || (ready > 0 && Stopwatch.GetTimestamp() >= deadline)) break;
                    Libc.usleep(ready == 0 ? 500u : 200u);
                }
            }
#if TRACE_LOG
            enters++;
            long now = Environment.TickCount64;
            if (now - statT >= 1000)
            {
                Log.Trace($"stats: {enters} waits, {cqes} cqes ({(double)cqes / Math.Max(1, enters):F1}/wait), {batches} batches, " +
                          $"{reqs} reqs ({(double)reqs / Math.Max(1, batches):F1}/batch), {writeBatches} write commits, {_ringCount} rings");
                statT = now; enters = cqes = batches = reqs = writeBatches = 0;
            }
#endif
            for (int ri = 0; ri < _ringCount; ri++)
            {
                Ring ring = _rings[ri];
                while (ring.U.TryPeek(out Cqe* cqe))
                {
#if TRACE_LOG
                    cqes++;
#endif
                    ulong ud = cqe->UserData;
                    int res = cqe->Res;
                    uint flags = cqe->Flags;
                    ring.U.Advance();
                    int idx = (int)((uint)ud >> 8);
                    uint gen = (uint)(ud >> 32);
                    switch (ud & 0xFF)
                    {
                        case OpAccept: OnAccept(ring, res); break;
                        case OpPoll: OnPoll(idx, gen, res, flags); break;
                        case OpRecv: OnRecv(idx, res); break;
                        case OpSend: OnSend(idx, res); break;
                        case OpClose: OnClosed(idx); break;
                        case OpTimeout: _timeoutArmed = false; break;
                    }
                }
            }
            if (_readyCount > 0 || _writeCount > 0)
            {
#if TRACE_LOG
                batches++; reqs += _readyCount + _writeCount; if (_writeCount > 0) writeBatches++;
#endif
                RunBatch();
            }
        }
    }

    void OnAccept(Ring r, int res)
    {
        r.Accepts--;
        if (res < 0) { Log.Trace("accept error " + res); return; }
        int idx = r.Index * _slots + res;
        Conn* c = &_conns[idx];
        r.Free--;
        c->Gen++;
        c->State = ConnState.Idle; c->Readable = 0; c->InLen = 0; c->ReqLen = 0; c->CloseAfter = 0;
        r.U.PrepPollMulti(res, Ud(OpPoll, idx, c->Gen));
    }

    Uring RingOf(int idx) => _rings[idx / _slots].U;

    void OnPoll(int idx, uint gen, int res, uint flags)
    {
        Conn* c = &_conns[idx];
        if (c->Gen != gen || c->State is ConnState.Free or ConnState.Closing) return; // the old connection's last word
        if (res < 0) { if ((flags & Uring.CQE_F_MORE) == 0 && c->State == ConnState.Idle) Close(idx, c); return; }
        if ((flags & Uring.CQE_F_MORE) == 0) RingOf(idx).PrepPollMulti(idx % _slots, Ud(OpPoll, idx, c->Gen)); // re-arm
        if (c->State == ConnState.Idle) StartRecv(idx, c);
        else c->Readable = 1;
    }

    void StartRecv(int idx, Conn* c)
    {
        if (c->In == null) { c->In = _inPool.Take(); c->InCap = InPoolSize; }
        if (c->InLen == c->InCap)
        {
            if (c->InCap >= InMax) { Close(idx, c); return; }
            int cap = c->InCap * 2;
            byte* nb = (byte*)NativeMemory.Alloc((nuint)cap);
            Buffer.MemoryCopy(c->In, nb, cap, c->InLen);
            ReleaseIn(c);
            c->In = nb; c->InCap = cap;
        }
        c->State = ConnState.Reading;
        c->Readable = 0;
        RingOf(idx).PrepRecv(idx % _slots, c->In + c->InLen, c->InCap - c->InLen, Ud(OpRecv, idx, c->Gen));
    }

    void ReleaseIn(Conn* c)
    {
        if (c->In == null) return;
        if (c->InCap == InPoolSize) _inPool.Return(c->In); else NativeMemory.Free(c->In);
        c->In = null; c->InCap = 0;
    }

    void ReleaseOut(Conn* c)
    {
        if (c->Out == null) return;
        if (c->OutCap == OutPoolSize) _outPool.Return(c->Out); else NativeMemory.Free(c->Out);
        c->Out = null; c->OutCap = 0;
    }

    void OnRecv(int idx, int res)
    {
        Conn* c = &_conns[idx];
        if (c->State != ConnState.Reading) return;
        if (res == -11 /*EAGAIN: nothing there after all*/)
        {
            c->State = ConnState.Idle;
            if (c->InLen == 0) ReleaseIn(c);
            if (c->Readable != 0) StartRecv(idx, c);
            return;
        }
        if (res <= 0) { Close(idx, c); return; }
        c->InLen += res;
        Parse(idx, c);
    }

    /// <summary>Queues the connection for the batch if a full request is buffered, else reads more, or closes.</summary>
    void Parse(int idx, Conn* c)
    {
        int r = Http.TryParse(c);
        if (r > 0)
        {
            c->State = ConnState.Queued;
            if (c->Route is Route.CreatePost or Route.Like)
            {
                if (_writeCount == 0) _writeOldest = Stopwatch.GetTimestamp();
                _writes[_writeCount++] = idx;
            }
            else _ready[_readyCount++] = idx;
        }
        else if (r == 0) StartRecv(idx, c); // the rest may already be there; if not, EAGAIN and the poll tells us
        else Close(idx, c);
    }

    void OnSend(int idx, int res)
    {
        Conn* c = &_conns[idx];
        if (c->State != ConnState.Sending) return;
        if (res <= 0) { Close(idx, c); return; }
        c->OutStart += res;
        if (c->OutStart < c->OutEnd)
        {
            RingOf(idx).PrepSend(idx % _slots, c->Out + c->OutStart, c->OutEnd - c->OutStart, Ud(OpSend, idx, c->Gen));
            return;
        }
        ReleaseOut(c);
        if (c->CloseAfter != 0) { Close(idx, c); return; }
        // Drop the served request; anything after it is the next (pipelined) request.
        int rest = c->InLen - c->ReqLen;
        if (rest > 0)
        {
            Buffer.MemoryCopy(c->In + c->ReqLen, c->In, c->InCap, rest);
            c->InLen = rest; c->ReqLen = 0;
            Parse(idx, c);
            return;
        }
        c->InLen = 0; c->ReqLen = 0;
        ReleaseIn(c);
        c->State = ConnState.Idle;
        if (c->Readable != 0) StartRecv(idx, c);
    }

    /// <summary>Only called when no recv or send is in flight for the connection (the poll may be).</summary>
    void Close(int idx, Conn* c)
    {
        c->State = ConnState.Closing;
        ReleaseIn(c); ReleaseOut(c);
        c->InLen = 0;
        RingOf(idx).PrepShutdownClose(idx % _slots, Ud(OpClose, idx, c->Gen));
    }

    void OnClosed(int idx)
    {
        Conn* c = &_conns[idx];
        if (c->State != ConnState.Closing) return;
        c->State = ConnState.Free;
        _rings[idx / _slots].Free++;
    }

    void RunBatch()
    {
        _now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // Writes first, in their own BEGIN IMMEDIATE transaction (group commit). If the checkpointer holds the
        // write lock right now (RESTART checkpoint), BEGIN fails at once (busy_timeout=0) and the writes wait
        // for the next loop turn; a 1 ms timeout makes sure that turn comes. The event loop never blocks.
        if (_writeCount > 0 && (_writeCount >= WriteMin || Stopwatch.GetTimestamp() - _writeOldest >= WriteWaitTicks))
        {
            if (_db.BeginImmediate())
            {
                for (int i = 0; i < _writeCount; i++) Handle(&_conns[_writes[i]]);
                if (!_db.Commit())
                {
                    _db.Rollback();
                    for (int i = 0; i < _writeCount; i++) { Conn* c = &_conns[_writes[i]]; Out.Begin(c); Out.Error(500, "internal server error"u8); }
                }
                for (int i = 0; i < _writeCount; i++) QueueSend(_writes[i]);
                _writeCount = 0;
            }
            else if (!_timeoutArmed)
            {
                Log.Trace("write deferred");
                _rings[0].U.PrepTimeout(_timeout, Ud(OpTimeout, 0, 0));
                _timeoutArmed = true;
            }
        }
        // Reads: one snapshot for the whole batch (they see the writes committed just above).
        if (_readyCount > 0)
        {
            bool txn = _db.Begin();
            for (int i = 0; i < _readyCount; i++) Handle(&_conns[_ready[i]]);
            if (txn) _db.EndRead();
            for (int i = 0; i < _readyCount; i++) QueueSend(_ready[i]);
            _readyCount = 0;
        }
    }

    void QueueSend(int idx)
    {
        Conn* c = &_conns[idx];
        c->State = ConnState.Sending;
        RingOf(idx).PrepSend(idx % _slots, c->Out + c->OutStart, c->OutEnd - c->OutStart, Ud(OpSend, idx, c->Gen));
    }
}

/// <summary>A free list of fixed-size native buffers, grown in slabs.</summary>
unsafe sealed class BufPool
{
    readonly int _size;
    byte** _free = (byte**)NativeMemory.Alloc(1024, (nuint)sizeof(byte*));
    int _count, _cap = 1024;

    public BufPool(int size) => _size = size;

    public byte* Take()
    {
        if (_count == 0)
        {
            byte* slab = (byte*)NativeMemory.Alloc((nuint)(_size * 64));
            for (int i = 0; i < 64; i++) Return(slab + i * _size);
        }
        return _free[--_count];
    }

    public void Return(byte* b)
    {
        if (_count == _cap)
        {
            _cap *= 2;
            _free = (byte**)NativeMemory.Realloc(_free, (nuint)_cap * (nuint)sizeof(byte*));
        }
        _free[_count++] = b;
    }
}
