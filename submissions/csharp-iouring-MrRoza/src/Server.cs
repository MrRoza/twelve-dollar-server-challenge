// The event loop: one thread, up to 16 io_uring rings (each holds up to ~65k direct descriptors, so
// LimitNOFILE=65535 doesn't cap connections). Each loop turn ("batch") drains every ring's completions,
// collects the connections that now hold a complete request, runs the writes in one BEGIN IMMEDIATE
// transaction (group commit) and the reads in one read transaction, and only then queues the sends.
// So a 201 is always sent after its row is committed.
//
// Idle connections hold no buffer: each one has a multishot recv armed on the ring's provided-buffer ring.
// Arriving bytes are copied into a pooled per-connection buffer and the provided buffer goes straight back;
// the pooled one goes back as soon as the response is sent. If the kernel refuses the buffer ring, each
// connection gets a multishot POLLIN instead, and a recv with MSG_DONTWAIT when it fires.
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
    public byte InQ, WantQ;      // on its ring's drain queue / the current connection asked for a drain read
    public Route Route;
    public int SegOff, SegLen;   // the :id path segment
    public int AuthOff, AuthLen; // AuthOff -1 = no Authorization header
    public int BodyOff, BodyLen;
}

sealed unsafe class Ring
{
    public Uring U = null!;
    public int Index, Free, Accepts;
    public bool Ms;              // multishot recv + buffer ring (else poll + recv)
    public int* Q; public int QHead, QTail; // drain queue (FIFO of connection indexes, each at most once)
}

unsafe sealed partial class Server
{
    const ulong OpAccept = 1, OpPoll = 2, OpRecv = 3, OpSend = 4, OpClose = 5, OpTimeout = 6, OpRecvMs = 7, OpRecvOne = 8;
    // Accepts in flight per ring. A ramp of a few thousand connections a second outruns 64 on the 1 vCPU box
    // (listen queues fill, clients wait out SYN retransmits), so 512, spread over the listeners.
    const int MaxRings = 16, MaxAccepts = 512;
    const int InPoolSize = 4096, OutPoolSize = Out.PoolSize, InMax = 65536;

    readonly int _slots;              // direct-descriptor slots per ring
    readonly Ring[] _rings = new Ring[MaxRings];
    int _ringCount, _acceptRing;
    readonly Db _db;
    readonly int[] _listens;
    readonly int[] _listenAccepts;     // accepts in flight per listener (all rings)
    readonly Conn* _conns;            // [ring * _slots + slot]
    readonly int* _ready, _writes;
    int _readyCount, _writeCount;
    readonly long* _timeout;          // __kernel_timespec for the write-retry timeout
    bool _timeoutArmed;
    long _writeOldest;                // Stopwatch timestamp of the oldest queued write
    readonly long _startTicks = Environment.TickCount64;
    long _now;                        // unix seconds, refreshed per batch
    readonly byte* _scratch, _name;
    readonly byte* _macs = (byte*)NativeMemory.Alloc(Sha256x8.Lanes * 32);
    byte* _batchMac;                  // the current write's precomputed HMAC, or null
    readonly BufPool _inPool = new(InPoolSize), _outPool = new(OutPoolSize);

    // Tunables: compile-time defaults (see Tune.cs).
    static readonly uint BatchMin = (uint)Tune.Get("BATCH_MIN", 128);
    static readonly long BatchWaitNs = Tune.Get("BATCH_WAIT_US", 2000) * 1000;
    // The write stage: queued writes commit together once WriteMin are queued or the oldest waited WriteWaitUs.
    // Every commit rewrites the same hot pages (newest posts leaf, index leaves) into the WAL, so fewer,
    // bigger commits write far fewer frames per row.
    static readonly int WriteMin = (int)Tune.Get("WRITE_MIN", 64);
    static readonly long WriteWaitTicks = Tune.Get("WRITE_WAIT_US", 5000) * Stopwatch.Frequency / 1_000_000;
    static readonly int SlotsOverride = (int)Tune.Get("RING_SLOTS", 0);
    static readonly int RecvBufs = (int)Tune.Get("RECV_BUFS", 4096), RecvBufSize = (int)Tune.Get("RECV_BUF_SIZE", 2048);
    static readonly bool NoMs = Tune.Get("NO_MULTISHOT", 0) != 0;

