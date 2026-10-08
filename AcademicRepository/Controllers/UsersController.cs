using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using AcademicRepository.Services;

namespace AcademicRepository.Controllers;

[Authorize(Roles = "Admin")]
public class UsersController(ApplicationDbContext db, UserManager<ApplicationUser> users, IDataProtectionProvider protection, ILogger<UsersController> logger,
    StudentDepartmentService studentDepartments) : Controller
{
    private readonly IDataProtector editProtector = protection.CreateProtector("AcademicRepository.UserEdits.v1");
    [EnableRateLimiting("search")]
    public async Task<IActionResult> Index(string? search = null, string? role = null, int? departmentId = null, bool? isActive = null, int page = 1, int? pageSize = null, string? sort = null, string? direction = null)
    {
        if (search?.Length > 200 || (role is not null && !IdentitySeeder.Roles.Contains(role)) || departmentId < 1)
            return BadRequest("Choose valid user filters.");
        search = search?.Trim();
        var query = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(u => u.FullName.Contains(search) || (u.Email != null && u.Email.Contains(search)) || (u.StudentNumber != null && u.StudentNumber.Contains(search)));
        if (role is not null)
            query = query.Where(u => (from assignment in db.UserRoles join assignedRole in db.Roles on assignment.RoleId equals assignedRole.Id
                where assignment.UserId == u.Id && assignedRole.Name == role select assignment.UserId).Any());
        if (departmentId.HasValue) query = query.Where(u => u.DepartmentId == departmentId);
        if (isActive.HasValue) query = query.Where(u => u.IsActive == isActive);
        var allowedSorts = new[] { "Name", "Email", "StudentNumber", "CreatedAt" };
        sort = allowedSorts.FirstOrDefault(s => string.Equals(s, sort, StringComparison.OrdinalIgnoreCase)) ?? "Name";
        direction = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";
        query = (sort, direction) switch
        {
            ("Email", "desc") => query.OrderByDescending(u => u.Email).ThenBy(u => u.Id),
            ("Email", _) => query.OrderBy(u => u.Email).ThenBy(u => u.Id),
            ("StudentNumber", "desc") => query.OrderByDescending(u => u.StudentNumber).ThenBy(u => u.Id),
            ("StudentNumber", _) => query.OrderBy(u => u.StudentNumber).ThenBy(u => u.Id),
            ("CreatedAt", "desc") => query.OrderByDescending(u => u.CreatedAt).ThenBy(u => u.Id),
            ("CreatedAt", _) => query.OrderBy(u => u.CreatedAt).ThenBy(u => u.Id),
            ("Name", "desc") => query.OrderByDescending(u => u.FullName).ThenBy(u => u.Id),
            _ => query.OrderBy(u => u.FullName).ThenBy(u => u.Id)
        };
        var accounts = await query.Include(u => u.Department)
            .ToPagedResultAsync(page, pageSize);
        var ids = accounts.Items.Select(u => u.Id).ToArray();
        var assignments = await (from assignment in db.UserRoles.AsNoTracking()
                                 join assignedRole in db.Roles on assignment.RoleId equals assignedRole.Id
                                 where ids.Contains(assignment.UserId) && IdentitySeeder.Roles.Contains(assignedRole.Name!)
                                 select new { assignment.UserId, assignedRole.Name }).ToListAsync();
        var departments = await db.Departments.AsNoTracking().Where(d => d.IsActive || d.Id == departmentId).OrderBy(d => d.Name)
            .Select(d => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem(d.Name, d.Id.ToString())).ToListAsync();
        var items = new List<UserListItem>();
        var assignmentsByUser = assignments.GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.Select(a => a.Name).ToArray());
        foreach (var user in accounts.Items)
        {
            var userRoles = assignmentsByUser.GetValueOrDefault(user.Id) ?? Array.Empty<string>();
            items.Add(new(user.Id, user.FullName, user.Email ?? "", string.Join(", ", userRoles),
                user.Department?.Name ?? "Not Assigned", user.IsActive, userRoles.Contains("Student") ? user.StudentNumber : null,
                user.CreatedAt ?? DateTime.MinValue));
        }
        return View(new UserListPageViewModel(new PagedResult<UserListItem>(items, accounts.PageNumber, accounts.PageSize, accounts.TotalCount),
            search, role, departmentId, isActive, departments, sort, direction));
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        var model = new UserFormViewModel();
        await PopulateDepartmentsAsync(model);
        return View("Form", model);
    }

    [HttpPost]
    public async Task<IActionResult> Create(UserFormViewModel model)
    {
        if (model.Role == "Student") ModelState.AddModelError(nameof(model.Role), "Students register by verifying their own email code. Admins create staff accounts only.");
        await ValidateAsync(model);
        if (model.Role != "Student" && string.IsNullOrWhiteSpace(model.TemporaryPassword)) ModelState.AddModelError(nameof(model.TemporaryPassword), "Temporary password is required for staff.");
        if (!ModelState.IsValid) return await FormAsync(model);
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var user = new ApplicationUser { FullName = model.FullName, UserName = model.Email, Email = model.Email, DepartmentId = model.DepartmentId };
            var created = model.Role == "Student" ? await users.CreateAsync(user) : await users.CreateAsync(user, model.TemporaryPassword!);
            if (!Accept(created)) return await FormAsync(model);
            if (!Accept(await users.AddToRoleAsync(user, model.Role))) return await FormAsync(model);
            await transaction.CommitAsync();
            TempData["Status"] = model.Role == "Student" ? "Student created. They sign in using an emailed verification code." : "User created. Share the temporary password securely with the user.";
            return RedirectToAction(nameof(Index));
        }
        catch (DbUpdateException exception)
        {
            SaveError(exception);
            return await FormAsync(model);
        }
    }

    [HttpGet]
    public async Task<IActionResult> Edit(string id)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null) return NotFound();
        if (await users.IsInRoleAsync(user, "Student")) return Forbid();
        var model = new UserFormViewModel
        {
            FullName = user.FullName, Email = user.Email ?? "", DepartmentId = user.DepartmentId,
            Role = (await users.GetRolesAsync(user)).FirstOrDefault(r => IdentitySeeder.Roles.Contains(r)) ?? "",
            EditToken = editProtector.Protect(UserEditValue(user))
        };
        await PopulateDepartmentsAsync(model, user.DepartmentId);
        return View("Form", model);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(string id, UserFormViewModel model)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null) return NotFound();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var previousRoles = await users.GetRolesAsync(user);
        if (previousRoles.Contains("Student")) return Forbid();
        if (model.Role == "Student")
            ModelState.AddModelError(nameof(model.Role), "Administrators cannot create or convert accounts to Students. Students must complete verified self-onboarding.");
        await ValidateAsync(model, user);
        if (user.IsActive && previousRoles.Contains("Admin") && model.Role != "Admin" && await ActiveAdminCountAsync() <= 1)
            ModelState.AddModelError(nameof(model.Role), "You cannot remove the last active Admin account.");
        if (model.Role != "Student" && (previousRoles.Contains("Student") || !await users.HasPasswordAsync(user)) && string.IsNullOrWhiteSpace(model.TemporaryPassword))
            ModelState.AddModelError(nameof(model.TemporaryPassword), "Set a temporary password when changing a passwordless Student to staff.");
        if (user.Id == users.GetUserId(User) && model.Role != "Admin")
            ModelState.AddModelError(nameof(model.Role), "You cannot remove your own Admin role.");
        if (!ValidEditToken(model.EditToken, user))
            ModelState.AddModelError("", "This user was changed by another administrator. Reload the edit page before saving.");
        if (!ModelState.IsValid) return await FormAsync(model, user.DepartmentId);
        if (previousRoles.Any(ApplicationRoles.RequiresDepartment) && model.Role == "ORICQEC" && model.DepartmentId is null)
            model.DepartmentId = user.DepartmentId;
        try
        {
            user.FullName = model.FullName;
            user.Email = model.Email;
            if (model.Role == "Student") user.StudentNumber = StudentDepartmentService.StudentNumberFromEmail(model.Email);
            user.UserName = model.Email;
            user.DepartmentId = model.DepartmentId;
            if (!Accept(await users.UpdateAsync(user))) return await FormAsync(model, user.DepartmentId);
            var oldRoles = (await users.GetRolesAsync(user)).Where(r => IdentitySeeder.Roles.Contains(r)).ToArray();
            if (oldRoles.Length > 0 && !Accept(await users.RemoveFromRolesAsync(user, oldRoles))) return await FormAsync(model, user.DepartmentId);
            if (!Accept(await users.AddToRoleAsync(user, model.Role))) return await FormAsync(model, user.DepartmentId);
            if (model.Role != "Student" && !string.IsNullOrWhiteSpace(model.TemporaryPassword))
            {
                var resetToken = await users.GeneratePasswordResetTokenAsync(user);
                if (!Accept(await users.ResetPasswordAsync(user, resetToken, model.TemporaryPassword))) return await FormAsync(model, user.DepartmentId);
            }
            // End existing sessions so role changes take effect immediately.
            if (!Accept(await users.UpdateSecurityStampAsync(user))) return await FormAsync(model, user.DepartmentId);
            await transaction.CommitAsync();
            TempData["Status"] = "User updated. The user must sign in again to use the updated profile.";
            return RedirectToAction(nameof(Index));
        }
        catch (DbUpdateException exception)
        {
            SaveError(exception);
            return await FormAsync(model, user.DepartmentId);
        }
    }

    [HttpPost]
    public async Task<IActionResult> SetActive(string id, bool? isActive)
    {
        if (!ModelState.IsValid || !isActive.HasValue) return BadRequest();
        var user = await users.FindByIdAsync(id);
        if (user is null) return NotFound();
        if (!isActive.Value && id == users.GetUserId(User))
        {
            TempData["Error"] = "You cannot deactivate your own account.";
            return RedirectToAction(nameof(Index));
        }
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var roles = await users.GetRolesAsync(user);
        if (!isActive.Value && roles.Contains("Admin") && await ActiveAdminCountAsync() <= 1)
        {
            TempData["Error"] = "You cannot deactivate the last active Admin account.";
            return RedirectToAction(nameof(Index));
        }
        user.IsActive = isActive.Value;
        var result = await users.UpdateAsync(user);
        if (result.Succeeded) result = await users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded)
        {
            TempData["Error"] = string.Join(" ", result.Errors.Select(e => e.Description));
            return RedirectToAction(nameof(Index));
        }
        await transaction.CommitAsync();
        TempData["Status"] = isActive.Value ? "User activated." : "User deactivated. Existing sessions have been revoked.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> ResetPassword(string id)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null) return NotFound();
        if (await users.IsInRoleAsync(user, "Student")) return BadRequest("Students use passwordless sign-in codes.");
        return View(new ResetPasswordViewModel { UserName = user.FullName });
    }

    [HttpPost]
    public async Task<IActionResult> ResetPassword(string id, ResetPasswordViewModel model)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null) return NotFound();
        if (await users.IsInRoleAsync(user, "Student")) return BadRequest("Students use passwordless sign-in codes.");
        model.UserName = user.FullName;
        if (ModelState.IsValid)
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            if (Accept(await users.ResetPasswordAsync(user, token, model.Password)))
            {
                TempData["Status"] = "Password reset. Share the new password securely. Existing sessions have been revoked.";
                return RedirectToAction(nameof(Index));
            }
        }
        model.Password = "";
        ModelState.SetModelValue(nameof(model.Password), null, null);
        return View(model);
    }

    private async Task ValidateAsync(UserFormViewModel model, ApplicationUser? existing = null)
    {
        model.FullName = (model.FullName ?? "").Trim();
        model.Email = (model.Email ?? "").Trim();
        if (model.FullName.Length == 0) ModelState.AddModelError(nameof(model.FullName), "Full name is required.");
        if (!InstitutionalEmail.IsValid(model.Email)) ModelState.AddModelError(nameof(model.Email), "Use a valid @smiu.edu.pk email address.");
        if (!IdentitySeeder.Roles.Contains(model.Role)) ModelState.AddModelError(nameof(model.Role), "Select a valid application role.");
        if (model.Role == "Student")
        {
            // Student department is always derived from the email; discard all browser values/errors.
            ModelState.Remove(nameof(model.DepartmentId));
            var mapped = await studentDepartments.ResolveAsync(model.Email);
            model.DepartmentId = mapped?.Id;
            if (mapped is null) ModelState.AddModelError(nameof(model.Email), "Use a Student email such as CSC20F005@smiu.edu.pk: department letters, two-digit year, F or S session, and three digits.");
        }
        if (ApplicationRoles.RequiresDepartment(model.Role) && model.DepartmentId is null) ModelState.AddModelError(nameof(model.DepartmentId), "A department is required for this role.");
        if (model.DepartmentId is int departmentId)
        {
            var department = await db.Departments.FindAsync(departmentId);
            if (department is null || (!department.IsActive && existing?.DepartmentId != departmentId))
                ModelState.AddModelError(nameof(model.DepartmentId), "Select an active department. Existing assignments to inactive departments may be retained.");
        }
        var emailOwner = await users.FindByEmailAsync(model.Email);
        if (emailOwner is not null && emailOwner.Id != existing?.Id) ModelState.AddModelError(nameof(model.Email), "This email is already assigned to another user.");
    }

    private async Task PopulateDepartmentsAsync(UserFormViewModel model, int? currentId = null)
    {
        model.Departments = await db.Departments.AsNoTracking().Where(d => d.IsActive || d.Id == currentId).OrderBy(d => d.Name)
            .Select(d => new SelectListItem(d.Name + (d.IsActive ? "" : " (inactive)"), d.Id.ToString())).ToListAsync();
    }

    private Task<int> ActiveAdminCountAsync() => (from assignment in db.UserRoles.AsNoTracking()
        join role in db.Roles on assignment.RoleId equals role.Id
        join user in db.Users.AsNoTracking() on assignment.UserId equals user.Id
        where role.Name == "Admin" && user.IsActive select user.Id).Distinct().CountAsync();

    private async Task<IActionResult> FormAsync(UserFormViewModel model, int? currentId = null)
    {
        model.TemporaryPassword = null;
        ModelState.SetModelValue(nameof(model.TemporaryPassword), null, null);
        await PopulateDepartmentsAsync(model, currentId);
        return View("Form", model);
    }

    private bool Accept(IdentityResult result)
    {
        if (result.Errors.Any(error => error.Code == "ConcurrencyFailure"))
        {
            logger.LogWarning("Identity user concurrency conflict. Result={Result}", "Conflict");
            ModelState.AddModelError("", "This record was changed by another user. Please refresh and try again.");
            return false;
        }
        foreach (var error in result.Errors) ModelState.AddModelError("", error.Description);
        return result.Succeeded;
    }

    private bool ValidEditToken(string? token, ApplicationUser user)
    {
        if (string.IsNullOrEmpty(token)) return false;
        try { return editProtector.Unprotect(token) == UserEditValue(user); }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }

    private static string UserEditValue(ApplicationUser user) =>
        $"{user.Id}\n{user.ConcurrencyStamp}\n{Convert.ToBase64String(user.RowVersion)}";

    private void SaveError(DbUpdateException exception)
    {
        logger.LogWarning(exception, "User update failed.");
        ModelState.AddModelError("", "Unable to save the user. Another administrator may have changed the record. Refresh and try again.");
    }
}
