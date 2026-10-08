using System.Net;
using System.Text.RegularExpressions;
using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AcademicRepository.Services;

internal static partial class IntegrationChecks
{
public static async Task Main(string[] args)
{
if (args is ["--audit"]) { await AuditDatabaseAsync(); return; }
if (args is ["--email-only"]) { await Office365EmailChecks(); return; }
if (args is ["--pagination-only"]) { PaginationOnly(); return; }
if (args is ["--errors-only"]) { await ErrorHandlingChecks(); return; }
if (args is ["--security-only"]) { await SecurityChecks(); return; }
if (args is ["--accessibility-only"]) { AccessibilityChecks(); return; }
if (args is ["--concurrency-only"]) { ConcurrencyChecks.Run(); return; }
if (args is ["--login-ui-only"]) { LoginUiChecks.Run(); return; }
await Office365EmailChecks();
await MigrationUpgradeChecks();
// Real SQL Server integration checks. Credentials are generated for this run only.
var database = "AcademicRepository_Test_" + Guid.NewGuid().ToString("N");
var storageRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AcademicRepository_Files_" + Guid.NewGuid().ToString("N")));
var connection = $"Server=.\\SQLEXPRESS;Database={database};Trusted_Connection=True;TrustServerCertificate=True";
var password = "Test!9a" + Guid.NewGuid().ToString("N");
var email = "admin@smiu.edu.pk";
var testClock = new AdjustableTestClock(DateTimeOffset.UtcNow);
var testEmailSender = new RecordingAuthenticationEmailSender();
var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connection).Options;
await using var db = new ApplicationDbContext(options);
try
{
    // Start with Milestone 1 schema/data, then apply the additive migration.
    await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db.Database)
        .MigrateAsync("20261006061443_InitialIdentity");
    var legacy = new ApplicationUser { Id = "legacy-user", UserName = "legacy@example.test", Email = "legacy@example.test" };
    var legacyHash = new PasswordHasher<ApplicationUser>().HashPassword(legacy, password);
    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AspNetUsers (Id,UserName,NormalizedUserName,Email,NormalizedEmail,EmailConfirmed,PasswordHash,SecurityStamp,ConcurrencyStamp,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount) VALUES ({legacy.Id},{legacy.UserName},{legacy.UserName.ToUpperInvariant()},{legacy.Email},{legacy.Email.ToUpperInvariant()},0,{legacyHash},{Guid.NewGuid().ToString()},{Guid.NewGuid().ToString()},0,0,1,0)");
    await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db.Database)
        .MigrateAsync("20261006064402_AddDepartmentsAndUserProfiles");
    var preserved = await db.Users.FromSqlRaw("SELECT *, CAST(NULL AS nvarchar(11)) AS StudentNumber, CAST(NULL AS datetime2) AS CreatedAt FROM AspNetUsers").AsNoTracking().SingleAsync(u => u.Id == legacy.Id);
    Check(preserved.IsActive && preserved.FullName == legacy.UserName && preserved.PasswordHash == legacyHash && preserved.DepartmentId is null, "Milestone 1 user/profile/password preserved by migration");
    var existingDepartment = new Department { Name = "Pre-existing Department", Code = "LEG" };
    await db.Database.ExecuteSqlRawAsync("INSERT INTO Departments (Name,Code,CreatedAt,IsActive) VALUES ('Pre-existing Department','LEG',SYSUTCDATETIME(),1)");
    existingDepartment.Id = await db.Database.SqlQueryRaw<int>("SELECT Id AS Value FROM Departments WHERE Code = 'LEG'").SingleAsync();
    var existingProfile = await db.Users.FromSqlRaw("SELECT *, CAST(NULL AS nvarchar(11)) AS StudentNumber, CAST(NULL AS datetime2) AS CreatedAt FROM AspNetUsers").SingleAsync(u => u.Id == legacy.Id);
    existingProfile.FullName = "Existing Profile";
    existingProfile.DepartmentId = existingDepartment.Id;
    await db.SaveChangesAsync();
    await db.Database.MigrateAsync();
    db.Departments.AddRange(new Department { Code = "CS", Name = "Computer Science" },
        new Department { Code = "SE", Name = "Software Engineering" });
    await db.SaveChangesAsync();
    var upgradedProfile = await db.Users.AsNoTracking().SingleAsync(u => u.Id == legacy.Id);
    Check(upgradedProfile.FullName == "Existing Profile" && upgradedProfile.DepartmentId == existingDepartment.Id && upgradedProfile.PasswordHash == legacyHash
        && await db.Departments.AnyAsync(d => d.Id == existingDepartment.Id && d.Code == "LEG"), "Milestone 3 upgrade preserves Milestone 2 department/profile/password data");
    using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:DefaultConnection", connection);
        builder.UseSetting("DevelopmentAdmin:Email", email);
        builder.UseSetting("DevelopmentAdmin:Password", password);
        builder.UseSetting("AuthenticationCodes:IpPermitLimit", "1000");
        builder.UseSetting("FileStorage:RootPath", storageRoot);
        builder.UseSetting("FileStorage:MaxFileSizeMB", "1");
        builder.UseSetting("FileStorage:MaxFilesPerSubmission", "3");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAuthenticationEmailSender>();
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(testClock);
            services.AddSingleton(testEmailSender);
            services.AddSingleton<IAuthenticationEmailSender>(testEmailSender);
        });
    });
    using (var isolationScope = factory.Services.CreateScope())
    {
        var appConnection = isolationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.GetConnectionString();
        var appDatabase = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(appConnection).InitialCatalog;
        Check(string.Equals(appDatabase, database, StringComparison.OrdinalIgnoreCase), "MVC integration host is bound to the generated isolated test database");
    }
    using var anonymous = Client(factory);
    foreach (var path in IdentitySeeder.Roles.Select(r => $"/{r}/Dashboard").Append("/"))
    {
        var response = await anonymous.GetAsync(path);
        Check(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location!.OriginalString.Contains("/Account/Login"), "Anonymous protection " + path);
    }
    Check((await anonymous.GetAsync("/Account/Register")).StatusCode == HttpStatusCode.NotFound, "Registration disabled");
    Check((await anonymous.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string,string> { ["Email"] = email, ["Password"] = password }))).StatusCode == HttpStatusCode.BadRequest, "Login CSRF protection");
    using (var scope = factory.Services.CreateScope())
    {
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var config = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        await IdentitySeeder.SeedAsync(scope.ServiceProvider, config, true);
        await IdentitySeeder.SeedAsync(scope.ServiceProvider, config, true);
        Check(await db.Roles.CountAsync() == IdentitySeeder.Roles.Length && await db.Users.CountAsync() == 2, "Idempotent development seeding");
        var fixtureDepartmentId = await db.Departments.Where(d => d.Code == "LEG").Select(d => d.Id).SingleAsync();
        legacy.DepartmentId = fixtureDepartmentId;
        var studentRole = await db.Roles.SingleAsync(r => r.Name == "Student");
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = legacy.Id, RoleId = studentRole.Id });
        await db.SaveChangesAsync();
        foreach (var role in IdentitySeeder.Roles.Where(r => r != "Admin"))
        {
            var fixtureEmail = role == "Student" ? "CSC20F001@smiu.edu.pk" : role + "@smiu.edu.pk";
            var user = new ApplicationUser { UserName = fixtureEmail, Email = fixtureEmail, DepartmentId = role == "Student"
                ? await db.Departments.Where(d => d.Code == "CS").Select(d => d.Id).SingleAsync()
                : ApplicationRoles.RequiresDepartment(role) ? fixtureDepartmentId : null };
            user.StudentNumber = role == "Student" ? StudentDepartmentService.StudentNumberFromEmail(fixtureEmail) : null;
            Check((await users.CreateAsync(user, password)).Succeeded, "Create test " + role);
            Check((await users.AddToRoleAsync(user, role)).Succeeded, "Assign test " + role);
        }
    }
    foreach (var role in IdentitySeeder.Roles)
    {
        using var client = Client(factory);
        var targetEmail = role == "Admin" ? email : role == "Student" ? "CSC20F001@smiu.edu.pk" : role + "@smiu.edu.pk";
        HttpResponseMessage response;
        if (role == "Student")
        {
            var login = await client.GetStringAsync("/Account/StudentSignIn");
            response = await client.PostAsync("/Account/StudentSignIn", Form(login, new() { ["Email"] = targetEmail }));
            var verify = await response.Content.ReadAsStringAsync();
            response = await client.PostAsync("/Account/VerifyStudentCode", Form(verify, new()
            {
                ["Email"] = targetEmail,
                ["ChallengeToken"] = HiddenValue(verify, "ChallengeToken"),
                ["Code"] = testEmailSender.Sent[^1].Code
            }));
            Check(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location!.OriginalString.Contains("Student/Dashboard"), role + " passwordless login");
        }
        else
        {
            var login = await client.GetStringAsync("/Account/Login");
            response = await client.PostAsync("/Account/Login", Form(login, new() { ["Email"] = targetEmail, ["Password"] = password, ["ReturnUrl"] = "https://example.test/unsafe" }));
            Check(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location!.OriginalString == "/", role + " login and safe return URL");
        }
        var home = await client.GetStringAsync("/");
        Check(home.Contains("Academic Project Repository") && home.Contains("Welcome,") && home.Contains("Role: " + role), role + " home");
        foreach (var target in IdentitySeeder.Roles)
        {
            response = await client.GetAsync($"/{target}/Dashboard");
            Check(target == role ? response.StatusCode == HttpStatusCode.OK : response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location!.OriginalString.Contains("/Account/AccessDenied"), $"{role} -> {target}");
            Check(home.Contains($"href=\"/{target}/Dashboard\"") == (role == target), role + " navigation " + target);
        }
        Check((await client.GetAsync("/Account/AccessDenied")).StatusCode == HttpStatusCode.Forbidden, "Access Denied 403");
        Check((await client.GetAsync("/Account/Logout")).StatusCode == HttpStatusCode.MethodNotAllowed, "Logout is POST only");
        Check((await client.PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode == HttpStatusCode.BadRequest, "Logout CSRF protection");
        response = await client.PostAsync("/Account/Logout", Form(home, new()));
        Check(response.StatusCode == HttpStatusCode.Redirect, role + " logout");
        Check((await client.GetAsync($"/{role}/Dashboard")).StatusCode == HttpStatusCode.Redirect, role + " session removed");
    }
    using (var legacyClient = Client(factory))
    {
        var login = await legacyClient.GetStringAsync("/Account/Login");
        var legacyResponse = await legacyClient.PostAsync("/Account/Login", Form(login, new() { ["Email"] = legacy.Email!, ["Password"] = password }));
        Check(legacyResponse.StatusCode == HttpStatusCode.OK && (await legacyResponse.Content.ReadAsStringAsync()).Contains("@smiu.edu.pk"), "Existing non-SMIU account is preserved but must be corrected to sign in");
    }
    await Milestone2Checks(factory, password, testEmailSender, testClock);
    await Milestone3Checks(factory, password, testEmailSender);
    await Milestone4Checks(factory, testEmailSender, storageRoot, password);
    await Milestone5Checks(factory, testEmailSender, testClock, password);
    await Milestone6Checks(factory, testEmailSender, testClock, password);
    await Milestone7Checks(factory, testEmailSender, testClock, password);
    await Milestone8Checks(factory, password);
    await Milestone9Checks(factory, testEmailSender, testClock, password);
    await Milestone10Checks(factory, password);
    await Milestone35Checks(factory, password, testEmailSender, testClock);
    await OnboardingChecks(factory, testEmailSender, testClock);
    Console.WriteLine("PASS: All Milestone 1, 2, 3, 3.5, 4, 5, 6, 7, 8, 9 and 10 SQL/MVC integration checks.");
}
finally
{
    await db.Database.EnsureDeletedAsync();
    if (storageRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(storageRoot).StartsWith("AcademicRepository_Files_", StringComparison.Ordinal) && Directory.Exists(storageRoot))
        Directory.Delete(storageRoot, true);
}
}

static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
static FormUrlEncodedContent Form(string html, Dictionary<string,string> values)
{
    var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
    Check(match.Success, "Antiforgery token rendered");
    var fields = new Dictionary<string, string>(values)
    {
        ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value)
    };
    return new FormUrlEncodedContent(fields);
}
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}
}
