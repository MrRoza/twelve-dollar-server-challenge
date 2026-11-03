// Raw libc and SQLite imports. Both are bound statically at link time (DirectPInvoke in Server.csproj),
// so a call is a plain C call: no lazy binding, no marshalling (only blittable types are used).
using System.Runtime.InteropServices;
// SuppressGCTransition below marks short, non-blocking calls that never call back into managed code.

namespace Twelve;

static unsafe class Libc
{
    public const int AF_INET = 2, SOCK_STREAM = 1, SOL_SOCKET = 1, SO_REUSEADDR = 2, SO_REUSEPORT = 15;
    public const int IPPROTO_TCP = 6, TCP_NODELAY = 1;
    public const int PROT_READ = 1, PROT_WRITE = 2, MAP_SHARED = 1, MAP_POPULATE = 0x8000;
    public const long SYS_io_uring_setup = 425, SYS_io_uring_enter = 426, SYS_io_uring_register = 427;

    [DllImport("libc")] public static extern int socket(int domain, int type, int protocol);
    [DllImport("libc")] public static extern int bind(int fd, void* addr, uint len);
    [DllImport("libc")] public static extern int listen(int fd, int backlog);
    [DllImport("libc")] public static extern int setsockopt(int fd, int level, int name, void* val, uint len);
    [DllImport("libc")] public static extern int close(int fd);
    [DllImport("libc")] public static extern void* mmap(void* addr, nuint len, int prot, int flags, int fd, long off);
    [DllImport("libc")] public static extern long syscall(long n, long a1, long a2, long a3, long a4, long a5, long a6);
    [DllImport("libc")] public static extern int inet_pton(int af, byte* src, void* dst);
    [DllImport("libc")] public static extern int* __errno_location();
    [DllImport("libc")] public static extern nint write(int fd, byte* buf, nuint n);

    [DllImport("libc")] public static extern int getrlimit(int resource, ulong* rlim);
    [DllImport("libc")] public static extern int epoll_create1(int flags);
    [DllImport("libc")] public static extern int epoll_ctl(int epfd, int op, int fd, byte* ev);
    [DllImport("libc")] public static extern int epoll_wait(int epfd, byte* events, int max, int timeoutMs);
    [DllImport("libc")] public static extern int eventfd(uint initval, int flags);
    [DllImport("libc")] public static extern nint read(int fd, void* buf, nuint n);
    [DllImport("libc")] public static extern int usleep(uint usec);

    public static int Errno => *__errno_location();
}

static unsafe class Sqlite
{
    public const int OK = 0, ROW = 100, DONE = 101;
    public const int OPEN_READWRITE = 0x2, OPEN_NOMUTEX = 0x8000;
    public const int CHECKPOINT_PASSIVE = 0, CHECKPOINT_RESTART = 2;
    public static readonly nint STATIC = 0;

    [DllImport("sqlite3")] public static extern int sqlite3_open_v2(byte* file, nint* db, int flags, byte* vfs);
    [DllImport("sqlite3")] public static extern int sqlite3_exec(nint db, byte* sql, nint cb, nint arg, byte** err);
    [DllImport("sqlite3")] public static extern int sqlite3_prepare_v3(nint db, byte* sql, int n, uint flags, nint* stmt, byte** tail);
    [DllImport("sqlite3")] public static extern int sqlite3_step(nint stmt);
    [DllImport("sqlite3"), SuppressGCTransition] public static extern int sqlite3_reset(nint stmt);
    [DllImport("sqlite3")] public static extern int sqlite3_finalize(nint stmt);
    [DllImport("sqlite3"), SuppressGCTransition] public static extern int sqlite3_bind_int64(nint stmt, int i, long v);
    [DllImport("sqlite3"), SuppressGCTransition] public static extern int sqlite3_bind_text(nint stmt, int i, byte* s, int n, nint destructor);
    [DllImport("sqlite3"), SuppressGCTransition] public static extern long sqlite3_column_int64(nint stmt, int i);
    [DllImport("sqlite3"), SuppressGCTransition] public static extern byte* sqlite3_column_text(nint stmt, int i);
    [DllImport("sqlite3"), SuppressGCTransition] public static extern int sqlite3_column_bytes(nint stmt, int i);
    [DllImport("sqlite3"), SuppressGCTransition] public static extern int sqlite3_changes(nint db);
    [DllImport("sqlite3")] public static extern byte* sqlite3_errmsg(nint db);
    [DllImport("sqlite3"), SuppressGCTransition] public static extern int sqlite3_get_autocommit(nint db);
    [DllImport("sqlite3")] public static extern int sqlite3_wal_checkpoint_v2(nint db, byte* name, int mode, int* log, int* ckpt);
}
