using System.Security.Claims;
using Microsoft.AspNetCore.Diagnostics;

namespace AcademicRepository.Services;

public sealed class SafeExceptionHandler(ILogger<SafeExceptionHandler> logger) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var roles = httpContext.User.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).Distinct().ToArray();
        logger.LogError(exception,
            "Unhandled MVC request. CorrelationId={CorrelationId} UserId={UserId} Roles={Roles} RequestPath={RequestPath} Action={Action} TimestampUtc={TimestampUtc}",
            httpContext.TraceIdentifier, httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), roles,
            httpContext.Request.Path.Value, httpContext.GetRouteData().Values.TryGetValue("action", out var action) ? action : null, DateTimeOffset.UtcNow);
        return ValueTask.FromResult(false);
    }
}
