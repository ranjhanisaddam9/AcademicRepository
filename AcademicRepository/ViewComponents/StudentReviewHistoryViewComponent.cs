using AcademicRepository.Models;
using AcademicRepository.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AcademicRepository.ViewComponents;

public sealed class StudentReviewHistoryViewComponent(IReviewService reviews, UserManager<ApplicationUser> users) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(int submissionId)
    {
        try { return View("~/Views/Shared/_ReviewHistory.cshtml", await reviews.GetStudentHistoryAsync(users.GetUserId(HttpContext.User) ?? "", submissionId)); }
        catch (ReviewOperationException) { return Content(""); }
    }
}
