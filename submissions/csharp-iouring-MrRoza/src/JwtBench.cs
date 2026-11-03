// DEV builds only: times the scalar HMAC against the 8-lane one on token-sized messages.
#if DEV_TUNABLES
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Twelve;

static unsafe class JwtBench
{
    public static void Run()
    {
        const int N = 400_000, Len = 150;
        byte* buf = (byte*)NativeMemory.Alloc(8 * Len);
        for (int i = 0; i < 8 * Len; i++) buf[i] = (byte)(i * 31);
        byte** msg = stackalloc byte*[8];
        int* len = stackalloc int[8];
        for (int l = 0; l < 8; l++) { msg[l] = buf + l * Len; len[l] = Len; }
        byte* macs = stackalloc byte[8 * 32];
        for (int lanes = 3; lanes <= 8; lanes++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i += lanes) Jwt.HmacBatch(msg, len, lanes, macs);
            double x8 = sw.Elapsed.TotalMilliseconds * 1e6 / N;
            sw.Restart();
            for (int i = 0; i < N; i++) Jwt.HmacScalar(msg[i & 7], Len, macs);
            double sc = sw.Elapsed.TotalMilliseconds * 1e6 / N;
            Console.WriteLine($"{lanes} lanes: scalar {sc:F0} ns/token, batch {x8:F0} ns/token");
        }
    }
}
#endif
