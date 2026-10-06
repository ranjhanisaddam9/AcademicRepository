using System.Net;
using System.Text.RegularExpressions;
using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static partial class IntegrationChecks
{
public static async Task Main()
{
// Real SQL Server integration checks. Credentials are generated for this run only.
var database = "AcademicRepository_Test_" + Guid.NewGuid().ToString("N");
var connection = $"Server=.\\SQLEXPRESS;Database={database};Trusted_Connection=True;TrustServerCertificate=True";
var password = "Test!9a" + Guid.NewGuid().ToString("N");
var email = "admin@example.test";
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
    await db.Database.MigrateAsync();
    var preserved = await db.Users.AsNoTracking().SingleAsync(u => u.Id == legacy.Id);
    Check(preserved.IsActive && preserved.FullName == legacy.UserName && preserved.PasswordHash == legacyHash && preserved.DepartmentId is null, "Milestone 1 user/profile/password preserved by migration");
    using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:DefaultConnection", connection);
        builder.UseSetting("DevelopmentAdmin:Email", email);
        builder.UseSetting("DevelopmentAdmin:Password", password);
    });
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
        Check(await db.Roles.CountAsync() == 4 && await db.Users.CountAsync() == 2, "Idempotent development seeding");
        foreach (var role in IdentitySeeder.Roles.Where(r => r != "Admin"))
        {
            var user = new ApplicationUser { UserName = role + "@example.test", Email = role + "@example.test" };
            Check((await users.CreateAsync(user, password)).Succeeded, "Create test " + role);
            Check((await users.AddToRoleAsync(user, role)).Succeeded, "Assign test " + role);
        }
    }
    foreach (var role in IdentitySeeder.Roles)
    {
        using var client = Client(factory);
        var login = await client.GetStringAsync("/Account/Login");
        var response = await client.PostAsync("/Account/Login", Form(login, new() { ["Email"] = role == "Admin" ? email : role + "@example.test", ["Password"] = password, ["ReturnUrl"] = "https://example.test/unsafe" }));
        Check(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location!.OriginalString == "/", role + " login and safe return URL");
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
        Check((await legacyClient.PostAsync("/Account/Login", Form(login, new() { ["Email"] = legacy.Email!, ["Password"] = password }))).StatusCode == HttpStatusCode.Redirect, "Existing Milestone 1 user can still log in");
    }
    await Milestone2Checks(factory, password);
    Console.WriteLine("PASS: All Milestone 1 and Milestone 2 SQL/MVC integration checks.");
}
finally { await db.Database.EnsureDeletedAsync(); }
}

static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
static FormUrlEncodedContent Form(string html, Dictionary<string,string> values)
{
    var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
    Check(match.Success, "Antiforgery token rendered");
    values["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value);
    return new FormUrlEncodedContent(values);
}
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}
}
