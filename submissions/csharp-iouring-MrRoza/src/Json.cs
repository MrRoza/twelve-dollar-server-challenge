// A small strict JSON reader (validates the whole document, collects the top-level object's fields)
// and the JSON writer helpers for responses.
using System.Runtime.CompilerServices;

namespace Twelve;

enum JsonKind : byte { String, Number, Other }

unsafe struct JsonFields
{
    public const int Max = 16;
    public bool IsObject;
    public int Count;
    fixed long _keyPtr[Max];
    fixed int _keyLen[Max];
    fixed byte _kind[Max];
    fixed long _valPtr[Max];
    fixed int _valLen[Max];

    public void Add(byte* k, int kl, JsonKind kind, byte* v, int vl)
    {
        if (Count >= Max) return;
        _keyPtr[Count] = (long)k; _keyLen[Count] = kl; _kind[Count] = (byte)kind;
        _valPtr[Count] = (long)v; _valLen[Count] = vl; Count++;
    }

    /// <summary>Last field with this key (duplicate keys: last one wins, like JSON.parse).</summary>
    public int Find(ReadOnlySpan<byte> key)
    {
        for (int i = Count - 1; i >= 0; i--)
            if (new ReadOnlySpan<byte>((byte*)_keyPtr[i], _keyLen[i]).SequenceEqual(key)) return i;
        return -1;
    }

    public JsonKind Kind(int i) => (JsonKind)_kind[i];
    public ReadOnlySpan<byte> Raw(int i) => new((byte*)_valPtr[i], _valLen[i]);

    public bool TryGetString(ReadOnlySpan<byte> key, out byte* p, out int len)
    {
        int i = Find(key);
        if (i < 0 || _kind[i] != (byte)JsonKind.String) { p = null; len = 0; return false; }
        p = (byte*)_valPtr[i]; len = _valLen[i];
        return true;
    }
}

static unsafe class Json
{
    /// <summary>
    /// Validates <paramref name="p"/> as one JSON document. If it is an object, its top-level fields are collected;
    /// keys and string values are unescaped into <paramref name="scratch"/> (needs at least <paramref name="n"/> bytes).
    /// </summary>
    public static bool ParseObject(byte* p, int n, ref JsonFields f, byte* scratch, int scap)
    {
        if (scap < n) return false;
        var r = new Reader { P = p, End = p + n, S = scratch };
        r.Ws();
        if (r.P < r.End && *r.P == '{')
        {
            f.IsObject = true;
            r.P++; r.Ws();
            if (r.P < r.End && *r.P == '}') { r.P++; }
            else
            {
                while (true)
                {
                    r.Ws();
                    if (!r.String(true, out byte* k, out int kl)) return false;
                    r.Ws();
                    if (r.P >= r.End || *r.P != ':') return false;
                    r.P++; r.Ws();
                    if (r.P >= r.End) return false;
                    byte c = *r.P;
                    if (c == '"')
                    {
                        if (!r.String(true, out byte* v, out int vl)) return false;
                        f.Add(k, kl, JsonKind.String, v, vl);
                    }
                    else if (c == '-' || (uint)(c - '0') <= 9)
                    {
                        byte* s = r.P;
                        if (!r.Number()) return false;
                        f.Add(k, kl, JsonKind.Number, s, (int)(r.P - s));
                    }
                    else
                    {
                        if (!r.Value(0)) return false;
                        f.Add(k, kl, JsonKind.Other, null, 0);
                    }
                    r.Ws();
                    if (r.P >= r.End) return false;
                    if (*r.P == ',') { r.P++; continue; }
                    if (*r.P == '}') { r.P++; break; }
                    return false;
                }
            }
        }
        else if (!r.Value(0)) return false;
        r.Ws();
        return r.P == r.End;
    }

    struct Reader
    {
        public byte* P, End, S;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Ws() { while (P < End && (*P == ' ' || *P == '\t' || *P == '\n' || *P == '\r')) P++; }

        public bool Value(int depth)
        {
            if (depth > 64 || P >= End) return false;
            byte c = *P;
            switch (c)
            {
                case (byte)'"': return String(false, out _, out _);
                case (byte)'{':
                    P++; Ws();
                    if (P < End && *P == '}') { P++; return true; }
                    while (true)
                    {
                        Ws();
                        if (!String(false, out _, out _)) return false;
                        Ws();
                        if (P >= End || *P != ':') return false;
                        P++; Ws();
                        if (!Value(depth + 1)) return false;
                        Ws();
                        if (P >= End) return false;
                        if (*P == ',') { P++; continue; }
                        if (*P == '}') { P++; return true; }
                        return false;
                    }
                case (byte)'[':
                    P++; Ws();
                    if (P < End && *P == ']') { P++; return true; }
                    while (true)
                    {
                        Ws();
                        if (!Value(depth + 1)) return false;
                        Ws();
                        if (P >= End) return false;
                        if (*P == ',') { P++; continue; }
                        if (*P == ']') { P++; return true; }
                        return false;
                    }
                case (byte)'t': return Lit("true"u8);
                case (byte)'f': return Lit("false"u8);
                case (byte)'n': return Lit("null"u8);
                default: return Number();
            }
        }

        bool Lit(ReadOnlySpan<byte> w)
        {
            if (End - P < w.Length || !new ReadOnlySpan<byte>(P, w.Length).SequenceEqual(w)) return false;
            P += w.Length; return true;
        }

