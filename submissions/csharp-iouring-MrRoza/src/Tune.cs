// Tunables. In the submission build they are compile-time defaults. Only a dev build (DEV=1 bash build.sh,
// DEV_TUNABLES) reads TWELVE_<name> environment variables, for experiments. start.sh never sets any.
namespace Twelve;

static class Tune
{
    public static long Get(string name, long dflt)
    {
#if DEV_TUNABLES
        string? v = Environment.GetEnvironmentVariable("TWELVE_" + name);
        if (v != null) return long.Parse(v);
#endif
        return dflt;
    }
}
