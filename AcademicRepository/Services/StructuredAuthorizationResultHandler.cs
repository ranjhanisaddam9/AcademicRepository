using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace AcademicRepository.Services;

public sealed class StructuredAuthorizationResultHandler(ILogger<StructuredAuthorizationResultHandler> logger)
    : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler defaultHandler = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden || authorizeResult.Challenged)
        {
            var roles = string.Join(",", context.User.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct());
            logger.LogWarning("Authorization denied. CorrelationId={CorrelationId} UserId={UserId} Role={Role} RequestPath={RequestPath} Action={Action} Result={Result} TimestampUtc={TimestampUtc}",
                context.TraceIdentifier, context.User.FindFirstValue(ClaimTypes.NameIdentifier), roles, context.Request.Path.Value,
                context.GetRouteData().Values.GetValueOrDefault("action")?.ToString(),
                authorizeResult.Forbidden ? "Forbidden" : "Challenge", DateTimeOffset.UtcNow);
        }
        return defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
