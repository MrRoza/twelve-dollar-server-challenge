// A minimal io_uring driven by the raw syscalls: one ring, one thread.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Twelve;

[StructLayout(LayoutKind.Explicit, Size = 64)]
unsafe struct Sqe
{
    [FieldOffset(0)] public byte Opcode;
    [FieldOffset(1)] public byte Flags;
    [FieldOffset(2)] public ushort Ioprio;
    [FieldOffset(4)] public int Fd;
    [FieldOffset(8)] public ulong Off;
    [FieldOffset(16)] public ulong Addr;
    [FieldOffset(24)] public uint Len;
    [FieldOffset(28)] public uint OpFlags;
    [FieldOffset(32)] public ulong UserData;
    [FieldOffset(40)] public ushort BufIndex;
    [FieldOffset(42)] public ushort Personality;
    [FieldOffset(44)] public int SpliceFdIn;
    [FieldOffset(48)] public ulong Addr3;
    [FieldOffset(56)] public ulong Pad;
}

[StructLayout(LayoutKind.Sequential)]
struct Cqe
{
    public ulong UserData;
    public int Res;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct UringParams
{
    public uint SqEntries, CqEntries, Flags, SqThreadCpu, SqThreadIdle, Features, WqFd;
    public fixed uint Resv[3];
    // io_sqring_offsets
    public uint SqHead, SqTail, SqRingMask, SqRingEntries, SqFlags, SqDropped, SqArray, SqResv1;
    public ulong SqUserAddr;
    // io_cqring_offsets
    public uint CqHead, CqTail, CqRingMask, CqRingEntries, CqOverflow, CqCqes, CqFlags, CqResv1;
    public ulong CqUserAddr;
}

unsafe sealed class Uring
{
    public const byte OP_TIMEOUT = 11, OP_ACCEPT = 13, OP_CLOSE = 19, OP_SEND = 26, OP_RECV = 27;
    public const ushort ACCEPT_MULTISHOT = 1;
    public const uint CQE_F_MORE = 2;
    const uint SETUP_CQSIZE = 1 << 3, SETUP_SUBMIT_ALL = 1 << 7, SETUP_COOP_TASKRUN = 1 << 8,
               SETUP_SINGLE_ISSUER = 1 << 12, SETUP_DEFER_TASKRUN = 1 << 13;
    const uint ENTER_GETEVENTS = 1;
    const long OFF_SQ_RING = 0, OFF_CQ_RING = 0x8000000, OFF_SQES = 0x10000000;

    readonly int _fd;
    readonly uint* _sqHead, _sqTail, _cqHead, _cqTail;
    readonly uint _sqMask, _cqMask, _sqEntries;
    readonly Sqe* _sqes;
    readonly Cqe* _cqes;
    uint _sqTailLocal; // our private tail; published on Submit
    uint _toSubmit;

