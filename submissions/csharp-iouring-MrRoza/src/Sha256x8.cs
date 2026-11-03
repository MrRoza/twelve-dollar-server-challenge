// Eight HMAC-SHA256s at once with AVX2 (one message per 32-bit lane). The write stage collects a batch's
// tokens and computes their signatures together; each token's signature is still computed from its own bytes
// and compared in Jwt.Verify. A startup self-test against the scalar code switches this off on any mismatch.
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Twelve;

static unsafe class Sha256x8
{
    public const int Lanes = 8, MaxMsg = 1024;
    public static bool On;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector256<uint> Rotr(Vector256<uint> x, int n) => (x >>> n) | (x << (32 - n));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Be(byte* p) => BinaryPrimitives.ReverseEndianness(*(uint*)p);

    /// <summary>One SHA-256 block per lane; lanes whose bit in <paramref name="mask"/> is clear keep their state.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void Compress(Vector256<uint>* h, byte** blk, Vector256<uint> mask)
    {
        Vector256<uint>* w = stackalloc Vector256<uint>[64];
        byte* b0 = blk[0], b1 = blk[1], b2 = blk[2], b3 = blk[3], b4 = blk[4], b5 = blk[5], b6 = blk[6], b7 = blk[7];
        for (int i = 0; i < 16; i++)
        {
            int o = i * 4;
            w[i] = Vector256.Create(Be(b0 + o), Be(b1 + o), Be(b2 + o), Be(b3 + o), Be(b4 + o), Be(b5 + o), Be(b6 + o), Be(b7 + o));
        }
        for (int i = 16; i < 64; i++)
        {
            Vector256<uint> x = w[i - 15], y = w[i - 2];
            Vector256<uint> s0 = Rotr(x, 7) ^ Rotr(x, 18) ^ (x >>> 3);
            Vector256<uint> s1 = Rotr(y, 17) ^ Rotr(y, 19) ^ (y >>> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }
        Vector256<uint> a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
        fixed (uint* k = Sha256.K)
        {
            for (int i = 0; i < 64; i++)
            {
                Vector256<uint> S1 = Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25);
                Vector256<uint> ch = g ^ (e & (f ^ g));
                Vector256<uint> t1 = hh + S1 + ch + Vector256.Create(k[i]) + w[i];
                Vector256<uint> S0 = Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22);
                Vector256<uint> maj = (a & b) | (c & (a | b));
                hh = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + S0 + maj;
            }
        }
        h[0] = Vector256.ConditionalSelect(mask, h[0] + a, h[0]);
        h[1] = Vector256.ConditionalSelect(mask, h[1] + b, h[1]);
        h[2] = Vector256.ConditionalSelect(mask, h[2] + c, h[2]);
        h[3] = Vector256.ConditionalSelect(mask, h[3] + d, h[3]);
        h[4] = Vector256.ConditionalSelect(mask, h[4] + e, h[4]);
        h[5] = Vector256.ConditionalSelect(mask, h[5] + f, h[5]);
        h[6] = Vector256.ConditionalSelect(mask, h[6] + g, h[6]);
        h[7] = Vector256.ConditionalSelect(mask, h[7] + hh, h[7]);
    }

    /// <summary>
    /// HMAC-SHA256 of up to 8 messages (each at most MaxMsg bytes), keyed by the precomputed inner/outer pad
    /// states. Writes 32 bytes per message to <paramref name="macs"/>.
    /// </summary>
    public static void Hmac(byte** msg, int* len, int n, uint* inner, uint* outer, byte* macs)
    {
        // Per lane: its full 64-byte blocks are read in place; the padded tail (1 or 2 blocks) goes in tails.
        byte* tails = stackalloc byte[Lanes * 128];
        int* full = stackalloc int[Lanes];
        int* blocks = stackalloc int[Lanes];
        int maxBlocks = 0;
        for (int l = 0; l < Lanes; l++)
        {
            int m = l < n ? l : 0; // idle lanes repeat lane 0 and are masked off
            int ln = len[m];
            int f = ln >> 6, rem = ln - (f << 6);
            byte* t = tails + l * 128;
            new Span<byte>(t, 128).Clear();
            new ReadOnlySpan<byte>(msg[m] + (f << 6), rem).CopyTo(new Span<byte>(t, rem));
            t[rem] = 0x80;
            int tailLen = rem + 9 <= 64 ? 64 : 128;
            BinaryPrimitives.WriteUInt64BigEndian(new Span<byte>(t + tailLen - 8, 8), (ulong)(64 + ln) * 8);
            full[l] = f;
            blocks[l] = f + tailLen / 64;
            if (l < n && blocks[l] > maxBlocks) maxBlocks = blocks[l];
        }

        Vector256<uint>* h = stackalloc Vector256<uint>[8];
        for (int j = 0; j < 8; j++) h[j] = Vector256.Create(inner[j]);
        byte** blk = stackalloc byte*[Lanes];
        uint* m32 = stackalloc uint[Lanes];
        for (int k = 0; k < maxBlocks; k++)
        {
            for (int l = 0; l < Lanes; l++)
            {
                int src = l < n ? l : 0;
                bool active = l < n && k < blocks[l];
                int kk = k < blocks[l] ? k : blocks[l] - 1; // keep the pointer valid on idle lanes
                blk[l] = kk < full[l] ? msg[src] + (kk << 6) : tails + l * 128 + ((kk - full[l]) << 6);
                m32[l] = active ? 0xFFFFFFFFu : 0;
            }
            Compress(h, blk, Vector256.Load(m32));
        }

        // Outer hash: one block per lane: the inner digest, padding, and the length (64 + 32 bytes).
        byte* ob = stackalloc byte[Lanes * 64];
        for (int l = 0; l < Lanes; l++)
        {
            byte* o = ob + l * 64;
            for (int j = 0; j < 8; j++) BinaryPrimitives.WriteUInt32BigEndian(new Span<byte>(o + j * 4, 4), h[j].GetElement(l));
            o[32] = 0x80;
            new Span<byte>(o + 33, 23).Clear();
            BinaryPrimitives.WriteUInt64BigEndian(new Span<byte>(o + 56, 8), (64 + 32) * 8);
            blk[l] = o;
            m32[l] = l < n ? 0xFFFFFFFFu : 0;
        }
        for (int j = 0; j < 8; j++) h[j] = Vector256.Create(outer[j]);
        Compress(h, blk, Vector256.Load(m32));
        for (int l = 0; l < n; l++)
            for (int j = 0; j < 8; j++)
                BinaryPrimitives.WriteUInt32BigEndian(new Span<byte>(macs + l * 32 + j * 4, 4), h[j].GetElement(l));
    }
}
