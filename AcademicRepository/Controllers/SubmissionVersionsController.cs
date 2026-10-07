using AcademicRepository.Models;
using AcademicRepository.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AcademicRepository.Controllers;

[Authorize(Roles = "Student,Coordinator")]
public sealed class SubmissionVersionsController(SubmissionVersionService versions, UserManager<ApplicationUser> users) : Controller
{
    private string UserId => users.GetUserId(User) ?? "";
    [HttpGet("SubmissionVersions/{id:int}")]
    public Task<IActionResult> Details([FromRoute] int id) => RunAsync(async () => View(await versions.DetailsAsync(UserId, id)));
    [HttpGet("SubmissionVersions/{id:int}/Files/{fileId:int}")]
    public Task<IActionResult> Download([FromRoute] int id, [FromRoute] int fileId) => RunAsync(async () =>
    {
        var file = await versions.DownloadAsync(UserId, id, fileId);
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(file.Stream, file.ContentType, file.Name);
    });
    private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (ReviewOperationException ex) { Response.StatusCode = ex.Status; return View("~/Views/Coordinator/ReviewError.cshtml", ex.Message); }
        catch (System.Data.Common.DbException) { Response.StatusCode = 503; return View("~/Views/Coordinator/ReviewError.cshtml", "Version history is temporarily unavailable."); }
    }
}
