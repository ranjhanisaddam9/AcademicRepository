using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace AcademicRepository.Services;

public sealed class DashboardService(ApplicationDbContext db, UserManager<ApplicationUser> users)
{
    public async Task<DashboardViewModel?> GetAsync(ClaimsPrincipal principal)
    {
        var user = await users.Users.Include(u => u.Department).SingleOrDefaultAsync(u => u.Id == users.GetUserId(principal));
        if (user is null) return null;
        var roles = (await users.GetRolesAsync(user)).ToArray();
        var admin = roles.Contains("Admin");
        return new DashboardViewModel(string.IsNullOrWhiteSpace(user.FullName) ? user.UserName ?? "User" : user.FullName,
            roles, user.Department?.Name ?? "Not Assigned",
            admin ? await db.Departments.CountAsync() : null, admin ? await db.Users.CountAsync() : null);
    }
}
