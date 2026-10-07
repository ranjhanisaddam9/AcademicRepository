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
[Authorize(Roles = "Coordinator")]
public class CoordinatorController(DashboardService dashboard) : Controller
{
    public async Task<IActionResult> Dashboard() => View("~/Views/Home/Index.cshtml", await dashboard.GetAsync(User));
    public IActionResult Submissions() => View("~/Views/Shared/Placeholder.cshtml", "Submissions");
}
[Authorize(Roles = "DepartmentHead")]
public class DepartmentHeadController(DashboardService dashboard) : Controller
{
    public async Task<IActionResult> Dashboard() => View("~/Views/Home/Index.cshtml", await dashboard.GetAsync(User));
    public IActionResult Repository() => View("~/Views/Shared/Placeholder.cshtml", "Repository");
}
[Authorize(Roles = "Admin")]
public class AdminController(DashboardService dashboard) : Controller
{
    public async Task<IActionResult> Dashboard() => View("~/Views/Home/Index.cshtml", await dashboard.GetAsync(User));
    public IActionResult Administration() => RedirectToAction("Index", "Users");
}

[Authorize(Roles = "ORICQEC")]
public class ORICQECController(DashboardService dashboard) : Controller
{
    public async Task<IActionResult> Dashboard() => View(await dashboard.GetAsync(User));
}
