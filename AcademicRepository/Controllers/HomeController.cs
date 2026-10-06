using AcademicRepository.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using AcademicRepository.Services;

namespace AcademicRepository.Controllers;

[Authorize]
public class HomeController(DashboardService dashboard) : Controller
{
    public async Task<IActionResult> Index()
    {
        var model = await dashboard.GetAsync(User);
        return model is null ? Challenge() : View(model);
    }

    [AllowAnonymous]
    public IActionResult Error() => View();
}
