// HS256 JWT verification with a hand-written SHA-256 (the benchmark Xeon has no SHA-NI, and the BCL
// path goes through OpenSSL with allocations). The HMAC key pads are hashed once at startup: that's the
// key schedule, not a remembered verification. Every token's signature is computed and checked.
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Twelve;

static unsafe class Sha256
{
    static readonly uint[] K =
    {
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    };

    public static void Init(uint* h)
    {
        h[0] = 0x6a09e667; h[1] = 0xbb67ae85; h[2] = 0x3c6ef372; h[3] = 0xa54ff53a;
        h[4] = 0x510e527f; h[5] = 0x9b05688c; h[6] = 0x1f83d9ab; h[7] = 0x5be0cd19;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Compress(uint* h, byte* block)
    {
        uint* w = stackalloc uint[64];
        for (int i = 0; i < 16; i++) w[i] = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(block + i * 4, 4));
        for (int i = 16; i < 64; i++)
        {
            uint x = w[i - 15], y = w[i - 2];
            uint s0 = BitOperations.RotateRight(x, 7) ^ BitOperations.RotateRight(x, 18) ^ (x >> 3);
            uint s1 = BitOperations.RotateRight(y, 17) ^ BitOperations.RotateRight(y, 19) ^ (y >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }
        uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
        fixed (uint* k = K)
        {
            for (int i = 0; i < 64; i++)
            {
                uint S1 = BitOperations.RotateRight(e, 6) ^ BitOperations.RotateRight(e, 11) ^ BitOperations.RotateRight(e, 25);
                uint ch = (e & f) ^ (~e & g);
                uint t1 = hh + S1 + ch + k[i] + w[i];
                uint S0 = BitOperations.RotateRight(a, 2) ^ BitOperations.RotateRight(a, 13) ^ BitOperations.RotateRight(a, 22);
                uint maj = (a & b) ^ (a & c) ^ (b & c);
                uint t2 = S0 + maj;
                hh = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
            }
        }
        h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
    }

    /// <summary>Continues a hash whose state already covers <paramref name="prefixLen"/> bytes (a multiple of 64).</summary>
    public static void Finish(uint* h, byte* msg, int len, long prefixLen, byte* digest)
    {
        int full = len & ~63;
        for (int off = 0; off < full; off += 64) Compress(h, msg + off);
        byte* tail = stackalloc byte[128];
        int rem = len - full;
        new ReadOnlySpan<byte>(msg + full, rem).CopyTo(new Span<byte>(tail, rem));
        tail[rem] = 0x80;
        int tailLen = rem + 9 <= 64 ? 64 : 128;
        new Span<byte>(tail + rem + 1, tailLen - rem - 1).Clear();
        BinaryPrimitives.WriteUInt64BigEndian(new Span<byte>(tail + tailLen - 8, 8), (ulong)(prefixLen + len) * 8);
        Compress(h, tail);
        if (tailLen == 128) Compress(h, tail + 64);
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32BigEndian(new Span<byte>(digest + i * 4, 4), h[i]);
    }
}

static unsafe class Jwt
{
    static readonly uint* Inner = (uint*)System.Runtime.InteropServices.NativeMemory.Alloc(32);
    static readonly uint* Outer = (uint*)System.Runtime.InteropServices.NativeMemory.Alloc(32);

    public static void Init(string secret)
    {
        byte* key = stackalloc byte[64];
        new Span<byte>(key, 64).Clear();
        byte[] s = System.Text.Encoding.UTF8.GetBytes(secret);
        if (s.Length > 64)
        {
            uint* h = stackalloc uint[8];
            Sha256.Init(h);
            fixed (byte* sp = s) Sha256.Finish(h, sp, s.Length, 0, key);
        }
        else s.CopyTo(new Span<byte>(key, 64));
        byte* pad = stackalloc byte[64];
        for (int i = 0; i < 64; i++) pad[i] = (byte)(key[i] ^ 0x36);
        Sha256.Init(Inner); Sha256.Compress(Inner, pad);
        for (int i = 0; i < 64; i++) pad[i] = (byte)(key[i] ^ 0x5c);
        Sha256.Init(Outer); Sha256.Compress(Outer, pad);
    }

    static void HmacSha256(byte* msg, int len, byte* mac)
    {
        uint* h = stackalloc uint[8];
        for (int i = 0; i < 8; i++) h[i] = Inner[i];
        byte* inner = stackalloc byte[32];
        Sha256.Finish(h, msg, len, 64, inner);
        for (int i = 0; i < 8; i++) h[i] = Outer[i];
        Sha256.Finish(h, inner, 32, 64, mac);
    }

    public enum Result { Ok, Invalid, BadPayload }

