using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AcademicRepository.Services;
using System.Security.Claims;

namespace AcademicRepository.Controllers;

[Authorize(Roles = "Student")]
public class StudentController(DashboardService dashboard, StudentSubmissionService submissions) : Controller
{
    public async Task<IActionResult> Dashboard()
    {
        var profile = await dashboard.GetAsync(User);
        var studentId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return profile is null || studentId is null ? Challenge() : View(await submissions.DashboardAsync(studentId, profile));
    }
    public IActionResult MySubmissions() => RedirectToAction("Index", "StudentSubmissions");
}
[Authorize(Roles = "Admin")]
public class AdminController(IAdminSystemService system, IOperationalSettingsService settings,
    Microsoft.AspNetCore.Identity.UserManager<AcademicRepository.Models.ApplicationUser> users) : Controller
{
    public async Task<IActionResult> Dashboard() => View("~/Views/Home/AdminOverview.cshtml", await system.GetDashboardAsync());
    public IActionResult Administration() => RedirectToAction("Index", "Users");
    public async Task<IActionResult> Overview() => View("~/Views/Home/AdminOverview.cshtml", await system.GetDashboardAsync());
    public async Task<IActionResult> SystemInformation() => View("~/Views/Home/SystemInformation.cshtml", await system.GetSystemInformationAsync());
    public async Task<IActionResult> Diagnostics() => View("~/Views/Home/Diagnostics.cshtml", await system.GetDiagnosticsAsync());
    [HttpGet]
    public async Task<IActionResult> Settings() => View("~/Views/Home/Settings.cshtml", await settings.GetAsync());
    [HttpPost]
    public async Task<IActionResult> Settings(AcademicRepository.Models.OperationalSettingsViewModel model)
    {
        if (!ModelState.IsValid) return View("~/Views/Home/Settings.cshtml", model);
        try
        {
            var id = users.GetUserId(User);
            if (id is null) return Challenge();
            await settings.UpdateAsync(model, id);
            TempData["Status"] = "Operational settings saved.";
            return RedirectToAction(nameof(Settings));
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return View("~/Views/Home/Settings.cshtml", model);
        }
    }
}
