namespace AcademicRepository.Middleware;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Correlation-ID", out var requestedId)
            && requestedId.Count == 1 && requestedId[0] is { Length: > 0 and <= 64 } value
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            context.TraceIdentifier = value;

        context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
        return next(context);
    }
}