    // base64url without padding. Returns decoded length or -1.
    static readonly sbyte[] B64 = MakeTable();
    static sbyte[] MakeTable()
    {
        var t = new sbyte[256];
        Array.Fill(t, (sbyte)-1);
        const string a = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        for (int i = 0; i < 64; i++) t[a[i]] = (sbyte)i;
        return t;
    }

    static int B64Decode(byte* src, int n, byte* dst, int cap)
    {
        if ((n & 3) == 1) return -1;
        int outLen = n / 4 * 3 + ((n & 3) == 0 ? 0 : (n & 3) - 1);
        if (outLen > cap) return -1;
        fixed (sbyte* t = B64)
        {
            int o = 0, i = 0;
            for (; i + 4 <= n; i += 4)
            {
                int a = t[src[i]], b = t[src[i + 1]], c = t[src[i + 2]], d = t[src[i + 3]];
                if ((a | b | c | d) < 0) return -1;
                int v = a << 18 | b << 12 | c << 6 | d;
                dst[o++] = (byte)(v >> 16); dst[o++] = (byte)(v >> 8); dst[o++] = (byte)v;
            }
            int r = n - i;
            if (r >= 2)
            {
                int a = t[src[i]], b = t[src[i + 1]];
                int c = r == 3 ? t[src[i + 2]] : 0;
                if ((a | b | c) < 0) return -1;
                int v = a << 18 | b << 12 | c << 6;
                dst[o++] = (byte)(v >> 16);
                if (r == 3) dst[o++] = (byte)(v >> 8);
            }
            return o;
        }
    }

    /// <summary>
    /// Verifies a compact JWT. On Ok, <paramref name="userId"/> is the integer sub and the username bytes
    /// (unescaped) are copied into <paramref name="nameBuf"/>.
    /// </summary>
    public static Result Verify(byte* tok, int n, long now, out long userId, byte* nameBuf, int nameCap, out int nameLen)
    {
        userId = 0; nameLen = 0;
        if (n > 4096) return Result.Invalid;
        int d1 = new ReadOnlySpan<byte>(tok, n).IndexOf((byte)'.');
        if (d1 <= 0) return Result.Invalid;
        int d2rel = new ReadOnlySpan<byte>(tok + d1 + 1, n - d1 - 1).IndexOf((byte)'.');
        if (d2rel <= 0) return Result.Invalid;
        int d2 = d1 + 1 + d2rel;
        if (n - d2 - 1 <= 0) return Result.Invalid;

        // Signature first: it's the cheap rejection for garbage and tampering.
        byte* sig = stackalloc byte[48];
        if (B64Decode(tok + d2 + 1, n - d2 - 1, sig, 48) != 32) return Result.Invalid;
        byte* mac = stackalloc byte[32];
        HmacSha256(tok, d2, mac);
        if (!new ReadOnlySpan<byte>(mac, 32).SequenceEqual(new ReadOnlySpan<byte>(sig, 32))) return Result.Invalid;

        // Header: must be a JSON object with "alg":"HS256".
        byte* buf = stackalloc byte[3072];
        byte* scratch = stackalloc byte[3072];
        int hl = B64Decode(tok, d1, buf, 3072);
        if (hl < 0) return Result.Invalid;
        var hdr = new JsonFields();
        if (!Json.ParseObject(buf, hl, ref hdr, scratch, 3072)) return Result.Invalid;
        if (!hdr.TryGetString("alg"u8, out byte* alg, out int algLen) || !new ReadOnlySpan<byte>(alg, algLen).SequenceEqual("HS256"u8))
            return Result.Invalid;

        int pl = B64Decode(tok + d1 + 1, d2rel, buf, 3072);
        if (pl < 0) return Result.Invalid;
        var pay = new JsonFields();
        if (!Json.ParseObject(buf, pl, ref pay, scratch, 3072)) return Result.Invalid;

        // exp is required: SPEC says verify it, so a token without one is rejected.
        int ei = pay.Find("exp"u8);
        if (ei < 0) return Result.Invalid;
        {
            if (pay.Kind(ei) != JsonKind.Number) return Result.Invalid;
            if (!double.TryParse(pay.Raw(ei), NumberStyles.Float, CultureInfo.InvariantCulture, out double exp)) return Result.Invalid;
            if (exp <= now) return Result.Invalid;
        }

        if (!pay.TryGetString("sub"u8, out byte* sub, out int subLen) || subLen == 0 || subLen > 15) return Result.BadPayload;
        long id = 0;
        for (int i = 0; i < subLen; i++)
        {
            uint c = (uint)(sub[i] - '0');
            if (c > 9) return Result.BadPayload;
            id = id * 10 + c;
        }
        if (id <= 0) return Result.BadPayload;
        if (!pay.TryGetString("username"u8, out byte* name, out int nl) || nl > nameCap) return Result.BadPayload;
        new ReadOnlySpan<byte>(name, nl).CopyTo(new Span<byte>(nameBuf, nl));
        userId = id; nameLen = nl;
        return Result.Ok;
    }
}
