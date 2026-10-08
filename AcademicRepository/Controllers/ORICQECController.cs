using System.Security.Claims;
using AcademicRepository.Models;
using AcademicRepository.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AcademicRepository.Controllers;

[Authorize(Roles = "ORICQEC")]
public sealed class ORICQECController(IRepositoryService repository, IRepositoryReportService reports, ILogger<ORICQECController> logger) : Controller
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    [HttpGet]
    public Task<IActionResult> Dashboard() => RunAsync(async () => View(await reports.GetInstitutionSummaryAsync(UserId)));
    [HttpGet]
    public Task<IActionResult> Reports() => RunAsync(async () => View(await reports.GetInstitutionSummaryAsync(UserId)));
    [HttpGet]
    public Task<IActionResult> Repository([FromQuery] RepositoryFilterViewModel filter) => RunAsync(async () =>
    {
        ValidateFilter();
        return View("~/Views/Repository/Index.cshtml", await repository.GetApprovedProjectsAsync(UserId, filter, true));
    });
    [HttpGet]
    public Task<IActionResult> Details([FromRoute] int id) => RunAsync(async () =>
        View("~/Views/Repository/Details.cshtml", await repository.GetApprovedProjectDetailsAsync(UserId, id, true)));
    [HttpGet("/ORICQEC/Repository/{id:int}/Versions/{versionId:int}/Files/{fileId:int}")]
    public Task<IActionResult> Download([FromRoute] int id, [FromRoute] int versionId, [FromRoute] int fileId) => RunAsync(async () =>
    {
        var file = await repository.DownloadAsync(UserId, id, versionId, fileId, true);
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(file.Stream, file.ContentType, file.Name);
    });
    [HttpGet]
    public Task<IActionResult> Export([FromQuery] RepositoryFilterViewModel filter) => RunAsync(async () =>
    {
        ValidateFilter();
        // Complete a bounded-memory export before returning headers; failed exports are never offered as successful files.
        var stream = new FileStream(Path.Combine(Path.GetTempPath(), "AcademicRepository-" + Guid.NewGuid().ToString("N") + ".csv"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await reports.WriteCsvAsync(UserId, filter, stream, HttpContext.RequestAborted);
            stream.Position = 0;
            Response.Headers.CacheControl = "no-store";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(stream, "text/csv; charset=utf-8", "academic-repository.csv");
        }
        catch { await stream.DisposeAsync(); throw; }
    });
    private void ValidateFilter()
    {
        if (!ModelState.IsValid) throw new ReviewOperationException(400, "Choose valid repository search criteria.");
    }
    private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (ReviewOperationException ex) { Response.StatusCode = ex.Status; return View("~/Views/Repository/Error.cshtml", ex.Message); }
        catch (Exception ex) when (ex is System.Data.Common.DbException or IOException or UnauthorizedAccessException)
        {
            logger.LogError("Institution repository services unavailable ({ErrorType}).", ex.GetType().Name);
            Response.StatusCode = 503;
            return View("~/Views/Repository/Error.cshtml", "Institution repository services are temporarily unavailable.");
        }
    }
}
