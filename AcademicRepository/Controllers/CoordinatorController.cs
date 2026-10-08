using AcademicRepository.Models;
using AcademicRepository.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AcademicRepository.Controllers;

[Authorize(Roles = "Coordinator")]
public sealed class CoordinatorController(IReviewService reviews, UserManager<ApplicationUser> users, ILogger<CoordinatorController> logger) : Controller
{
    private string UserId => users.GetUserId(User) ?? "";
    [HttpGet]
    public Task<IActionResult> Dashboard() => RunAsync(async () => View(await reviews.GetCoordinatorDashboardAsync(UserId)));
    [HttpGet]
    [EnableRateLimiting("search")]
    public Task<IActionResult> ReviewQueue([FromQuery] ReviewFilterViewModel filter) => ListingAsync(filter, true);
    [HttpGet]
    [EnableRateLimiting("search")]
    public Task<IActionResult> Submissions([FromQuery] ReviewFilterViewModel filter) => ListingAsync(filter, false);
    private Task<IActionResult> ListingAsync(ReviewFilterViewModel filter, bool queue) => RunAsync(async () =>
    {
        if (!ModelState.IsValid) throw new ReviewOperationException(400, "Review filters are invalid. Use the review queue to start again.");
        return View("Submissions", await reviews.GetDepartmentSubmissionsAsync(UserId, filter, queue));
    });
    [HttpGet("/Coordinator/Submissions/Review/{id:int}")]
    public Task<IActionResult> Review([FromRoute] int id) => RunAsync(async () => View(await reviews.GetSubmissionForReviewAsync(UserId, id)));
    [HttpPost("/Coordinator/Submissions/StartReview/{id:int}")]
    public Task<IActionResult> StartReview([FromRoute] int id) => RunAsync(async () =>
    {
        await reviews.StartReviewAsync(UserId, id);
        TempData["Status"] = "Review started.";
        return RedirectToAction(nameof(Review), new { id });
    });
    [HttpPost("/Coordinator/Submissions/Approve/{id:int}")]
    public Task<IActionResult> Approve([FromRoute] int id, ReviewDecisionViewModel model) => DecideAsync(id, model, true);
    [HttpPost("/Coordinator/Submissions/Reject/{id:int}")]
    public Task<IActionResult> Reject([FromRoute] int id, ReviewDecisionViewModel model) => DecideAsync(id, model, false);
    private Task<IActionResult> DecideAsync(int id, ReviewDecisionViewModel model, bool approve) => RunAsync(async () =>
    {
        try
        {
            if (!ModelState.IsValid) throw new ReviewOperationException(400, "Choose the active review and use comments of at most 2000 characters.");
            if (approve) await reviews.ApproveAsync(UserId, id, model); else await reviews.RejectAsync(UserId, id, model);
        }
        catch (ReviewOperationException ex) when (ex.Status == 400)
        {
            // Re-authorize before rendering any details after a validation failure.
            var details = await reviews.GetSubmissionForReviewAsync(UserId, id);
            ModelState.AddModelError("", ex.Message);
            ViewData["Comments"] = model.Comments;
            Response.StatusCode = 400;
            return View("Review", details);
        }
        TempData["Status"] = approve ? "Submission approved." : "Submission rejected. Comments are visible to the Student.";
        return RedirectToAction(nameof(Review), new { id });
    });
    [HttpGet("/Coordinator/Files/Download/{id:int}")]
    public Task<IActionResult> Download([FromRoute] int id) => RunAsync(async () =>
    {
        var file = await reviews.DownloadAsync(UserId, id);
        logger.LogInformation("Secure file access. Action={Action} UserId={UserId} Role={Role} FileId={FileId} Result={Result}",
            "FileDownload", UserId, "Coordinator", id, "Success");
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "no-store";
        return File(file.Stream, file.ContentType, file.Name);
    });
    private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (ReviewOperationException ex)
        {
            Response.StatusCode = ex.Status;
            return View("ReviewError", ex.Message);
        }
        catch (System.Data.Common.DbException ex)
        {
            logger.LogError(ex, "Coordinator review database request failed.");
            Response.StatusCode = 503;
            return View("ReviewError", "We couldn't complete your request. Please try again.");
        }
    }
}
