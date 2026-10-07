using AcademicRepository.Models;
using Microsoft.AspNetCore.Identity;

namespace AcademicRepository.Data;

public static class IdentitySeeder
{
    public static readonly string[] Roles = ["Admin", "Student", "Coordinator", "DepartmentHead", "ORICQEC"];

    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration, bool development)
    {
        await DepartmentSeeder.SeedAsync(services.GetRequiredService<ApplicationDbContext>());
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in Roles)
            if (!await roles.RoleExistsAsync(role))
                Ensure(await roles.CreateAsync(new IdentityRole(role)));

        if (!development) return;
        var email = configuration["DevelopmentAdmin:Email"];
        var password = configuration["DevelopmentAdmin:Password"];
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(password)) return;
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Configure both DevelopmentAdmin:Email and DevelopmentAdmin:Password using user secrets.");
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);
        // Preserve legacy development accounts; they must be corrected manually before signing in.
        if (user is not null && !Services.InstitutionalEmail.IsValid(email)) return;
        if (user is null)
        {
            user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = "Development Administrator" };
            Ensure(await users.CreateAsync(user, password));
        }
        var applicationRoles = (await users.GetRolesAsync(user)).Where(r => Roles.Contains(r)).ToArray();
        if (applicationRoles.Length != 1 || applicationRoles[0] != "Admin")
        {
            await using var transaction = await services.GetRequiredService<ApplicationDbContext>().Database.BeginTransactionAsync();
            if (applicationRoles.Length > 0) Ensure(await users.RemoveFromRolesAsync(user, applicationRoles));
            Ensure(await users.AddToRoleAsync(user, "Admin"));
            Ensure(await users.UpdateSecurityStampAsync(user));
            await transaction.CommitAsync();
        }
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException("Identity seeding failed: " + string.Join("; ", result.Errors.Select(e => e.Description)));
    }
}