    public Uring(uint entries, uint cqEntries)
    {
        UringParams p = default;
        p.Flags = SETUP_CQSIZE | SETUP_SUBMIT_ALL | SETUP_COOP_TASKRUN | SETUP_SINGLE_ISSUER | SETUP_DEFER_TASKRUN;
        p.CqEntries = cqEntries;
        long fd = Libc.syscall(Libc.SYS_io_uring_setup, entries, (long)&p, 0, 0, 0, 0);
        if (fd < 0) throw new Exception("io_uring_setup failed: errno " + Libc.Errno);
        _fd = (int)fd;

        // FEAT_SINGLE_MMAP is in every kernel we care about (5.4+): SQ and CQ rings share one mapping.
        nuint sqSize = p.SqArray + p.SqEntries * 4;
        nuint cqSize = p.CqCqes + p.CqEntries * (uint)sizeof(Cqe);
        nuint ringSize = sqSize > cqSize ? sqSize : cqSize;
        byte* ring = (byte*)Libc.mmap(null, ringSize, Libc.PROT_READ | Libc.PROT_WRITE, Libc.MAP_SHARED | Libc.MAP_POPULATE, _fd, OFF_SQ_RING);
        if (ring == (byte*)-1) throw new Exception("mmap ring failed");
        _sqes = (Sqe*)Libc.mmap(null, p.SqEntries * (nuint)sizeof(Sqe), Libc.PROT_READ | Libc.PROT_WRITE, Libc.MAP_SHARED | Libc.MAP_POPULATE, _fd, OFF_SQES);
        if (_sqes == (Sqe*)-1) throw new Exception("mmap sqes failed");

        _sqHead = (uint*)(ring + p.SqHead);
        _sqTail = (uint*)(ring + p.SqTail);
        _sqMask = *(uint*)(ring + p.SqRingMask);
        _sqEntries = p.SqEntries;
        _cqHead = (uint*)(ring + p.CqHead);
        _cqTail = (uint*)(ring + p.CqTail);
        _cqMask = *(uint*)(ring + p.CqRingMask);
        _cqes = (Cqe*)(ring + p.CqCqes);

        // Identity-map the SQ index array once: slot i always points at SQE i.
        uint* array = (uint*)(ring + p.SqArray);
        for (uint i = 0; i < p.SqEntries; i++) array[i] = i;
        _sqTailLocal = *_sqTail;
    }

    /// <summary>Next free SQE, zeroed. Submits first if the ring is full.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Sqe* GetSqe()
    {
        if (_sqTailLocal - Volatile.Read(ref *_sqHead) >= _sqEntries) Submit(0);
        Sqe* s = &_sqes[_sqTailLocal & _sqMask];
        *s = default;
        _sqTailLocal++;
        _toSubmit++;
        return s;
    }

    /// <summary>Publishes queued SQEs and optionally waits for at least <paramref name="wait"/> completions.</summary>
    public int Submit(uint wait)
    {
        Volatile.Write(ref *_sqTail, _sqTailLocal);
        uint n = _toSubmit;
        // DEFER_TASKRUN: completions are only produced inside io_uring_enter with GETEVENTS, so always pass it.
        long r = Libc.syscall(Libc.SYS_io_uring_enter, _fd, n, wait, ENTER_GETEVENTS, 0, 0);
        if (r < 0)
        {
            int e = Libc.Errno;
            if (e == 4 /*EINTR*/ || e == 16 /*EBUSY*/ || e == 11 /*EAGAIN*/) return 0;
            throw new Exception("io_uring_enter failed: errno " + e);
        }
        _toSubmit -= (uint)r > n ? n : (uint)r;
        return (int)r;
    }

    /// <summary>Peeks the next completion. Call <see cref="Advance"/> after handling it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPeek(out Cqe* cqe)
    {
        uint head = *_cqHead;
        if (head == Volatile.Read(ref *_cqTail)) { cqe = null; return false; }
        cqe = &_cqes[head & _cqMask];
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance() => Volatile.Write(ref *_cqHead, *_cqHead + 1);

    public void PrepAcceptMultishot(int listenFd, ulong userData)
    {
        Sqe* s = GetSqe();
        s->Opcode = OP_ACCEPT; s->Fd = listenFd; s->Ioprio = ACCEPT_MULTISHOT; s->UserData = userData;
    }

    public void PrepRecv(int fd, byte* buf, int len, ulong userData)
    {
        Sqe* s = GetSqe();
        s->Opcode = OP_RECV; s->Fd = fd; s->Addr = (ulong)buf; s->Len = (uint)len; s->UserData = userData;
    }

    public void PrepSend(int fd, byte* buf, int len, ulong userData)
    {
        Sqe* s = GetSqe();
        s->Opcode = OP_SEND; s->Fd = fd; s->Addr = (ulong)buf; s->Len = (uint)len;
        s->OpFlags = 0x4000; /* MSG_NOSIGNAL */ s->UserData = userData;
    }

    public void PrepClose(int fd, ulong userData)
    {
        Sqe* s = GetSqe();
        s->Opcode = OP_CLOSE; s->Fd = fd; s->UserData = userData;
    }
}
