using NzbDrone.Common.Http;
using System.Net;

namespace Tubifarry.Indexers.Lucida
{
    public static class LucidaRetryPolicy
    {
        public const int MaxAttempts = 4;

        public static Task DelayAsync(int attempt) => Task.Delay(TimeSpan.FromSeconds(3 * attempt));

        public static bool IsLastAttempt(int attempt) => attempt >= MaxAttempts - 1;

        public static bool IsTransient(HttpStatusCode statusCode)
            => statusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.NotFound or HttpStatusCode.Gone);

        public static bool IsTransient(Exception ex) => ex switch
        {
            HttpException httpEx => IsTransient(httpEx.Response.StatusCode),
            HttpRequestException { StatusCode: HttpStatusCode statusCode } => IsTransient(statusCode),
            _ => true
        };
    }
}
