using System.Security.Claims;
using AcademicRepository.Controllers;
using AcademicRepository.Models;
using AcademicRepository.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

internal static partial class IntegrationChecks
{
    public static async Task ErrorHandlingChecks()
    {
        var suppliedContext = new DefaultHttpContext();
        suppliedContext.TraceIdentifier = "generated-id";
        suppliedContext.Request.Headers["X-Correlation-ID"] = "client-request-42";
        await new CorrelationIdMiddleware(_ => Task.CompletedTask).InvokeAsync(suppliedContext);
        await suppliedContext.Response.StartAsync();
        Check(suppliedContext.TraceIdentifier == "client-request-42"
            && suppliedContext.Response.Headers["X-Correlation-ID"] == "client-request-42",
            $"Correlation middleware reuses one valid incoming ID for logging and response (trace={suppliedContext.TraceIdentifier}, header={suppliedContext.Response.Headers["X-Correlation-ID"]})");

        var invalidContext = new DefaultHttpContext();
        invalidContext.TraceIdentifier = "generated-fallback";
        invalidContext.Request.Headers["X-Correlation-ID"] = "secret value\r\nInjected";
        await new CorrelationIdMiddleware(_ => Task.CompletedTask).InvokeAsync(invalidContext);
        await invalidContext.Response.StartAsync();
        Check(invalidContext.TraceIdentifier == "generated-fallback"
            && invalidContext.Response.Headers["X-Correlation-ID"] == "generated-fallback",
            "Invalid correlation header is ignored in favor of the framework request ID");

        var capture = new RecordingLogger<RequestLoggingMiddleware>();
        var request = new DefaultHttpContext();
        request.TraceIdentifier = "log-correlation-7";
        request.Request.Path = "/Student/Dashboard";
        request.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "student-id"), new Claim(ClaimTypes.Role, "Student")], "test"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:SlowRequestThresholdMs"] = "100"
        }).Build();
        await new RequestLoggingMiddleware(_ => Task.CompletedTask, capture, config,
            new EmailTestEnvironment { EnvironmentName = "Production" }).InvokeAsync(request);
        Check(capture.Scopes.Single() is var scope && scope["CorrelationId"]?.ToString() == "log-correlation-7"
            && scope["RequestPath"]?.ToString() == "/Student/Dashboard" && scope["UserId"]?.ToString() == "student-id"
            && scope["Role"]?.ToString() == "Student" && scope.ContainsKey("TimestampUtc"),
            "Request logging scope contains timestamp, correlation, path, user and role fields");
        Check(capture.Entries.Count == 0, "Production request logging does not emit every normal request");

        var context = new DefaultHttpContext();
        context.TraceIdentifier = "test-correlation-123";
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test-user")], "test"));
        var controller = new HomeController(null!) { ControllerContext = new ControllerContext { HttpContext = context } };

        var error = (ViewResult)controller.Error();
        var errorModel = (ErrorPageViewModel)error.Model!;
        Check(context.Response.StatusCode == StatusCodes.Status500InternalServerError, "Unhandled request error uses HTTP 500");
        Check(errorModel.CorrelationId == "test-correlation-123" && errorModel.IsAuthenticated, "Error page reuses the request correlation ID and auth state");

        foreach (var code in new[] { 400, 401, 403, 404, 405, 409, 429, 500, 503 })
        {
            var result = (ViewResult)controller.Status(code);
            var model = (StatusCodePageViewModel)result.Model!;
            Check(context.Response.StatusCode == code && model.StatusCode == code && model.CorrelationId == "test-correlation-123",
                $"HTTP {code} uses safe status page and request correlation ID");
            Check(!model.Message.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                && !model.Message.Contains("SQL", StringComparison.OrdinalIgnoreCase), $"HTTP {code} response avoids implementation details");
        }
    }
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<Dictionary<string, object?>> Scopes { get; } = [];
    public List<(LogLevel Level, string Message, Dictionary<string, object?> Fields)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        Scopes.Add(ToFields(state));
        return EmptyScope.Instance;
    }
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception), ToFields(state)));
    private static Dictionary<string, object?> ToFields<TState>(TState state) => state is IEnumerable<KeyValuePair<string, object?>> values
        ? values.ToDictionary(p => p.Key, p => p.Value) : new Dictionary<string, object?> { ["State"] = state };
    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();
        public void Dispose() { }
    }
}
