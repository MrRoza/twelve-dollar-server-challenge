// The response writer. The body is written at offset HeaderRoom of the connection's output buffer; End()
// then writes the status line and headers right-aligned in front of it, so nothing is ever copied.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Twelve;

static unsafe class Out
{
    public const int HeaderRoom = 128;
    static Conn* _c;
    static byte* _p, _end;

    public const int PoolSize = 16384;
    public static BufPool Pool = null!;

    public static void Begin(Conn* c)
    {
        if (c->Out == null) { c->Out = Pool.Take(); c->OutCap = PoolSize; }
        _c = c;
        _p = c->Out + HeaderRoom;
        _end = c->Out + c->OutCap;
    }

    /// <summary>Write position as an offset into the connection's buffer (stable across Grow).</summary>
    public static int Pos
    {
        get => (int)(_p - _c->Out);
        set => _p = _c->Out + value;
    }

    public static byte* At(int off) => _c->Out + off;

    static void Grow(int need)
    {
        int used = (int)(_p - _c->Out);
        int cap = _c->OutCap;
        while (cap - used < need) cap *= 2;
        if (_c->OutCap == PoolSize)
        {
            byte* nb = (byte*)NativeMemory.Alloc((nuint)cap);
            Buffer.MemoryCopy(_c->Out, nb, cap, used);
            Pool.Return(_c->Out);
            _c->Out = nb;
        }
        else _c->Out = (byte*)NativeMemory.Realloc(_c->Out, (nuint)cap);
        _c->OutCap = cap;
        _p = _c->Out + used;
        _end = _c->Out + cap;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Raw(ReadOnlySpan<byte> s)
    {
        if (_end - _p < s.Length) Grow(s.Length);
        s.CopyTo(new Span<byte>(_p, s.Length));
        _p += s.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Raw(byte* s, int n) => Raw(new ReadOnlySpan<byte>(s, n));

    public static void Int(long v)
    {
        if (_end - _p < 21) Grow(21);
        if (v < 0) { *_p++ = (byte)'-'; v = -v; }
        byte* tmp = stackalloc byte[20];
        int i = 20;
        do { tmp[--i] = (byte)('0' + v % 10); v /= 10; } while (v != 0);
        int len = 20 - i;
        Buffer.MemoryCopy(tmp + i, _p, len, len);
        _p += len;
    }

    static ReadOnlySpan<byte> Hex => "0123456789abcdef"u8;

    /// <summary>Writes a JSON string literal (with quotes), escaping quote, backslash and control characters.</summary>
    public static void Str(byte* s, int n)
    {
        if (_end - _p < n * 6 + 2) Grow(n * 6 + 2);
        _p = Escape(_p, s, n);
    }

    static readonly System.Buffers.SearchValues<byte> NeedsEscape = System.Buffers.SearchValues.Create(
        "\0\x01\x02\x03\x04\x05\x06\x07\x08\x09\x0a\x0b\x0c\x0d\x0e\x0f\x10\x11\x12\x13\x14\x15\x16\x17\x18\x19\x1a\x1b\x1c\x1d\x1e\x1f\"\\"u8);

    /// <summary>
    /// Writes <paramref name="s"/> as a JSON string literal at <paramref name="o"/> (room for n*6+2 bytes needed)
    /// and returns the end. One vectorized scan finds the next byte to escape.
    /// </summary>
    public static byte* Escape(byte* o, byte* s, int n)
    {
        *o++ = (byte)'"';
        var span = new ReadOnlySpan<byte>(s, n);
        while (true)
        {
            int k = span.IndexOfAny(NeedsEscape);
            int q = k < 0 ? span.Length : k;
            span.Slice(0, q).CopyTo(new Span<byte>(o, q));
            o += q;
            if (k < 0) break;
            byte c = span[q];
            *o++ = (byte)'\\';
            switch (c)
            {
                case (byte)'"': *o++ = (byte)'"'; break;
                case (byte)'\\': *o++ = (byte)'\\'; break;
                case (byte)'\n': *o++ = (byte)'n'; break;
                case (byte)'\r': *o++ = (byte)'r'; break;
                case (byte)'\t': *o++ = (byte)'t'; break;
                case 8: *o++ = (byte)'b'; break;
                case 12: *o++ = (byte)'f'; break;
                default:
                    *o++ = (byte)'u'; *o++ = (byte)'0'; *o++ = (byte)'0';
                    *o++ = Hex[c >> 4]; *o++ = Hex[c & 15];
                    break;
            }
            span = span.Slice(q + 1);
        }
        *o++ = (byte)'"';
        return o;
    }

    /// <summary>Writes a non-negative integer at <paramref name="o"/> (room for 20 bytes) and returns the end.</summary>
    public static byte* IntTo(byte* o, long v)
    {
        if (v < 10) { *o = (byte)('0' + v); return o + 1; }
        byte* tmp = stackalloc byte[20];
        int i = 20;
        do { tmp[--i] = (byte)('0' + v % 10); v /= 10; } while (v != 0);
        int len = 20 - i;
        Buffer.MemoryCopy(tmp + i, o, len, len);
        return o + len;
    }

    public static void Error(int status, ReadOnlySpan<byte> msg)
    {
        Raw("{\"error\":\""u8); Raw(msg); Raw("\"}"u8);
        End(status);
    }

    /// <summary>Finishes the response: status line + headers in front of the body. Sets OutStart/OutEnd.</summary>
    public static void End(int status)
    {
        int bodyLen = (int)(_p - (_c->Out + HeaderRoom));
        ReadOnlySpan<byte> line = status switch
        {
            200 => "HTTP/1.1 200 OK\r\n"u8,
            201 => "HTTP/1.1 201 Created\r\n"u8,
            400 => "HTTP/1.1 400 Bad Request\r\n"u8,
            401 => "HTTP/1.1 401 Unauthorized\r\n"u8,
            404 => "HTTP/1.1 404 Not Found\r\n"u8,
            503 => "HTTP/1.1 503 Service Unavailable\r\n"u8,
            _ => "HTTP/1.1 500 Internal Server Error\r\n"u8,
        };
        ReadOnlySpan<byte> h1 = "Content-Type: application/json\r\nContent-Length: "u8;
        byte* num = stackalloc byte[12];
        int ni = 12, v = bodyLen;
        do { num[--ni] = (byte)('0' + v % 10); v /= 10; } while (v != 0);
        ReadOnlySpan<byte> close = _c->CloseAfter != 0 ? "\r\nConnection: close\r\n\r\n"u8 : "\r\n\r\n"u8;
        int total = line.Length + h1.Length + (12 - ni) + close.Length;
        byte* start = _c->Out + HeaderRoom - total;
        byte* o = start;
        line.CopyTo(new Span<byte>(o, line.Length)); o += line.Length;
        h1.CopyTo(new Span<byte>(o, h1.Length)); o += h1.Length;
        Buffer.MemoryCopy(num + ni, o, 12 - ni, 12 - ni); o += 12 - ni;
        close.CopyTo(new Span<byte>(o, close.Length));
        _c->OutStart = (int)(start - _c->Out);
        _c->OutEnd = (int)(_p - _c->Out);
    }
}
