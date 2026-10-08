using System.Net;
using AcademicRepository.Data;
using AcademicRepository.Models;
using AcademicRepository.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static partial class IntegrationChecks
{
    static async Task Milestone10Checks(WebApplicationFactory<Program> factory, string password)
    {
        using var client = Client(factory);
        await LoginAsync(client, "admin@smiu.edu.pk", password);
        using (var securedResponse = await client.GetAsync("/Admin/Dashboard"))
            Check(securedResponse.Headers.Contains("X-Content-Type-Options")
                && securedResponse.Headers.GetValues("X-Content-Type-Options").Single() == "nosniff"
                && securedResponse.Headers.GetValues("X-Frame-Options").Single() == "DENY"
                && securedResponse.Headers.Contains("Content-Security-Policy"), "Security headers are applied to authenticated pages");
        foreach (var path in new[] { "/Admin/Dashboard", "/Admin/Overview", "/Admin/SystemInformation", "/Admin/Diagnostics", "/Admin/Settings", "/Departments", "/Users" })
            Check((await client.GetAsync(path)).StatusCode == HttpStatusCode.OK, "M10 Admin page available: " + path);

        var info = await client.GetStringAsync("/Admin/SystemInformation");
        Check(info.Contains("Database") && info.Contains("File storage") && info.Contains("Microsoft Graph")
            && !info.Contains("ClientSecret", StringComparison.OrdinalIgnoreCase) && !info.Contains("DefaultConnection", StringComparison.Ordinal),
            "System information shows safe health/configuration status without secrets or connection details");
        var diagnostics = await client.GetStringAsync("/Admin/Diagnostics");
        Check(diagnostics.Contains("Read-only") && !diagnostics.Contains(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase),
            "Diagnostics are read-only and do not disclose storage paths");
        var studentList = await client.GetStringAsync("/Users?role=Student&search=CSC20F005");
        Check(studentList.Contains("CSC-20F-005") && studentList.Contains("Student ID") && !studentList.Contains("Edit staff"),
            "Admin can filter and view Student identity but is not offered Student profile editing");
        var cscStudent = studentList.Contains("CSC20F005@smiu.edu.pk", StringComparison.OrdinalIgnoreCase);
        Check(cscStudent, "Database-backed Student filter finds the expected Student by email and Student ID");
        var editStudent = await client.GetAsync("/Users/Edit/" + Uri.EscapeDataString((await ReadUserId(factory, "CSC20F005@smiu.edu.pk"))));
        Check(editStudent.StatusCode == HttpStatusCode.Forbidden || (editStudent.StatusCode == HttpStatusCode.Redirect
            && editStudent.Headers.Location?.OriginalString.Contains("AccessDenied", StringComparison.Ordinal) == true),
            "Admin cannot edit a Student's derived email, ID or department");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var departments = await db.Departments.AsNoTracking().ToListAsync();
        Check(departments.Select(d => d.StudentEmailKeyword).Where(k => k != null).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            == departments.Count(d => d.StudentEmailKeyword != null), "Department Student email keywords remain unique in the database");
        var settingsService = scope.ServiceProvider.GetRequiredService<IOperationalSettingsService>();
        var settings = await settingsService.GetAsync();
        settings.ApplicationDisplayName = "Academic Repository M10 Test";
        settings.SupportEmail = "support@smiu.edu.pk";
        settings.RepositoryPageSize = 10;
        var adminId = await ReadUserId(factory, "admin@smiu.edu.pk");
        await settingsService.UpdateAsync(settings, adminId);
        var persisted = await settingsService.GetAsync();
        Check(persisted.ApplicationDisplayName == settings.ApplicationDisplayName && persisted.SupportEmail == settings.SupportEmail
            && persisted.RepositoryPageSize == 10 && await db.SystemSettings.CountAsync() == 9,
            "Validated nonsecret operational settings persist with minimal updater accountability");
        var updatedHome = await client.GetStringAsync("/");
        Check(updatedHome.Contains("Academic Repository M10 Test") && updatedHome.Contains("support@smiu.edu.pk"),
            "Application display name and support contact apply dynamically at runtime");
        Check(await db.Database.GetAppliedMigrationsAsync() is var migrations && migrations.Last().Contains("AddAdministrativeConfiguration"),
            "M10 administrative schema migration is applied to the isolated integration database");
    }

    private static async Task<string> ReadUserId(WebApplicationFactory<Program> factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email) is { } user
            ? user.Id : throw new InvalidOperationException("Expected test user not found.");
    }
}
