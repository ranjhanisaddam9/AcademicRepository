using AcademicRepository.Models;
using AcademicRepository.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AcademicRepository.Controllers;

[Authorize(Roles = "Student")]
public sealed class ProjectFilesController(ProjectFileService files, UserManager<ApplicationUser> users, ILogger<ProjectFilesController> logger) : Controller
{
    private string StudentId => users.GetUserId(User) ?? "";
    [HttpPost]
    [EnableRateLimiting("uploads")]
    public async Task<IActionResult> Upload([FromRoute] int id, UploadResourceViewModel model)
    {
        if (!ModelState.IsValid) return Error(400, "Choose a file, a valid resource type, and a description of at most 1000 characters.");
        try { await files.UploadAsync(id, StudentId, model); }
        catch (ResourceOperationException ex) { return Error(ex.Status, ex.Message); }
        catch (System.Data.Common.DbException ex) { return DatabaseError(ex); }
        TempData["Status"] = "Resource uploaded. Review your project before submitting.";
        return RedirectToAction("Details", "StudentSubmissions", new { id });
    }
    [HttpGet]
    public async Task<IActionResult> Download([FromRoute] int id)
    {
        try
        {
            var file = await files.DownloadAsync(id, StudentId);
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers.CacheControl = "no-store";
            return File(file.Stream, file.ContentType, file.Name);
        }
        catch (ResourceOperationException ex) { return Error(ex.Status, ex.Message); }
        catch (System.Data.Common.DbException ex) { return DatabaseError(ex); }
    }
    [HttpPost]
    public async Task<IActionResult> Delete([FromRoute] int id)
    {
        try
        {
            var submissionId = await files.DeleteAsync(id, StudentId);
            TempData["Status"] = "Resource deleted.";
            return RedirectToAction("Details", "StudentSubmissions", new { id = submissionId });
        }
        catch (ResourceOperationException ex) { return Error(ex.Status, ex.Message); }
        catch (System.Data.Common.DbException ex) { return DatabaseError(ex); }
    }
    private IActionResult Error(int status, string message)
    {
        Response.StatusCode = status;
        return View("ResourceError", message);
    }
    private IActionResult DatabaseError(Exception exception)
    {
        logger.LogError("Resource database operation unavailable ({ErrorType}).", exception.GetType().Name);
        return Error(503, "Resource services are temporarily unavailable. Please try again.");
    }
}
