using AcademicRepository.Models;
using AcademicRepository.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AcademicRepository.ViewComponents;

public sealed class ProjectResourcesViewComponent(ProjectFileService files, UserManager<ApplicationUser> users) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(int submissionId)
    {
        try { return View(await files.ListAsync(submissionId, users.GetUserId(HttpContext.User) ?? "")); }
        catch (ResourceOperationException) { return Content("Resources are unavailable for this department assignment."); }
    }
}
