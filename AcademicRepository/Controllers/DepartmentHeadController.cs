using AcademicRepository.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace AcademicRepository.Controllers;

[Authorize(Roles = "DepartmentHead")]
public sealed class DepartmentHeadController(IRepositoryService repository, ILogger<DepartmentHeadController> logger) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Dashboard()
    {
        try { return View(await repository.GetDepartmentRepositoryStatsAsync(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "")); }
        catch (ReviewOperationException ex) { Response.StatusCode = ex.Status; return View("~/Views/Repository/Error.cshtml", ex.Message); }
        catch (System.Data.Common.DbException ex)
        {
            logger.LogError("Department repository insights unavailable ({ErrorType}).", ex.GetType().Name);
            Response.StatusCode = 503;
            return View("~/Views/Repository/Error.cshtml", "Department insights are temporarily unavailable.");
        }
    }
    [HttpGet]
    public IActionResult Repository() => RedirectToAction("Index", "Repository");
}