        public bool Number()
        {
            if (P < End && *P == '-') P++;
            if (P >= End) return false;
            if (*P == '0') P++;
            else if ((uint)(*P - '1') <= 8) { while (P < End && (uint)(*P - '0') <= 9) P++; }
            else return false;
            if (P < End && *P == '.')
            {
                P++;
                if (P >= End || (uint)(*P - '0') > 9) return false;
                while (P < End && (uint)(*P - '0') <= 9) P++;
            }
            if (P < End && (*P == 'e' || *P == 'E'))
            {
                P++;
                if (P < End && (*P == '+' || *P == '-')) P++;
                if (P >= End || (uint)(*P - '0') > 9) return false;
                while (P < End && (uint)(*P - '0') <= 9) P++;
            }
            return true;
        }

        static int Hex(byte c)
        {
            uint d = (uint)(c - '0'); if (d <= 9) return (int)d;
            d = (uint)((c | 0x20) - 'a'); if (d <= 5) return (int)d + 10;
            return -1;
        }

        bool Hex4(out int v)
        {
            v = 0;
            if (End - P < 4) return false;
            for (int i = 0; i < 4; i++) { int h = Hex(P[i]); if (h < 0) return false; v = v << 4 | h; }
            P += 4; return true;
        }

        /// <summary>Parses a string at P. When <paramref name="store"/>, unescapes it into scratch.</summary>
        public bool String(bool store, out byte* outP, out int outLen)
        {
            outP = S; outLen = 0;
            if (P >= End || *P != '"') return false;
            P++;
            byte* o = S;
            while (true)
            {
                // Fast run of plain bytes.
                byte* run = P;
                while (P < End && *P != '"' && *P != '\\' && *P >= 0x20) P++;
                if (store && P > run) { long len = P - run; Buffer.MemoryCopy(run, o, len, len); o += len; }
                if (P >= End || *P < 0x20) return false;
                if (*P == '"') { P++; break; }
                // Escape.
                P++;
                if (P >= End) return false;
                byte e = *P++;
                int cp;
                switch (e)
                {
                    case (byte)'"': cp = '"'; break;
                    case (byte)'\\': cp = '\\'; break;
                    case (byte)'/': cp = '/'; break;
                    case (byte)'b': cp = 8; break;
                    case (byte)'f': cp = 12; break;
                    case (byte)'n': cp = 10; break;
                    case (byte)'r': cp = 13; break;
                    case (byte)'t': cp = 9; break;
                    case (byte)'u':
                        if (!Hex4(out cp)) return false;
                        if (cp >= 0xD800 && cp <= 0xDBFF && End - P >= 6 && P[0] == '\\' && P[1] == 'u')
                        {
                            byte* save = P; P += 2;
                            if (Hex4(out int lo) && lo >= 0xDC00 && lo <= 0xDFFF) cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                            else P = save;
                        }
                        break;
                    default: return false;
                }
                if (store) o = Utf8(o, cp);
            }
            if (store) { outLen = (int)(o - S); S = o; }
            return true;
        }

        static byte* Utf8(byte* o, int cp)
        {
            if (cp < 0x80) { *o++ = (byte)cp; }
            else if (cp < 0x800) { *o++ = (byte)(0xC0 | cp >> 6); *o++ = (byte)(0x80 | (cp & 0x3F)); }
            else if (cp < 0x10000) { *o++ = (byte)(0xE0 | cp >> 12); *o++ = (byte)(0x80 | (cp >> 6 & 0x3F)); *o++ = (byte)(0x80 | (cp & 0x3F)); }
            else { *o++ = (byte)(0xF0 | cp >> 18); *o++ = (byte)(0x80 | (cp >> 12 & 0x3F)); *o++ = (byte)(0x80 | (cp >> 6 & 0x3F)); *o++ = (byte)(0x80 | (cp & 0x3F)); }
            return o;
        }
    }

    /// <summary>Trims whitespace the way JavaScript's String.prototype.trim does (ASCII + Unicode spaces).</summary>
    public static void Trim(ref byte* p, ref int n)
    {
        while (n > 0)
        {
            int w = WsLenAt(p, n);
            if (w == 0) break;
            p += w; n -= w;
        }
        while (n > 0)
        {
            int w = WsLenBefore(p, n);
            if (w == 0) break;
            n -= w;
        }
    }

    static bool IsWsCp(int cp) => cp is 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x20 or 0xA0 or 0x1680
        or (>= 0x2000 and <= 0x200A) or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000 or 0xFEFF;

    static int WsLenAt(byte* p, int n)
    {
        byte c = p[0];
        if (c < 0x80) return IsWsCp(c) ? 1 : 0;
        if ((c & 0xE0) == 0xC0 && n >= 2) return IsWsCp((c & 0x1F) << 6 | (p[1] & 0x3F)) ? 2 : 0;
        if ((c & 0xF0) == 0xE0 && n >= 3) return IsWsCp((c & 0x0F) << 12 | (p[1] & 0x3F) << 6 | (p[2] & 0x3F)) ? 3 : 0;
        return 0;
    }

    static int WsLenBefore(byte* p, int n)
    {
        byte c = p[n - 1];
        if (c < 0x80) return IsWsCp(c) ? 1 : 0;
        if (n >= 2 && (p[n - 2] & 0xE0) == 0xC0) return WsLenAt(p + n - 2, 2) == 2 ? 2 : 0;
        if (n >= 3 && (p[n - 3] & 0xF0) == 0xE0) return WsLenAt(p + n - 3, 3) == 3 ? 3 : 0;
        return 0;
    }

    /// <summary>Number of code points in UTF-8 text (what SQLite's length() counts).</summary>
    public static int CodePoints(byte* p, int n)
    {
        int count = 0;
        for (int i = 0; i < n; i++) if ((p[i] & 0xC0) != 0x80) count++;
        return count;
    }
}
