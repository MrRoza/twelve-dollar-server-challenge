// HTTP/1.1 request parsing (in place, no copies).
using System.Runtime.InteropServices;
using System.Text;

namespace Twelve;

static unsafe class Http
{
    /// <summary>1 = complete request parsed into <paramref name="c"/>, 0 = need more bytes, -1 = garbage.</summary>
    public static int TryParse(Conn* c)
    {
        byte* p = c->In;
        int n = c->InLen;
        var span = new ReadOnlySpan<byte>(p, n);
        int hdrEnd = span.IndexOf("\r\n\r\n"u8);
        if (hdrEnd < 0) return n >= 65536 ? -1 : 0;

        // Request line: METHOD SP target SP version
        int lineEnd = span.Slice(0, hdrEnd + 2).IndexOf("\r\n"u8);
        var line = span.Slice(0, lineEnd);
        int sp1 = line.IndexOf((byte)' ');
        if (sp1 <= 0) return -1;
        int sp2rel = line.Slice(sp1 + 1).IndexOf((byte)' ');
        if (sp2rel <= 0) return -1;
        int pathOff = sp1 + 1, pathLen = sp2rel;
        int q = line.Slice(pathOff, pathLen).IndexOf((byte)'?');
        if (q >= 0) pathLen = q;
        bool isGet = sp1 == 3 && line[0] == 'G' && line[1] == 'E' && line[2] == 'T';
        bool isPost = sp1 == 4 && line[0] == 'P' && line[1] == 'O' && line[2] == 'S' && line[3] == 'T';
        bool http10 = line.Slice(pathOff + sp2rel + 1).SequenceEqual("HTTP/1.0"u8);

        // Headers
        long contentLength = 0;
        c->AuthOff = -1; c->AuthLen = 0;
        c->CloseAfter = (byte)(http10 ? 1 : 0);
        int pos = lineEnd + 2;
        while (pos < hdrEnd + 2)
        {
            int eol = span.Slice(pos).IndexOf("\r\n"u8) + pos;
            var h = span.Slice(pos, eol - pos);
            int colon = h.IndexOf((byte)':');
            if (colon > 0)
            {
                var name = h.Slice(0, colon);
                int vs = colon + 1;
                while (vs < h.Length && (h[vs] == ' ' || h[vs] == '\t')) vs++;
                int ve = h.Length;
                while (ve > vs && (h[ve - 1] == ' ' || h[ve - 1] == '\t')) ve--;
                var val = h.Slice(vs, ve - vs);
                if (name.Length == 14 && Ascii.EqualsIgnoreCase(name, "content-length"u8))
                {
                    contentLength = 0;
                    foreach (byte b in val)
                    {
                        uint d = (uint)(b - '0');
                        if (d > 9 || contentLength > 1 << 20) return -1;
                        contentLength = contentLength * 10 + d;
                    }
                }
                else if (name.Length == 13 && Ascii.EqualsIgnoreCase(name, "authorization"u8))
                {
                    c->AuthOff = pos + vs; c->AuthLen = ve - vs;
                }
                else if (name.Length == 10 && Ascii.EqualsIgnoreCase(name, "connection"u8))
                {
                    if (Ascii.EqualsIgnoreCase(val, "close"u8)) c->CloseAfter = 1;
                    else if (Ascii.EqualsIgnoreCase(val, "keep-alive"u8)) c->CloseAfter = 0;
                }
            }
            pos = eol + 2;
        }

        int bodyOff = hdrEnd + 4;
        if (contentLength > 65536 - bodyOff) return -1;
        if (n < bodyOff + contentLength) return 0;
        c->BodyOff = bodyOff; c->BodyLen = (int)contentLength;
        c->ReqLen = bodyOff + (int)contentLength;

        // Route
        var path = span.Slice(pathOff, pathLen);
        c->Route = Route.NotFound;
        if (isGet)
        {
            if (path.SequenceEqual("/feed"u8)) c->Route = Route.Feed;
            else if (path.SequenceEqual("/health"u8)) c->Route = Route.Health;
            else if (path.StartsWith("/posts/"u8))
            {
                var seg = path.Slice(7);
                if (seg.Length > 0 && seg.IndexOf((byte)'/') < 0) { c->Route = Route.GetPost; c->SegOff = pathOff + 7; c->SegLen = seg.Length; }
            }
        }
        else if (isPost)
        {
            if (path.SequenceEqual("/posts"u8)) c->Route = Route.CreatePost;
            else if (path.StartsWith("/posts/"u8) && path.EndsWith("/like"u8) && path.Length > 12)
            {
                int segLen = path.Length - 12;
                var seg = path.Slice(7, segLen);
                if (seg.IndexOf((byte)'/') < 0) { c->Route = Route.Like; c->SegOff = pathOff + 7; c->SegLen = segLen; }
            }
        }
        return 1;
    }
}
