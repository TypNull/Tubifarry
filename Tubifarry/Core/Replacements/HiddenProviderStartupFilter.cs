using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tubifarry.Core.Replacements
{
    public sealed class HiddenProviderStartupFilter : IStartupFilter
    {
        private static readonly HashSet<string> HiddenImplementations = new(StringComparer.OrdinalIgnoreCase)
        {
            "DABMusicIndexer",
            "TripleTripleIndexer",
            "TubifarryIndexer",
            "DABMusicClient",
            "TripleTripleClient"
        };

        private static readonly string[] SchemaPaths = ["/api/v1/indexer/schema", "/api/v1/downloadclient/schema"];

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(FilterSchemaAsync);
            next(app);
        };

        private static async Task FilterSchemaAsync(HttpContext context, Func<Task> next)
        {
            if (!HttpMethods.IsGet(context.Request.Method) || !IsSchemaPath(context.Request.Path))
            {
                await next();
                return;
            }

            context.Request.Headers.Remove("Accept-Encoding");
            Stream originalBody = context.Response.Body;
            using MemoryStream buffer = new();
            context.Response.Body = buffer;

            try
            {
                await next();
            }
            finally
            {
                context.Response.Body = originalBody;
            }

            byte[] body = buffer.ToArray();
            byte[]? filtered = CanFilter(context.Response) ? TryFilter(body) : null;
            if (filtered == null)
            {
                await originalBody.WriteAsync(body);
                return;
            }

            context.Response.ContentLength = filtered.Length;
            await originalBody.WriteAsync(filtered);
        }

        private static bool CanFilter(HttpResponse response) =>
            response.StatusCode == StatusCodes.Status200OK && !response.HasStarted && !response.Headers.ContainsKey("Content-Encoding");

        private static byte[]? TryFilter(byte[] body)
        {
            try
            {
                if (JsonNode.Parse(body) is not JsonArray providers)
                    return null;

                List<JsonNode?> hidden = providers.Where(IsHidden).ToList();
                if (hidden.Count == 0)
                    return null;

                foreach (JsonNode? provider in hidden)
                    providers.Remove(provider);

                return JsonSerializer.SerializeToUtf8Bytes(providers);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsSchemaPath(PathString path) =>
            path.Value?.TrimEnd('/') is string value && SchemaPaths.Any(schemaPath => value.EndsWith(schemaPath, StringComparison.OrdinalIgnoreCase));

        private static bool IsHidden(JsonNode? provider) =>
            provider is JsonObject entry && entry["implementation"] is JsonValue value && value.TryGetValue(out string? implementation) && HiddenImplementations.Contains(implementation);
    }
}
