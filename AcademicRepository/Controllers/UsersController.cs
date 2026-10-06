using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AcademicRepository.Controllers;

[Authorize(Roles = "Admin")]
public class UsersController(ApplicationDbContext db, UserManager<ApplicationUser> users, IDataProtectionProvider protection, ILogger<UsersController> logger) : Controller
{
    private readonly IDataProtector editProtector = protection.CreateProtector("AcademicRepository.UserEdits.v1");
    public async Task<IActionResult> Index(int? departmentId = null)
    {
        var accounts = await db.Users.AsNoTracking().Include(u => u.Department)
            .Where(u => departmentId == null || u.DepartmentId == departmentId).OrderBy(u => u.FullName).ToListAsync();
        var assignments = await (from assignment in db.UserRoles
                                 join role in db.Roles on assignment.RoleId equals role.Id
                                 select new { assignment.UserId, role.Name }).ToListAsync();
        return View(accounts.Select(u => new UserListViewModel(u.Id, u.FullName, u.Email ?? "",
            string.Join(", ", assignments.Where(a => a.UserId == u.Id && IdentitySeeder.Roles.Contains(a.Name)).Select(a => a.Name)),
            u.Department?.Name ?? "Not Assigned", u.IsActive)).ToList());
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
        await ValidateAsync(model);
        if (string.IsNullOrWhiteSpace(model.TemporaryPassword)) ModelState.AddModelError(nameof(model.TemporaryPassword), "Temporary password is required.");
        if (!ModelState.IsValid) return await FormAsync(model);
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var user = new ApplicationUser { FullName = model.FullName, UserName = model.Email, Email = model.Email, DepartmentId = model.DepartmentId };
            if (!Accept(await users.CreateAsync(user, model.TemporaryPassword!))) return await FormAsync(model);
            if (!Accept(await users.AddToRoleAsync(user, model.Role))) return await FormAsync(model);
            await transaction.CommitAsync();
            TempData["Status"] = "User created. Share the temporary password securely with the user.";
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
        var model = new UserFormViewModel
        {
            FullName = user.FullName, Email = user.Email ?? "", DepartmentId = user.DepartmentId,
            Role = (await users.GetRolesAsync(user)).FirstOrDefault(r => IdentitySeeder.Roles.Contains(r)) ?? "",
            EditToken = editProtector.Protect(user.Id + "\n" + user.ConcurrencyStamp)
        };
        await PopulateDepartmentsAsync(model, user.DepartmentId);
        return View("Form", model);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(string id, UserFormViewModel model)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null) return NotFound();
        await ValidateAsync(model, user);
        if (user.Id == users.GetUserId(User) && model.Role != "Admin")
            ModelState.AddModelError(nameof(model.Role), "You cannot remove your own Admin role.");
        if (!ValidEditToken(model.EditToken, user))
            ModelState.AddModelError("", "This user was changed by another administrator. Reload the edit page before saving.");
        if (!ModelState.IsValid) return await FormAsync(model, user.DepartmentId);
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            user.FullName = model.FullName;
            user.Email = model.Email;
            user.UserName = model.Email;
            user.DepartmentId = model.DepartmentId;
            if (!Accept(await users.UpdateAsync(user))) return await FormAsync(model, user.DepartmentId);
            var oldRoles = (await users.GetRolesAsync(user)).Where(r => IdentitySeeder.Roles.Contains(r)).ToArray();
            if (oldRoles.Length > 0 && !Accept(await users.RemoveFromRolesAsync(user, oldRoles))) return await FormAsync(model, user.DepartmentId);
            if (!Accept(await users.AddToRoleAsync(user, model.Role))) return await FormAsync(model, user.DepartmentId);
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
        await using var transaction = await db.Database.BeginTransactionAsync();
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
        return user is null ? NotFound() : View(new ResetPasswordViewModel { UserName = user.FullName });
    }

    [HttpPost]
    public async Task<IActionResult> ResetPassword(string id, ResetPasswordViewModel model)
    {
        var user = await users.FindByIdAsync(id);
        if (user is null) return NotFound();
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
        if (!IdentitySeeder.Roles.Contains(model.Role)) ModelState.AddModelError(nameof(model.Role), "Select one of the four application roles.");
        if (model.Role != "Admin" && model.DepartmentId is null) ModelState.AddModelError(nameof(model.DepartmentId), "A department is required for this role.");
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

    private async Task<IActionResult> FormAsync(UserFormViewModel model, int? currentId = null)
    {
        model.TemporaryPassword = null;
        ModelState.SetModelValue(nameof(model.TemporaryPassword), null, null);
        await PopulateDepartmentsAsync(model, currentId);
        return View("Form", model);
    }

    private bool Accept(IdentityResult result)
    {
        foreach (var error in result.Errors) ModelState.AddModelError("", error.Description);
        return result.Succeeded;
    }

    private bool ValidEditToken(string? token, ApplicationUser user)
    {
        if (string.IsNullOrEmpty(token)) return false;
        try { return editProtector.Unprotect(token) == user.Id + "\n" + user.ConcurrencyStamp; }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }

    private void SaveError(DbUpdateException exception)
    {
        logger.LogWarning(exception, "User update failed.");
        ModelState.AddModelError("", "Unable to save the user. Another administrator may have changed the record. Refresh and try again.");
    }
}
