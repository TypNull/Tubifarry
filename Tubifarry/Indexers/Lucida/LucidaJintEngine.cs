using Jint;

namespace Tubifarry.Indexers.Lucida
{
    public static class LucidaJintEngine
    {
        public static Engine Create() => new(opts => opts
            .TimeoutInterval(TimeSpan.FromSeconds(5))
            .LimitMemory(50_000_000)
            .LimitRecursion(64)
            .MaxStatements(1_000_000));
    }
}
