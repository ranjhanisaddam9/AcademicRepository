using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcademicRepository.Controllers;

[Authorize(Roles = "Admin")]
public class DepartmentsController(ApplicationDbContext db, ILogger<DepartmentsController> logger) : Controller
{
    public async Task<IActionResult> Index() => View(await db.Departments.AsNoTracking().OrderBy(d => d.Code)
        .Select(d => new DepartmentListViewModel(d.Id, d.Code, d.Name, d.IsActive, d.StudentEmailKeyword ?? "Not configured")).ToListAsync());

    public async Task<IActionResult> Details(int id)
    {
        var model = await db.Departments.AsNoTracking().Where(d => d.Id == id)
            .Select(d => new DepartmentDetailsViewModel(d.Id, d.Code, d.Name, d.IsActive, d.CreatedAt, d.UpdatedAt, d.Users.Count)).SingleOrDefaultAsync();
        return model is null ? NotFound() : View(model);
    }

    [HttpGet]
    public IActionResult Create() => View("Form", new DepartmentFormViewModel());

    [HttpPost]
    public async Task<IActionResult> Create(DepartmentFormViewModel model)
    {
        await ValidateAsync(model);
        if (!ModelState.IsValid) return View("Form", model);
        db.Departments.Add(new Department { Name = model.Name, Code = model.Code,
            StudentEmailKeyword = string.IsNullOrWhiteSpace(model.StudentEmailKeyword) ? null : model.StudentEmailKeyword });
        if (!await SaveAsync()) return View("Form", model);
        TempData["Status"] = "Department created.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var department = await db.Departments.FindAsync(id);
        if (department is null) return NotFound();
        return View("Form", new DepartmentFormViewModel { Name = department.Name, Code = department.Code, StudentEmailKeyword = department.StudentEmailKeyword ?? "" });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, DepartmentFormViewModel model)
    {
        var department = await db.Departments.FindAsync(id);
        if (department is null) return NotFound();
        // Preserve a current Student email mapping when older/manual clients omit this field.
        model.StudentEmailKeyword ??= department.StudentEmailKeyword;
        await ValidateAsync(model, id);
        if (!ModelState.IsValid) return View("Form", model);
        department.Name = model.Name;
        department.Code = model.Code;
        department.StudentEmailKeyword = string.IsNullOrWhiteSpace(model.StudentEmailKeyword) ? null : model.StudentEmailKeyword;
        department.UpdatedAt = DateTime.UtcNow;
        if (!await SaveAsync()) return View("Form", model);
        TempData["Status"] = "Department updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> SetActive(int id, bool? isActive)
    {
        if (!ModelState.IsValid || !isActive.HasValue) return BadRequest();
        var department = await db.Departments.FindAsync(id);
        if (department is null) return NotFound();
        department.IsActive = isActive.Value;
        department.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        TempData["Status"] = isActive.Value ? "Department activated." : "Department deactivated. Existing user accounts are retained.";
        return RedirectToAction(nameof(Index));
    }

    private async Task ValidateAsync(DepartmentFormViewModel model, int? id = null)
    {
        model.Name = (model.Name ?? "").Trim();
        model.Code = (model.Code ?? "").Trim().ToUpperInvariant();
        if (model.Name.Length == 0) ModelState.AddModelError(nameof(model.Name), "Department name is required.");
        if (model.Code.Length == 0) ModelState.AddModelError(nameof(model.Code), "Department code is required.");
        model.StudentEmailKeyword = model.StudentEmailKeyword?.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(model.StudentEmailKeyword) && !System.Text.RegularExpressions.Regex.IsMatch(model.StudentEmailKeyword, "^[A-Z]{3}$"))
            ModelState.AddModelError(nameof(model.StudentEmailKeyword), "Student email code must contain three letters, for example CSC.");
        if (await db.Departments.AnyAsync(d => d.Id != id && d.Name == model.Name))
            ModelState.AddModelError(nameof(model.Name), "A department with this name already exists.");
        if (await db.Departments.AnyAsync(d => d.Id != id && d.Code == model.Code))
            ModelState.AddModelError(nameof(model.Code), "A department with this code already exists.");
        if (!string.IsNullOrWhiteSpace(model.StudentEmailKeyword) && await db.Departments.AnyAsync(d => d.Id != id && d.StudentEmailKeyword == model.StudentEmailKeyword))
            ModelState.AddModelError(nameof(model.StudentEmailKeyword), "This Student email code is already assigned to a department.");
    }

    private async Task<bool> SaveAsync()
    {
        try { await db.SaveChangesAsync(); return true; }
        catch (DbUpdateException exception)
        {
            logger.LogWarning(exception, "Department update failed.");
            ModelState.AddModelError("", "Unable to save the department. Its name or code may have been used by another administrator. Refresh and try again.");
            return false;
        }
    }
}
