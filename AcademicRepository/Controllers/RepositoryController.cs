using AcademicRepository.Models;
using AcademicRepository.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AcademicRepository.Controllers;

[Authorize(Roles = "Student,Coordinator,DepartmentHead,ORICQEC")]
public sealed class RepositoryController(IRepositoryService repository, UserManager<ApplicationUser> users, ILogger<RepositoryController> logger) : Controller
{
    private string UserId => users.GetUserId(User) ?? "";
    [HttpGet]
    [EnableRateLimiting("search")]
    public Task<IActionResult> Index([FromQuery] RepositoryFilterViewModel filter) => RunAsync(async () =>
    {
        if (!ModelState.IsValid) throw new ReviewOperationException(400, "Choose valid repository search criteria.");
        return View(await repository.GetApprovedProjectsAsync(UserId, filter));
    });
    [HttpGet]
    public Task<IActionResult> Details([FromRoute] int id) => RunAsync(async () => View(await repository.GetApprovedProjectDetailsAsync(UserId, id)));
    [HttpGet("/Repository/{id:int}/Versions/{versionId:int}/Files/{fileId:int}")]
    public Task<IActionResult> Download([FromRoute] int id, [FromRoute] int versionId, [FromRoute] int fileId) => RunAsync(async () =>
    {
        var file = await repository.DownloadAsync(UserId, id, versionId, fileId);
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(file.Stream, file.ContentType, file.Name);
    });
    private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (ReviewOperationException ex) { Response.StatusCode = ex.Status; return View("Error", ex.Message); }
        catch (System.Data.Common.DbException ex)
        {
            logger.LogError(ex, "Repository database request failed.");
            Response.StatusCode = 503;
            return View("Error", "We couldn't complete your request. Please try again.");
        }
    }
}
