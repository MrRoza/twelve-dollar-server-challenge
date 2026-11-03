// The five endpoints. Each handler writes the complete response through Out.
using System.Runtime.InteropServices;
using System.Text;

namespace Twelve;

unsafe sealed partial class Server
{
    void Handle(Conn* c)
    {
        Out.Begin(c);
        switch (c->Route)
        {
            case Route.Health: Health(); break;
            case Route.Feed:
                if (_db.Feed()) Out.End(200);
                else { Out.Begin(c); Out.Error(500, "internal server error"u8); }
                break;
            case Route.GetPost: GetPost(c); break;
            case Route.CreatePost: CreatePost(c); break;
            case Route.Like: Like(c); break;
            default: Out.Error(404, "not found"u8); break;
        }
    }

    void Health()
    {
        string? err = _db.Health();
        if (err == null)
        {
            Out.Raw("{\"status\":\"ok\",\"db\":\"ok\",\"uptime_s\":"u8);
            Out.Int((Environment.TickCount64 - _startTicks) / 1000);
            Out.Raw("}"u8);
            Out.End(200);
        }
        else
        {
            byte[] e = System.Text.Encoding.UTF8.GetBytes(err);
            Out.Raw("{\"status\":\"degraded\",\"db\":\"unreachable\",\"error\":"u8);
            fixed (byte* ep = e) Out.Str(ep, e.Length);
            Out.Raw("}"u8);
            Out.End(503);
        }
    }

    /// <summary>Positive integer id, digits only, at most 2^53. -1 if invalid.</summary>
    static long ParseId(byte* s, int n)
    {
        if (n <= 0 || n > 16) return -1;
        long v = 0;
        for (int i = 0; i < n; i++)
        {
            uint d = (uint)(s[i] - '0');
            if (d > 9) return -1;
            v = v * 10 + d;
        }
        return v > 0 && v <= 1L << 53 ? v : -1;
    }

    void GetPost(Conn* c)
    {
        long id = ParseId(c->In + c->SegOff, c->SegLen);
        if (id < 0) { Out.Error(400, "invalid post id"u8); return; }
        int r = _db.Post(id);
        if (r > 0) Out.End(200);
        else { Out.Begin(c); if (r == 0) Out.Error(404, "post not found"u8); else Out.Error(500, "internal server error"u8); }
    }

    /// <summary>Checks the bearer token. Writes the 401 and returns false when it fails.</summary>
    bool Auth(Conn* c, out long userId, out int nameLen)
    {
        userId = 0; nameLen = 0;
        byte* a = c->In + c->AuthOff;
        if (c->AuthOff < 0 || c->AuthLen < 7 || !new ReadOnlySpan<byte>(a, 7).SequenceEqual("Bearer "u8))
        {
            Out.Error(401, "missing bearer token"u8);
            return false;
        }
        switch (Jwt.Verify(a + 7, c->AuthLen - 7, _now, out userId, _name, 1024, out nameLen))
        {
            case Jwt.Result.Ok: return true;
            case Jwt.Result.BadPayload: Out.Error(401, "invalid token payload"u8); return false;
            default: Out.Error(401, "invalid or expired token"u8); return false;
        }
    }

    void CreatePost(Conn* c)
    {
        if (!Auth(c, out long userId, out int nameLen)) return;
        var f = new JsonFields();
        if (!Json.ParseObject(c->In + c->BodyOff, c->BodyLen, ref f, _scratch, InMax))
        {
            Out.Error(400, "malformed JSON body"u8);
            return;
        }
        if (!f.TryGetString("body"u8, out byte* body, out int bodyLen)) { Out.Error(400, "body is required"u8); return; }
        Json.Trim(ref body, ref bodyLen);
        if (bodyLen == 0) { Out.Error(400, "body is required"u8); return; }
        if (Json.CodePoints(body, bodyLen) > 500) { Out.Error(400, "body must be at most 500 characters"u8); return; }
        if (_db.InsertPost(userId, body, bodyLen, _name, nameLen)) Out.End(201);
        else { Out.Begin(c); Out.Error(500, "internal server error"u8); }
    }

    void Like(Conn* c)
    {
        if (!Auth(c, out long userId, out _)) return;
        long id = ParseId(c->In + c->SegOff, c->SegLen);
        if (id < 0) { Out.Error(400, "invalid post id"u8); return; }
        int r = _db.Like(userId, id);
        switch (r)
        {
            case 0: Out.Error(404, "post not found"u8); break;
            case 1: Out.Raw("{\"liked\":true,\"already_liked\":false,\"post_id\":"u8); Out.Int(id); Out.Raw("}"u8); Out.End(201); break;
            case 2: Out.Raw("{\"liked\":true,\"already_liked\":true,\"post_id\":"u8); Out.Int(id); Out.Raw("}"u8); Out.End(200); break;
            default: Out.Error(500, "internal server error"u8); break;
        }
    }
}