    public Server(int[] listenFds, Db db)
    {
        _listens = listenFds;
        _listenAccepts = new int[listenFds.Length];
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
        r.Ms = !NoMs && r.U.SetupBufRing(RecvBufs, RecvBufSize);
        if (!r.Ms) Log.Info($"ring {r.Index}: no provided-buffer ring, using poll + recv");
        r.Q = (int*)NativeMemory.Alloc((nuint)_slots, sizeof(int));
        _rings[_ringCount++] = r;
        Log.Info($"ring {r.Index} up ({_ringCount * _slots} connection slots)");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Ud(ulong op, int idx, uint gen) => (ulong)gen << 32 | (ulong)(uint)idx << 8 | op;

    /// <summary>
    /// Keeps up to MaxAccepts one-shot direct accepts armed on the accepting ring (an equal share per listener),
    /// never more than it has free slots: an accept that finds no free slot has already taken the connection
    /// off the listen queue. Moves on to another ring, or opens one, only when it is full.
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
        int share = MaxAccepts / _listens.Length;
        for (int l = 0; l < _listens.Length; l++)
            while (_listenAccepts[l] < share && r.Accepts < MaxAccepts && r.Free - r.Accepts > 0)
            {
                r.U.PrepAcceptDirect(_listens[l], Ud(OpAccept, l, 0));
                r.Accepts++;
                _listenAccepts[l]++;
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
            bool draining = IssueDrainReads();
            if (draining) { for (int i = 0; i < _ringCount; i++) _rings[i].U.Enter(0, 0); } // queued reads: don't wait
            else if (_ringCount == 1)
            {
                // Batch window: wait until BatchMin completions are ready or BatchWaitNs passed, so one turn
                // (one enter, one SQLite snapshot, one commit) serves many requests.
                if (BatchMin <= 1) _rings[0].U.Enter(1, 0);
                else _rings[0].U.Enter(BatchMin, BatchWaitNs);
            }
            else
            {
                // Several rings, one thread. A DEFER_TASKRUN ring only posts completions inside its own
                // io_uring_enter (and its eventfd proved no reliable wakeup), so: give every other ring one
                // non-blocking enter (submit its sends, collect its completions), then wait on ring 0 for the rest
                // of the batch or the window. Completions on the other rings wait at most one window.
                uint others = 0;
                for (int i = 1; i < _ringCount; i++) { Uring u = _rings[i].U; u.Enter(0, 0); others += u.ReadyCount; }
                // min_complete counts every completion in ring 0's queue, so only the other rings' are subtracted.
                Uring u0 = _rings[0].U;
                if (others >= BatchMin || BatchMin <= 1) u0.Enter(0, 0);
                else u0.Enter(BatchMin - others, BatchWaitNs);
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
                        case OpAccept: _listenAccepts[idx]--; OnAccept(ring, res); break;
                        case OpPoll: OnPoll(idx, gen, res, flags); break;
                        case OpRecv: OnRecv(idx, res); break;
                        case OpRecvMs: OnRecvMs(ring, idx, gen, res, flags, false); break;
                        case OpRecvOne: OnRecvMs(ring, idx, gen, res, flags, true); break;
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
        c->State = ConnState.Idle; c->Readable = 0; c->InLen = 0; c->ReqLen = 0; c->CloseAfter = 0; c->WantQ = 0;
        if (r.Ms) r.U.PrepRecvMulti(res, Ud(OpRecvMs, idx, c->Gen));
        else r.U.PrepPollMulti(res, Ud(OpPoll, idx, c->Gen));
    }

    /// <summary>
    /// After a burst every connection has data waiting, the buffer ring runs dry and the multishot recvs end
    /// with ENOBUFS. Re-arming them all at once would only repeat that, so an ended recv isn't re-armed: its
    /// connection goes on the ring's drain queue, and each turn issues single-shot reads for it, never more
    /// than half the ring's buffers. A read that fills its buffer is queued again; a short one or EAGAIN means
    /// the socket is drained, and the multishot recv is armed again.
    /// </summary>
    void EnqueueDrain(Ring r, int idx, Conn* c)
    {
        c->WantQ = 1;
        if (c->InQ != 0) return; // already queued (perhaps by this slot's previous connection)
        c->InQ = 1;
        r.Q[r.QTail] = idx;
        r.QTail = r.QTail + 1 == _slots ? 0 : r.QTail + 1;
    }

    /// <summary>Issues this turn's drain reads. True if any ring still has a queue (then the loop doesn't wait).</summary>
    bool IssueDrainReads()
    {
        bool left = false;
        for (int ri = 0; ri < _ringCount; ri++)
        {
            Ring r = _rings[ri];
            int budget = RecvBufs / 2;
            while (r.QHead != r.QTail && budget > 0)
            {
                int idx = r.Q[r.QHead];
                r.QHead = r.QHead + 1 == _slots ? 0 : r.QHead + 1;
                Conn* c = &_conns[idx];
                c->InQ = 0;
                if (c->WantQ == 0 || c->State is ConnState.Free or ConnState.Closing) continue;
                c->WantQ = 0;
                r.U.PrepRecvOne(idx % _slots, Ud(OpRecvOne, idx, c->Gen));
                budget--;
            }
            if (r.QHead != r.QTail) left = true;
        }
        return left;
    }

    void OnRecvMs(Ring ring, int idx, uint gen, int res, uint flags, bool one)
    {
        Conn* c = &_conns[idx];
        bool live = c->Gen == gen && c->State is not (ConnState.Free or ConnState.Closing);
        bool ok = true;
        if ((flags & Uring.CQE_F_BUFFER) != 0)
        {
            int bid = (int)(flags >> 16);
            if (live && res > 0) ok = Append(c, ring.U.BufAddr(bid), res);
            ring.U.ProvideBuf(bid);
        }
        if (!live) return;
        if (ok && res > 0)
        {
            if (one)
            {
                // A full buffer: there may be more. A short read drained the socket: back to multishot.
                if (res == RecvBufSize) EnqueueDrain(ring, idx, c);
                else ring.U.PrepRecvMulti(idx % _slots, Ud(OpRecvMs, idx, c->Gen));
            }
            else if ((flags & Uring.CQE_F_MORE) == 0) EnqueueDrain(ring, idx, c); // the multishot recv ended
            if (c->State == ConnState.Idle) Parse(idx, c);
            return;
        }
        if (ok && res == -105) { EnqueueDrain(ring, idx, c); return; } // ENOBUFS: ring ran dry, the data waits in the socket
        if (ok && one && res == -11) { ring.U.PrepRecvMulti(idx % _slots, Ud(OpRecvMs, idx, c->Gen)); return; } // EAGAIN: drained
        // EOF, an error or an oversized request. A response in progress still goes out, then the connection closes.
        if (c->State == ConnState.Idle) Close(idx, c); else c->CloseAfter = 1;
    }

    /// <summary>Appends received bytes to the connection's input buffer. False if the request would pass InMax.</summary>
    bool Append(Conn* c, byte* src, int n)
    {
        if (c->In == null) { c->In = _inPool.Take(); c->InCap = InPoolSize; }
        if (c->InCap - c->InLen < n)
        {
            int cap = c->InCap;
            while (cap - c->InLen < n) cap *= 2;
            if (cap > InMax) return false;
            byte* nb = (byte*)NativeMemory.Alloc((nuint)cap);
            Buffer.MemoryCopy(c->In, nb, cap, c->InLen);
            ReleaseIn(c);
            c->In = nb; c->InCap = cap;
        }
        Buffer.MemoryCopy(src, c->In + c->InLen, n, n);
        c->InLen += n;
        return true;
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
        else if (r == 0)
        {
            // Incomplete. Multishot: the rest arrives by itself. Poll: it may already be there; if not, EAGAIN.
            if (_rings[idx / _slots].Ms) c->State = ConnState.Idle; else StartRecv(idx, c);
        }
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
                int* slot = stackalloc int[Sha256x8.Lanes];
                for (int s = 0; s < _writeCount; s += Sha256x8.Lanes)
                {
                    int n = Math.Min(Sha256x8.Lanes, _writeCount - s);
                    HmacWrites(s, n, slot);
                    for (int i = 0; i < n; i++)
                    {
                        _batchMac = slot[i] >= 0 ? _macs + slot[i] * 32 : null;
                        Handle(&_conns[_writes[s + i]]);
                    }
                    _batchMac = null;
                }
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

    /// <summary>
    /// Computes the HMACs of up to 8 writes' bearer tokens together (one AVX2 lane each). slot[i] is where write
    /// s+i's MAC is in _macs, or -1 (no well-formed token, too long, or too few tokens to be worth it: then
    /// Verify computes it alone). Every token's signature is still computed from its own bytes, now.
    /// </summary>
    void HmacWrites(int s, int n, int* slot)
    {
        byte** msg = stackalloc byte*[Sha256x8.Lanes];
        int* len = stackalloc int[Sha256x8.Lanes];
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            slot[i] = -1;
            if (!Sha256x8.On) continue;
            Conn* c = &_conns[_writes[s + i]];
            if (c->AuthOff < 0 || c->AuthLen <= 7) continue;
            byte* tok = c->In + c->AuthOff + 7;
            int d2 = Jwt.SigningLen(tok, c->AuthLen - 7);
            if (d2 < 0 || d2 > Sha256x8.MaxMsg) continue;
            msg[k] = tok; len[k] = d2; slot[i] = k++;
        }
        if (k >= 3) Jwt.HmacBatch(msg, len, k, _macs);
        else for (int i = 0; i < n; i++) slot[i] = -1; // 1-2 tokens: the scalar path is cheaper
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
