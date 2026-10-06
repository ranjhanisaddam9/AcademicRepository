using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AcademicRepository.Services;

namespace AcademicRepository.Controllers;

[Authorize(Roles = "Student")]
public class StudentController(DashboardService dashboard) : Controller
{
    public async Task<IActionResult> Dashboard() => View("~/Views/Home/Index.cshtml", await dashboard.GetAsync(User));
    public IActionResult MySubmissions() => View("~/Views/Shared/Placeholder.cshtml", "My Submissions");
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
