using AcademicRepository.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using AcademicRepository.Services;

namespace AcademicRepository.Controllers;

[Authorize]
public class HomeController(DashboardService dashboard) : Controller
{
    public async Task<IActionResult> Index()
    {
        var model = await dashboard.GetAsync(User);
        return model is null ? Challenge() : View(model);
    }

    [AllowAnonymous, Route("/Error")]
    public IActionResult Error()
    {
        Response.StatusCode = StatusCodes.Status500InternalServerError;
        Response.Headers["X-Correlation-ID"] = HttpContext.TraceIdentifier;
        return View(new ErrorPageViewModel(HttpContext.TraceIdentifier, User.Identity?.IsAuthenticated == true));
    }

    [AllowAnonymous, Route("/Error/Status")]
    public IActionResult Status(int statusCode)
    {
        if (statusCode is not (400 or 401 or 403 or 404 or 405 or 409 or 429 or 500 or 503))
            statusCode = StatusCodes.Status500InternalServerError;
        Response.Headers["X-Correlation-ID"] = HttpContext.TraceIdentifier;
        var (title, message) = statusCode switch
        {
            400 => ("Invalid request", "We couldn't understand that request. Check the information and try again."),
            401 => ("Sign in required", "Please sign in to continue."),
            403 => ("Access denied", "You don't have permission to access this page."),
            404 => ("Page not found", "We couldn't find the page you requested."),
            405 => ("Action not allowed", "That action isn't available for this request."),
            409 => ("Request conflict", "The information changed before your request completed. Refresh and try again."),
            429 => ("Too many requests", "Please wait a moment before trying again."),
            503 => ("Service temporarily unavailable", "We couldn't complete your request. Please try again."),
            _ => ("Something went wrong", "We couldn't complete your request. Please try again.")
        };
        Response.StatusCode = statusCode;
        return View("StatusCode", new StatusCodePageViewModel(statusCode, title, message, HttpContext.TraceIdentifier, User.Identity?.IsAuthenticated == true));
    }
}
