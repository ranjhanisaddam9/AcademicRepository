using System.Net;
using AcademicRepository.Data;
using AcademicRepository.Models;
using AcademicRepository.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static partial class IntegrationChecks
{
    static async Task Milestone2Checks(WebApplicationFactory<Program> factory, string password, RecordingAuthenticationEmailSender emailSender, AdjustableTestClock clock)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        using var admin = Client(factory);
        await LoginAsync(admin, "admin@smiu.edu.pk", password);
        Check(!(await admin.GetStringAsync("/Users/Create")).Contains("value=\"Student\""), "Admin create role dropdown excludes Student");
        var adminHome = await admin.GetStringAsync("/");
        Check(adminHome.Contains("Department: Not Assigned") && adminHome.Contains("Welcome, Development Administrator"), "Admin profile without department");
        Check(adminHome.Contains("href=\"/Departments\"") && adminHome.Contains("href=\"/Users\""), "Admin management navigation");

        var response = await PostFormAsync(admin, "/Departments/Create", new() { ["Name"] = "Test Department", ["Code"] = "test", ["IsActive"] = "false", ["Id"] = "999" });
        Check(response.StatusCode == HttpStatusCode.Redirect, "Admin creates department");
        var department = await db.Departments.AsNoTracking().SingleAsync(d => d.Code == "CS");
        var createdDepartment = await db.Departments.AsNoTracking().SingleAsync(d => d.Code == "TEST");
        Check(createdDepartment.IsActive && createdDepartment.Id != 999 && createdDepartment.CreatedAt > DateTime.UtcNow.AddMinutes(-5), "Department defaults, normalization and overposting protection");
        await InvalidPost(admin, "/Departments/Create", new() { ["Name"] = "computer science", ["Code"] = "OTHER" }, "name already exists", "Duplicate department name");
        await InvalidPost(admin, "/Departments/Create", new() { ["Name"] = "Other Name", ["Code"] = "cS" }, "code already exists", "Duplicate department code");
        await InvalidPost(admin, "/Departments/Create", new() { ["Name"] = " ", ["Code"] = " " }, "required", "Department required fields");
        await InvalidPost(admin, "/Departments/Create", new() { ["Name"] = new string('x', 121), ["Code"] = new string('X', 21) }, "maximum length", "Department maximum lengths");
        Check((await PostFormAsync(admin, $"/Departments/Edit/{department.Id}", new() { ["Name"] = "Computing", ["Code"] = "CS" })).StatusCode == HttpStatusCode.Redirect, "Edit department");
        var details = await admin.GetStringAsync($"/Departments/Details/{department.Id}");
        Check(details.Contains("Computing") && details.Contains("Updated (UTC)"), "Department details");
        await ToggleAsync(admin, "/Departments", department.Id.ToString(), false);
        Check(!await db.Departments.Where(d => d.Id == department.Id).Select(d => d.IsActive).SingleAsync(), "Deactivate department");
        await ToggleAsync(admin, "/Departments", department.Id.ToString(), true);
        Check(await db.Departments.Where(d => d.Id == department.Id).Select(d => d.IsActive).SingleAsync(), "Reactivate department");
        Check((await PostFormAsync(admin, "/Departments/Create", new() { ["Name"] = "Another Test Department", ["Code"] = "TEST2" })).StatusCode == HttpStatusCode.Redirect, "Create second department");
        var secondId = await db.Departments.Where(d => d.Code == "SE").Select(d => d.Id).SingleAsync();

        foreach (var role in IdentitySeeder.Roles.Where(r => r != "Student"))
        {
            var values = NewUser(role, password, role == "Admin" ? null : department.Id);
            Check((await PostFormAsync(admin, "/Users/Create", values)).StatusCode == HttpStatusCode.Redirect, "Admin creates " + role);
            if (ApplicationRoles.RequiresDepartment(role))
            {
                values = NewUser(role, password, null);
                values["Email"] = "missing-" + role + "@smiu.edu.pk";
                await InvalidPost(admin, "/Users/Create", values, "department is required", role + " requires department");
            }
        }
        var invalid = NewUser("Root", password, department.Id);
        await InvalidPost(admin, "/Users/Create", invalid, "valid application role", "Invalid role rejected");
        invalid = NewUser("Coordinator", password, 999999);
        invalid["Email"] = "invalid-dept@smiu.edu.pk";
        await InvalidPost(admin, "/Users/Create", invalid, "active department", "Invalid department rejected");
        invalid = NewUser("Student", password, department.Id);
        invalid["FullName"] = " "; invalid["Email"] = "invalid-email";
        await InvalidPost(admin, "/Users/Create", invalid, "required", "Full name and email validation");
        invalid = NewUser("Student", password, department.Id); invalid["Email"] = "not-an-email";
        await InvalidPost(admin, "/Users/Create", invalid, "valid e-mail", "Invalid email rejected");
        invalid = NewUser("Student", password, department.Id); invalid["Email"] = "outside@gmail.com";
        await InvalidPost(admin, "/Users/Create", invalid, "valid @smiu.edu.pk", "Non-SMIU account creation rejected");
        invalid = NewUser("Coordinator", password, department.Id); invalid["Email"] = "missing-password@smiu.edu.pk"; invalid.Remove("TemporaryPassword");
        await InvalidPost(admin, "/Users/Create", invalid, "Temporary password is required", "Required password error remains visible");
        invalid = NewUser("Coordinator", "weak", department.Id); invalid["Email"] = "weak@smiu.edu.pk";
        await InvalidPost(admin, "/Users/Create", invalid, "Passwords must", "Identity password validation");
        Check(!await db.Users.AnyAsync(u => u.Email == "weak@smiu.edu.pk"), "Failed user create is atomic");
        await InvalidPost(admin, "/Users/Create", NewUser("Coordinator", password, department.Id), "already assigned", "Duplicate user email");
        await InvalidPost(admin, "/Users/Create", NewUser("Student", password, department.Id), "Admins create staff accounts only", "Admin cannot register a Student");
        using (var registering = Client(factory)) await LoginStudentAsync(registering, "CSC20F002@smiu.edu.pk", emailSender);
        var registered = await db.Users.AsNoTracking().SingleAsync(u => u.Email == "csc20f002@smiu.edu.pk");
        var studentProfile = NewUser("Student", password, department.Id);
        Check((await PostFormAsync(admin, $"/Users/Edit/{registered.Id}", studentProfile)).StatusCode == HttpStatusCode.Redirect, "Admin may maintain a verified Student profile");
        clock.Advance(TimeSpan.FromSeconds(61));
        Check((await admin.PostAsync("/Users/Create", new FormUrlEncodedContent(NewUser("Student", password, department.Id)))).StatusCode == HttpStatusCode.BadRequest, "User management CSRF protection");
        Check((await admin.PostAsync("/Departments/Create", new FormUrlEncodedContent(new Dictionary<string,string> { ["Name"] = "Unprotected", ["Code"] = "X" }))).StatusCode == HttpStatusCode.BadRequest, "Department management CSRF protection");

        var student = await db.Users.AsNoTracking().SingleAsync(u => u.Email == "CSC20F002@smiu.edu.pk");
        var managementPage = await admin.GetStringAsync("/Users");
        Check((await admin.PostAsync($"/Users/SetActive/{student.Id}", Form(managementPage, new() { ["isActive"] = "invalid" }))).StatusCode == HttpStatusCode.BadRequest, "Invalid user status rejected");
        Check((await admin.PostAsync($"/Departments/SetActive/{department.Id}", Form(managementPage, new()))).StatusCode == HttpStatusCode.BadRequest, "Missing department status rejected");
        Check(student.IsActive && student.PasswordHash is null && student.EmailConfirmed, "Verified Student defaults to passwordless sign-in");
        Check((await admin.GetAsync($"/Users/ResetPassword/{student.Id}")).StatusCode == HttpStatusCode.BadRequest, "Admin cannot open Student password reset");
        var adminPage = await admin.GetStringAsync("/Users");
        Check((await admin.PostAsync($"/Users/ResetPassword/{student.Id}", Form(adminPage, new() { ["Password"] = password }))).StatusCode == HttpStatusCode.BadRequest, "Admin cannot POST Student password reset");
        using var studentClient = Client(factory);
        var studentLoginPage = await studentClient.GetStringAsync("/Account/StudentSignIn");
        var studentVerifyResponse = await studentClient.PostAsync("/Account/StudentSignIn", Form(studentLoginPage, new() { ["Email"] = student.Email! }));
        var studentVerifyPage = await studentVerifyResponse.Content.ReadAsStringAsync();
        await studentClient.PostAsync("/Account/VerifyStudentCode", Form(studentVerifyPage, new()
        {
            ["Email"] = student.Email!, ["ChallengeToken"] = HiddenValue(studentVerifyPage, "ChallengeToken"), ["Code"] = emailSender.Sent[^1].Code
        }));
        var home = await studentClient.GetStringAsync("/");
        Check(home.Contains("Welcome, Managed Student") && home.Contains("Department: Computing") && home.Contains("Role: Student"), "Student dashboard profile");
        Check(!(await studentClient.GetStringAsync("/Student/Dashboard")).Contains("Not Assigned"), "Role dashboard profile");
        clock.Advance(TimeSpan.FromSeconds(61));
        foreach (var role in IdentitySeeder.Roles.Where(r => r != "Admin"))
        {
            using var nonAdmin = Client(factory);
            var managedEmail = role == "Student" ? student.Email! : "managed-" + role + "@smiu.edu.pk";
            if (role == "Student") await LoginStudentAsync(nonAdmin, managedEmail, emailSender);
            else await LoginAsync(nonAdmin, managedEmail, password);
            var page = await nonAdmin.GetStringAsync("/");
            Check(!page.Contains("href=\"/Departments\"") && !page.Contains("href=\"/Users\""), role + " cannot see management navigation");
            foreach (var path in new[] { "/Departments", "/Departments/Create", $"/Departments/Edit/{department.Id}", $"/Departments/Details/{department.Id}", "/Users", "/Users/Create", $"/Users/Edit/{student.Id}", $"/Users/ResetPassword/{student.Id}" })
            {
                var blocked = await nonAdmin.GetAsync(path);
                Check(blocked.StatusCode == HttpStatusCode.Redirect && blocked.Headers.Location!.OriginalString.Contains("AccessDenied"), role + " blocked " + path);
            }
            foreach (var path in new[] { "/Departments/Create", $"/Departments/SetActive/{department.Id}", "/Users/Create", $"/Users/Edit/{student.Id}", $"/Users/SetActive/{student.Id}", $"/Users/ResetPassword/{student.Id}" })
            {
                var blocked = await nonAdmin.PostAsync(path, Form(page, new()));
                Check(blocked.StatusCode == HttpStatusCode.Redirect && blocked.Headers.Location!.OriginalString.Contains("AccessDenied"), role + " blocked POST " + path);
            }
        }
        using (var anonymous = Client(factory))
            foreach (var path in new[] { "/Users", "/Departments" })
                Check((await anonymous.GetAsync(path)).StatusCode == HttpStatusCode.Redirect, "Anonymous management protection " + path);

        // Role edits remove application roles only; unrelated claims remain untouched.
        db.UserClaims.Add(new Microsoft.AspNetCore.Identity.IdentityUserClaim<string> { UserId = student.Id, ClaimType = "test-system-claim", ClaimValue = "preserve" });
        await db.SaveChangesAsync();
        var edits = NewUser("Coordinator", password, secondId);
        edits["Email"] = student.Email!; edits["FullName"] = "Updated Student";
        var editPage = await admin.GetStringAsync($"/Users/Edit/{student.Id}");
        edits["EditToken"] = HiddenValue(editPage, "EditToken");
        Check(!editPage.Contains(student.ConcurrencyStamp!) && !editPage.Contains(student.SecurityStamp!) && student.PasswordHash is null, "Edit form hides Identity security fields and Student has no password hash");
        Check((await admin.PostAsync($"/Users/Edit/{student.Id}", Form(editPage, edits))).StatusCode == HttpStatusCode.Redirect, "Change user role and department");
        var roles = await (from assignment in db.UserRoles join role in db.Roles on assignment.RoleId equals role.Id where assignment.UserId == student.Id select role.Name).ToListAsync();
        Check(roles.SequenceEqual(new[] { "Coordinator" }) && await db.UserClaims.AnyAsync(c => c.UserId == student.Id && c.ClaimType == "test-system-claim"), "Single application role and preserved system claim");
        Check((await studentClient.GetAsync("/Student/Dashboard")).StatusCode == HttpStatusCode.Redirect, "Role change invalidates old session");
        await LoginAsync(studentClient, student.Email!, password);
        Check((await studentClient.GetAsync("/Coordinator/Dashboard")).StatusCode == HttpStatusCode.OK, "Edited role takes effect on login");
        home = await studentClient.GetStringAsync("/");
        Check(home.Contains("Welcome, Updated Student") && home.Contains("Department: Software Engineering") && home.Contains("Role: Coordinator"), "Edited dashboard profile");
        var listing = await admin.GetStringAsync("/Users");
        var staffHash = await db.Users.Where(u => u.Email == "managed-Coordinator@smiu.edu.pk").Select(u => u.PasswordHash).SingleAsync();
        Check(listing.Contains("Updated Student") && listing.Contains("Software Engineering") && listing.Contains("Coordinator") && staffHash is not null && !listing.Contains(staffHash), "User listing safe profile fields");
        await InvalidPost(admin, $"/Users/Edit/{student.Id}", edits, "Reload the edit page", "Stale user edit rejected");

        await ToggleAsync(admin, "/Users", student.Id, false);
        Check((await studentClient.GetAsync("/Coordinator/Dashboard")).StatusCode == HttpStatusCode.Redirect, "Deactivation invalidates existing session immediately");
        using var inactive = Client(factory);
        var loginPage = await inactive.GetStringAsync("/Account/Login");
        Check((await inactive.PostAsync("/Account/Login", Form(loginPage, new() { ["Email"] = student.Email!, ["Password"] = password }))).StatusCode == HttpStatusCode.OK, "Inactive user cannot login");
        await ToggleAsync(admin, "/Users", student.Id, true);
        await LoginAsync(inactive, student.Email!, password);
        Check((await inactive.GetAsync("/Coordinator/Dashboard")).StatusCode == HttpStatusCode.OK, "Reactivated user can login");
        await InvalidPost(admin, $"/Users/ResetPassword/{student.Id}", new() { ["Password"] = "weak" }, "Passwords must", "Reset validates Identity password policy");
        await InvalidPost(admin, $"/Users/ResetPassword/{student.Id}", new(), "required", "Required reset password error remains visible");
        var newPassword = "New!9a" + Guid.NewGuid().ToString("N");
        Check((await PostFormAsync(admin, $"/Users/ResetPassword/{student.Id}", new() { ["Password"] = newPassword })).StatusCode == HttpStatusCode.Redirect, "Admin resets password");
        Check((await inactive.GetAsync("/")).StatusCode == HttpStatusCode.Redirect, "Password reset revokes session");
        using var resetClient = Client(factory);
        loginPage = await resetClient.GetStringAsync("/Account/Login");
        Check((await resetClient.PostAsync("/Account/Login", Form(loginPage, new() { ["Email"] = student.Email!, ["Password"] = password }))).StatusCode == HttpStatusCode.OK, "Old password rejected after reset");
        await LoginAsync(resetClient, student.Email!, newPassword);

        await ToggleAsync(admin, "/Departments", secondId.ToString(), false);
        var retained = await db.Users.AsNoTracking().SingleAsync(u => u.Id == student.Id);
        Check(retained.DepartmentId == secondId && retained.IsActive && (await resetClient.GetAsync("/")).StatusCode == HttpStatusCode.OK, "Inactive department preserves assigned users and access");
        invalid = NewUser("Coordinator", password, secondId); invalid["Email"] = "inactive-dept@smiu.edu.pk";
        await InvalidPost(admin, "/Users/Create", invalid, "active department", "New inactive department assignment rejected");
        var filtered = await admin.GetStringAsync($"/Users?departmentId={secondId}");
        Check(filtered.Contains("Updated Student") && !filtered.Contains("managed-Admin@smiu.edu.pk"), "Users queried by department");
        var own = await db.Users.AsNoTracking().SingleAsync(u => u.Email == "admin@smiu.edu.pk");
        await ToggleAsync(admin, "/Users", own.Id, false);
        Check(await db.Users.Where(u => u.Id == own.Id).Select(u => u.IsActive).SingleAsync(), "Admin cannot deactivate self");
        var selfEdit = NewUser("Student", password, department.Id); selfEdit["Email"] = own.Email!;
        await InvalidPost(admin, $"/Users/Edit/{own.Id}", selfEdit, "own Admin role", "Admin cannot remove own Admin role");
        Check((await admin.GetAsync("/Departments/Edit/999999")).StatusCode == HttpStatusCode.NotFound && (await admin.GetAsync("/Users/Edit/missing-user")).StatusCode == HttpStatusCode.NotFound, "Missing records return 404");
    }

    static Dictionary<string,string> NewUser(string role, string password, int? departmentId)
    {
        var values = new Dictionary<string,string> { ["FullName"] = "Managed " + role, ["Email"] = "managed-" + role + "@smiu.edu.pk", ["Role"] = role, ["TemporaryPassword"] = password, ["IsActive"] = "false", ["PasswordHash"] = "injected" };
        if (role == "Student") values["Email"] = "CSC20F002@smiu.edu.pk";
        if (departmentId.HasValue) values["DepartmentId"] = departmentId.Value.ToString();
        return values;
    }
    static async Task LoginAsync(HttpClient client, string email, string password)
    {
        var page = await client.GetStringAsync("/Account/Login");
        Check((await client.PostAsync("/Account/Login", Form(page, new() { ["Email"] = email, ["Password"] = password }))).StatusCode == HttpStatusCode.Redirect, "Login " + email);
    }
    static async Task LoginStudentAsync(HttpClient client, string email, RecordingAuthenticationEmailSender emailSender)
    {
        var page = await client.GetStringAsync("/Account/StudentSignIn");
        var response = await client.PostAsync("/Account/StudentSignIn", Form(page, new() { ["Email"] = email }));
        var verify = await response.Content.ReadAsStringAsync();
        response = await client.PostAsync("/Account/VerifyStudentCode", Form(verify, new()
        {
            ["Email"] = email, ["ChallengeToken"] = HiddenValue(verify, "ChallengeToken"), ["Code"] = emailSender.Sent[^1].Code
        }));
        Check(response.StatusCode == HttpStatusCode.Redirect, "Passwordless Student login " + email);
    }
    static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, Dictionary<string,string> values)
    {
        var page = await client.GetStringAsync(path);
        if (path.StartsWith("/Users/Edit/")) values.TryAdd("EditToken", HiddenValue(page, "EditToken"));
        return await client.PostAsync(path, Form(page, values));
    }
    static async Task InvalidPost(HttpClient client, string path, Dictionary<string,string> values, string error, string message)
    {
        using var response = await PostFormAsync(client, path, values);
        Check(response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadAsStringAsync()).Contains(error, StringComparison.OrdinalIgnoreCase), message);
    }
    static async Task ToggleAsync(HttpClient client, string controllerPath, string id, bool active)
    {
        var page = await client.GetStringAsync(controllerPath);
        Check((await client.PostAsync($"{controllerPath}/SetActive/{id}", Form(page, new() { ["isActive"] = active.ToString() }))).StatusCode == HttpStatusCode.Redirect, controllerPath + " set active " + active);
    }
    static string HiddenValue(string page, string name) => WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(page, "name=\"" + name + "\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
}
