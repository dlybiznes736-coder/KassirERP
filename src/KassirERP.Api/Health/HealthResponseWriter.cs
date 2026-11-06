using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace KassirERP.Api.Health;

/// <summary>Ответ health check в JSON: общий статус и каждая проверка с длительностью.</summary>
internal static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All), // кириллица без \uXXXX
        WriteIndented = true,
    };

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var body = new
        {
            status = report.Status.ToString(),
            durationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new
                {
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description,
                    durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
                    data = e.Value.Data,
                }),
        };

        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
    }
}
