using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AcademicRepository.Controllers;

[Authorize(Roles = "Admin")]
public class DepartmentsController(ApplicationDbContext db, ILogger<DepartmentsController> logger) : Controller
{
    [EnableRateLimiting("search")]
    public async Task<IActionResult> Index(string? search = null, int page = 1, int? pageSize = null, string? sort = null, string? direction = null)
    {
        if (search?.Length > 200) return BadRequest("Search must be 200 characters or fewer.");
        search = search?.Trim();
        var query = db.Departments.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(d => d.Name.Contains(search) || d.Code.Contains(search) || (d.StudentEmailKeyword != null && d.StudentEmailKeyword.Contains(search)));
        sort = new[] { "Code", "Name", "Status" }.FirstOrDefault(s => string.Equals(s, sort, StringComparison.OrdinalIgnoreCase)) ?? "Code";
        direction = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";
        query = (sort, direction) switch
        {
            ("Name", "desc") => query.OrderByDescending(d => d.Name).ThenBy(d => d.Id),
            ("Name", _) => query.OrderBy(d => d.Name).ThenBy(d => d.Id),
            ("Status", "desc") => query.OrderByDescending(d => d.IsActive).ThenBy(d => d.Code).ThenBy(d => d.Id),
            ("Status", _) => query.OrderBy(d => d.IsActive).ThenBy(d => d.Code).ThenBy(d => d.Id),
            ("Code", "desc") => query.OrderByDescending(d => d.Code).ThenBy(d => d.Id),
            _ => query.OrderBy(d => d.Code).ThenBy(d => d.Id)
        };
        var rows = await query
            .Select(d => new DepartmentListViewModel(d.Id, d.Code, d.Name, d.IsActive, d.StudentEmailKeyword ?? "Not configured"))
            .ToPagedResultAsync(page, pageSize);
        return View(new DepartmentListPageViewModel(rows, search, sort, direction));
    }

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
        return View("Form", new DepartmentFormViewModel { Name = department.Name, Code = department.Code, StudentEmailKeyword = department.StudentEmailKeyword ?? "", RowVersion = department.RowVersion });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(int id, DepartmentFormViewModel model)
    {
        var department = await db.Departments.FindAsync(id);
        if (department is null) return NotFound();
        if (model.RowVersion is null || model.RowVersion.Length == 0)
            ModelState.AddModelError("", "This department was changed by another administrator. Please refresh and try again.");
        // Preserve a current Student email mapping when older/manual clients omit this field.
        model.StudentEmailKeyword ??= department.StudentEmailKeyword;
        await ValidateAsync(model, id);
        if (!ModelState.IsValid) return View("Form", model);
        db.Entry(department).Property(d => d.RowVersion).OriginalValue = model.RowVersion!;
        department.Name = model.Name;
        department.Code = model.Code;
        department.StudentEmailKeyword = string.IsNullOrWhiteSpace(model.StudentEmailKeyword) ? null : model.StudentEmailKeyword;
        department.UpdatedAt = DateTime.UtcNow;
        if (!await SaveAsync(department)) return View("Form", model);
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

    private async Task<bool> SaveAsync(Department? department = null)
    {
        try { await db.SaveChangesAsync(); return true; }
        catch (DbUpdateConcurrencyException exception)
        {
            logger.LogWarning(exception, "Department concurrency conflict. DepartmentId={DepartmentId} Result={Result}", department?.Id, "Conflict");
            ModelState.AddModelError("", "This record was changed by another user. Please refresh and try again.");
            if (department is not null)
            {
                var entry = db.Entry(department);
                await entry.ReloadAsync();
            }
            return false;
        }
        catch (DbUpdateException exception)
        {
            logger.LogWarning(exception, "Department update failed.");
            ModelState.AddModelError("", "Unable to save the department. Its name or code may have been used by another administrator. Refresh and try again.");
            return false;
        }
    }
}
