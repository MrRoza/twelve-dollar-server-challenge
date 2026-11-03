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

    public static void Begin(Conn* c)
    {
        if (c->Out == null) { c->OutCap = 16384; c->Out = (byte*)NativeMemory.Alloc((nuint)c->OutCap); }
        _c = c;
        _p = c->Out + HeaderRoom;
        _end = c->Out + c->OutCap;
    }

    static void Grow(int need)
    {
        int used = (int)(_p - _c->Out);
        int cap = _c->OutCap;
        while (cap - used < need) cap *= 2;
        _c->Out = (byte*)NativeMemory.Realloc(_c->Out, (nuint)cap);
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
        byte* o = _p;
        *o++ = (byte)'"';
        var span = new ReadOnlySpan<byte>(s, n);
        while (true)
        {
            int k = span.IndexOfAnyInRange((byte)0, (byte)0x1F);
            int q = k < 0 ? span.Length : k;
            int k2 = span.Slice(0, q).IndexOfAny((byte)'"', (byte)'\\');
            if (k2 >= 0) q = k2;
            fixed (byte* sp = span) Buffer.MemoryCopy(sp, o, q, q);
            o += q;
            if (q == span.Length) break;
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
        _p = o;
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
