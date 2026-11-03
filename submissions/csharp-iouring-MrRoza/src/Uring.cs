// A minimal io_uring driven by the raw syscalls. Everything used here exists in Linux 6.1+ (the benchmark box
// runs Ubuntu 24.04's 6.8): DEFER_TASKRUN, direct descriptors, multishot poll and recv, provided-buffer rings,
// EXT_ARG waits.
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
    [FieldOffset(44)] public uint FileIndex; // splice_fd_in / file_index
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
    public const byte OP_POLL_ADD = 6, OP_TIMEOUT = 11, OP_ACCEPT = 13, OP_CLOSE = 19, OP_SEND = 26, OP_RECV = 27, OP_SHUTDOWN = 34;
    public const byte SQE_FIXED_FILE = 1, SQE_IO_LINK = 4, SQE_IO_HARDLINK = 8, SQE_CQE_SKIP_SUCCESS = 64;
    public const uint CQE_F_BUFFER = 1, CQE_F_MORE = 2;
    const byte SQE_BUFFER_SELECT = 32;
    const ushort RECV_MULTISHOT = 2;
    const uint REGISTER_PBUF_RING = 22;
    const uint POLL_ADD_MULTI = 1, FILE_INDEX_ALLOC = 0xFFFFFFFF;
    const uint SETUP_CQSIZE = 1 << 3, SETUP_SUBMIT_ALL = 1 << 7, SETUP_COOP_TASKRUN = 1 << 8, SETUP_TASKRUN_FLAG = 1 << 9,
               SETUP_SINGLE_ISSUER = 1 << 12, SETUP_DEFER_TASKRUN = 1 << 13;
    const uint ENTER_GETEVENTS = 1, ENTER_EXT_ARG = 1 << 3;
    const long OFF_SQ_RING = 0, OFF_SQES = 0x10000000;
    const uint REGISTER_FILES = 2, REGISTER_EVENTFD = 4;

    public readonly int Fd;
    readonly uint* _sqHead, _sqTail, _cqHead, _cqTail, _sqFlags;
    readonly uint _sqMask, _cqMask, _sqEntries;
    readonly Sqe* _sqes;
    readonly Cqe* _cqes;
    uint _sqTailLocal; // our private tail; published on submit
    uint _toSubmit;
    // io_uring_getevents_arg + __kernel_timespec for EXT_ARG waits.
    readonly ulong* _waitArg = (ulong*)NativeMemory.AllocZeroed(48);

    public Uring(uint entries, uint cqEntries)
    {
        UringParams p = default;
        p.Flags = SETUP_CQSIZE | SETUP_SUBMIT_ALL | SETUP_COOP_TASKRUN | SETUP_TASKRUN_FLAG | SETUP_SINGLE_ISSUER | SETUP_DEFER_TASKRUN;
        p.CqEntries = cqEntries;
        long fd = Libc.syscall(Libc.SYS_io_uring_setup, entries, (long)&p, 0, 0, 0, 0);
        if (fd < 0) throw new Exception("io_uring_setup failed: errno " + Libc.Errno);
        Fd = (int)fd;

        // FEAT_SINGLE_MMAP (5.4+): SQ and CQ rings share one mapping.
        nuint sqSize = p.SqArray + p.SqEntries * 4;
        nuint cqSize = p.CqCqes + p.CqEntries * (uint)sizeof(Cqe);
        nuint ringSize = sqSize > cqSize ? sqSize : cqSize;
        byte* ring = (byte*)Libc.mmap(null, ringSize, Libc.PROT_READ | Libc.PROT_WRITE, Libc.MAP_SHARED | Libc.MAP_POPULATE, Fd, OFF_SQ_RING);
        if (ring == (byte*)-1) throw new Exception("mmap ring failed");
        _sqes = (Sqe*)Libc.mmap(null, p.SqEntries * (nuint)sizeof(Sqe), Libc.PROT_READ | Libc.PROT_WRITE, Libc.MAP_SHARED | Libc.MAP_POPULATE, Fd, OFF_SQES);
        if (_sqes == (Sqe*)-1) throw new Exception("mmap sqes failed");

        _sqHead = (uint*)(ring + p.SqHead);
        _sqTail = (uint*)(ring + p.SqTail);
        _sqMask = *(uint*)(ring + p.SqRingMask);
        _sqFlags = (uint*)(ring + p.SqFlags);
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

    /// <summary>Registers a sparse direct-descriptor table of <paramref name="slots"/> entries.</summary>
    public void RegisterSparseFiles(int slots)
    {
        int* fds = (int*)NativeMemory.Alloc((nuint)slots, 4);
        for (int i = 0; i < slots; i++) fds[i] = -1;
        long r = Libc.syscall(Libc.SYS_io_uring_register, Fd, REGISTER_FILES, (long)fds, slots, 0, 0);
        NativeMemory.Free(fds);
        if (r < 0) throw new Exception("IORING_REGISTER_FILES failed: errno " + Libc.Errno);
    }

    public void RegisterEventFd(int efd)
    {
        long r = Libc.syscall(Libc.SYS_io_uring_register, Fd, REGISTER_EVENTFD, (long)&efd, 1, 0, 0);
        if (r < 0) throw new Exception("IORING_REGISTER_EVENTFD failed: errno " + Libc.Errno);
    }

    // Provided-buffer ring (group 0): the kernel picks a buffer per multishot recv completion.
    byte* _bufRing, _bufBase;
    int _bufSize;
    ushort _bufMask, _bufTail;

    /// <summary>
    /// Registers a provided-buffer ring of <paramref name="entries"/> (power of two) buffers of <paramref name="size"/>
    /// bytes. Ubuntu's 6.8.0-136+ kernels refuse a registration whose reserved words are all zero (an inverted
    /// check, Launchpad #2162843), so a refused one is retried once with resv[0]=1. False if both are refused.
    /// </summary>
    public bool SetupBufRing(int entries, int size)
    {
        _bufRing = (byte*)NativeMemory.AlignedAlloc((nuint)entries * 16, 4096);
        new Span<byte>(_bufRing, entries * 16).Clear();
        ulong* reg = stackalloc ulong[5]; // io_uring_buf_reg: ring_addr, ring_entries|bgid|flags, resv[3]
        reg[0] = (ulong)_bufRing; reg[1] = (uint)entries; reg[2] = reg[3] = reg[4] = 0;
        long r = Libc.syscall(Libc.SYS_io_uring_register, Fd, REGISTER_PBUF_RING, (long)reg, 1, 0, 0);
        if (r < 0) { reg[2] = 1; r = Libc.syscall(Libc.SYS_io_uring_register, Fd, REGISTER_PBUF_RING, (long)reg, 1, 0, 0); }
        if (r < 0) { NativeMemory.AlignedFree(_bufRing); _bufRing = null; return false; }
        _bufSize = size; _bufMask = (ushort)(entries - 1);
        _bufBase = (byte*)NativeMemory.AlignedAlloc((nuint)entries * (nuint)size, 64);
        for (int i = 0; i < entries; i++) ProvideBuf(i);
        return true;
    }

    public byte* BufAddr(int bid) => _bufBase + (nint)bid * _bufSize;

    /// <summary>Hands buffer <paramref name="bid"/> back to the kernel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ProvideBuf(int bid)
    {
        byte* e = _bufRing + (_bufTail & _bufMask) * 16; // io_uring_buf: addr, len, bid (entry 0's resv is the tail)
        *(ulong*)e = (ulong)BufAddr(bid); *(uint*)(e + 8) = (uint)_bufSize; *(ushort*)(e + 12) = (ushort)bid;
        _bufTail++;
        Volatile.Write(ref *(ushort*)(_bufRing + 14), _bufTail);
    }

    /// <summary>Multishot recv on a direct descriptor into the buffer ring: one CQE per arrival, no SQE per request.</summary>
    public void PrepRecvMulti(int slot, ulong userData)
    {
        Sqe* s = GetSqe();
        s->Opcode = OP_RECV; s->Fd = slot; s->Flags = SQE_FIXED_FILE | SQE_BUFFER_SELECT; s->Ioprio = RECV_MULTISHOT;
        s->BufIndex = 0; /* buf_group */ s->UserData = userData;
    }

    public bool HasPending => _toSubmit > 0;

    /// <summary>Next free SQE, zeroed. Submits first if the ring is full.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Sqe* GetSqe()
    {
        if (_sqTailLocal - Volatile.Read(ref *_sqHead) >= _sqEntries) Enter(0, 0);
        Sqe* s = &_sqes[_sqTailLocal & _sqMask];
        *s = default;
        _sqTailLocal++;
        _toSubmit++;
        return s;
    }

    /// <summary>
    /// Publishes queued SQEs and runs pending task work (DEFER_TASKRUN only posts completions inside
    /// io_uring_enter with GETEVENTS). Waits for <paramref name="wait"/> completions, at most
    /// <paramref name="timeoutNs"/> if that is positive.
    /// </summary>
    public void Enter(uint wait, long timeoutNs)
    {
        Volatile.Write(ref *_sqTail, _sqTailLocal);
        uint n = _toSubmit;
        long r;
        if (timeoutNs > 0 && wait > 0)
        {
            long* ts = (long*)(_waitArg + 3);
            ts[0] = timeoutNs / 1_000_000_000; ts[1] = timeoutNs % 1_000_000_000;
            _waitArg[0] = 0; _waitArg[1] = 0; _waitArg[2] = (ulong)ts; // sigmask, sigmask_sz|pad, ts
            r = Libc.syscall(Libc.SYS_io_uring_enter, Fd, n, wait, ENTER_GETEVENTS | ENTER_EXT_ARG, (long)_waitArg, 24);
        }
        else r = Libc.syscall(Libc.SYS_io_uring_enter, Fd, n, wait, ENTER_GETEVENTS, 0, 0);
        if (r < 0)
        {
            int e = Libc.Errno;
            if (e == 62 /*ETIME*/ || e == 4 /*EINTR*/ || e == 16 /*EBUSY*/ || e == 11 /*EAGAIN*/) return;
            throw new Exception("io_uring_enter failed: errno " + e);
        }
        _toSubmit -= (uint)r > n ? n : (uint)r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPeek(out Cqe* cqe)
    {
        uint head = *_cqHead;
        if (head == Volatile.Read(ref *_cqTail)) { cqe = null; return false; }
        cqe = &_cqes[head & _cqMask];
        return true;
    }

    /// <summary>
    /// The kernel has deferred task work queued for this ring (TASKRUN_FLAG). Newer kernels run only part of it
    /// per enter, and a DEFER_TASKRUN ring signals its eventfd only when its work list goes from empty to
    /// non-empty, so sleeping in epoll while this is set can sleep forever.
    /// </summary>
    public bool TaskRunPending => (Volatile.Read(ref *_sqFlags) & 2 /*IORING_SQ_TASKRUN*/) != 0;

    public uint ReadyCount => Volatile.Read(ref *_cqTail) - *_cqHead;

    public bool HasCompletions => *_cqHead != Volatile.Read(ref *_cqTail);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance() => Volatile.Write(ref *_cqHead, *_cqHead + 1);

    /// <summary>One-shot accept into a free slot of this ring's direct-descriptor table (CQE res = slot).</summary>
    public void PrepAcceptDirect(int listenFd, ulong userData)
    {
        Sqe* s = GetSqe();
        s->Opcode = OP_ACCEPT; s->Fd = listenFd; s->FileIndex = FILE_INDEX_ALLOC; s->UserData = userData;
    }

    /// <summary>Multishot POLLIN on a direct descriptor: one CQE every time data arrives, no buffer held.</summary>
    public void PrepPollMulti(int slot, ulong userData)
    {
        Sqe* s = GetSqe();
        s->Opcode = OP_POLL_ADD; s->Fd = slot; s->Flags = SQE_FIXED_FILE; s->Len = POLL_ADD_MULTI;
        s->OpFlags = 0x001 /*POLLIN*/ | 0x2000 /*POLLRDHUP*/; s->UserData = userData;
    }

    public void PrepRecv(int slot, byte* buf, int len, ulong userData)
    {
        Sqe* s = GetSqe();
        s->Opcode = OP_RECV; s->Fd = slot; s->Flags = SQE_FIXED_FILE; s->Addr = (ulong)buf; s->Len = (uint)len;
        s->OpFlags = 0x40; /* MSG_DONTWAIT: the poll said it's readable; never park a buffer */ s->UserData = userData;
    }

    public void PrepSend(int slot, byte* buf, int len, ulong userData)
    {
        Sqe* s = GetSqe();
        s->Opcode = OP_SEND; s->Fd = slot; s->Flags = SQE_FIXED_FILE; s->Addr = (ulong)buf; s->Len = (uint)len;
        s->OpFlags = 0x4000; /* MSG_NOSIGNAL */ s->UserData = userData;
    }

    /// <summary>SHUTDOWN hard-linked to CLOSE of the slot: the shutdown ends the multishot poll, the close frees the slot.</summary>
    public void PrepShutdownClose(int slot, ulong userData)
    {
        Sqe* s = GetSqe();
        s->Opcode = OP_SHUTDOWN; s->Fd = slot; s->Flags = SQE_FIXED_FILE | SQE_IO_HARDLINK | SQE_CQE_SKIP_SUCCESS;
        s->Len = 2; /* SHUT_RDWR */ s->UserData = userData;
        s = GetSqe();
        s->Opcode = OP_CLOSE; s->FileIndex = (uint)slot + 1; s->UserData = userData;
    }

    public void PrepTimeout(long* ts, ulong userData)
    {
        Sqe* s = GetSqe();
        s->Opcode = OP_TIMEOUT; s->Fd = -1; s->Addr = (ulong)ts; s->Len = 1; s->UserData = userData;
    }
}
