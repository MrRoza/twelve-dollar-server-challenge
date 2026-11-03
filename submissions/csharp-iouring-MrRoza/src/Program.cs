// Entry point: config from SQLITE_PATH, JWT_SECRET, HOST and PORT; then the single-threaded io_uring loop.
using System.Text;

namespace Twelve;

static unsafe class Program
{
    static int Main()
    {
        string dbPath = Environment.GetEnvironmentVariable("SQLITE_PATH") ?? "feed.db";
        string secret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? "twelve-dollar-challenge";
        string host = Environment.GetEnvironmentVariable("HOST") ?? "127.0.0.1";
        int port = int.Parse(Environment.GetEnvironmentVariable("PORT") ?? "3000");

        Jwt.Init(secret);
        var db = new Db(dbPath);
        Db.StartCheckpointer(dbPath);
        int fd = Listen(host, port);
        Log.Info($"listening on {host}:{port}, db {dbPath}");
        new Server(fd, db).Run();
        return 0;
    }

    static int Listen(string host, int port)
    {
        int fd = Libc.socket(Libc.AF_INET, Libc.SOCK_STREAM, 0);
        if (fd < 0) throw new Exception("socket failed: errno " + Libc.Errno);
        int one = 1;
        Libc.setsockopt(fd, Libc.SOL_SOCKET, Libc.SO_REUSEADDR, &one, 4);
        // Accepted sockets inherit TCP_NODELAY from the listener on Linux.
        Libc.setsockopt(fd, Libc.IPPROTO_TCP, Libc.TCP_NODELAY, &one, 4);
        byte* addr = stackalloc byte[16];
        new Span<byte>(addr, 16).Clear();
        *(ushort*)addr = Libc.AF_INET;
        addr[2] = (byte)(port >> 8); addr[3] = (byte)port;
        byte[] h = Encoding.ASCII.GetBytes(host + "\0");
        fixed (byte* hp = h)
            if (Libc.inet_pton(Libc.AF_INET, hp, addr + 4) != 1) throw new Exception("bad HOST " + host);
        if (Libc.bind(fd, addr, 16) != 0) throw new Exception("bind failed: errno " + Libc.Errno);
        if (Libc.listen(fd, 65535) != 0) throw new Exception("listen failed: errno " + Libc.Errno);
        return fd;
    }
}
