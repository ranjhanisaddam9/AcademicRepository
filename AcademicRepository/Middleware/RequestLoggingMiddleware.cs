using System.Diagnostics;
using System.Security.Claims;

namespace AcademicRepository.Middleware;

public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger,
    IConfiguration configuration, IWebHostEnvironment environment)
{
    private readonly double slowRequestThresholdMs = Math.Clamp(configuration.GetValue("Logging:SlowRequestThresholdMs", 2000), 100, 120_000);

    public async Task InvokeAsync(HttpContext context)
    {
        var action = context.GetRouteData().Values.TryGetValue("action", out var value) ? value?.ToString() : null;
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var roles = string.Join(",", context.User.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct());
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["TimestampUtc"] = DateTimeOffset.UtcNow,
            ["CorrelationId"] = context.TraceIdentifier,
            ["RequestPath"] = context.Request.Path.Value,
            ["UserId"] = userId,
            ["Role"] = roles,
            ["Action"] = action
        });
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            var result = context.Response.StatusCode;
            if (environment.IsDevelopment())
                logger.LogDebug("HTTP request completed. Action={Action} Result={Result} DurationMs={DurationMs}", action, result, stopwatch.Elapsed.TotalMilliseconds);
            else if (stopwatch.Elapsed.TotalMilliseconds >= slowRequestThresholdMs)
                logger.LogWarning("Slow HTTP request. Action={Action} Result={Result} DurationMs={DurationMs} ThresholdMs={ThresholdMs}",
                    action, result, stopwatch.Elapsed.TotalMilliseconds, slowRequestThresholdMs);
        }
    }
}
