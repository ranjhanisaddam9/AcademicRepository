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
    static async Task OnboardingChecks(WebApplicationFactory<Program> factory,
        RecordingAuthenticationEmailSender sender, AdjustableTestClock clock)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var resolver = scope.ServiceProvider.GetRequiredService<StudentDepartmentService>();
        var codes = scope.ServiceProvider.GetRequiredService<AuthenticationCodeService>();
        var beforeDepartments = await db.Departments.AsNoTracking().OrderBy(d => d.Id).Select(d => new { d.Id, d.Code, d.Name, d.StudentEmailKeyword }).ToListAsync();
        await DepartmentSeeder.SeedAsync(db);
        await DepartmentSeeder.SeedAsync(db);
        Check(beforeDepartments.SequenceEqual(await db.Departments.AsNoTracking().OrderBy(d => d.Id).Select(d => new { d.Id, d.Code, d.Name, d.StudentEmailKeyword }).ToListAsync()), "Mapping seed preserves existing IDs, names and codes on repeated runs");
        foreach (var (keyword, _) in DepartmentSeeder.Mappings)
        {
            var department = await resolver.ResolveAsync($"{keyword}20F001@smiu.edu.pk");
            Check(department?.StudentEmailKeyword == keyword, "Database resolves Student keyword " + keyword);
            Check(StudentDepartmentService.StudentNumberFromEmail($"{keyword.ToLowerInvariant()}20f001@SMIU.EDU.PK") == $"{keyword}-20F-001", "Canonical StudentNumber " + keyword);
            // Earlier department-management tests deliberately deactivate Software Engineering.
            department!.IsActive = true;
            await db.SaveChangesAsync();
            var email = $"{keyword}20F900@smiu.edu.pk";
            using var client = Client(factory);
            var page = await client.GetStringAsync("/Account/StudentSignIn");
            Check(!page.Contains("type=\"password\""), "Student sign-in has no password field " + keyword);
            var response = await client.PostAsync("/Account/StudentSignIn", Form(page, new() { ["Email"] = email }));
            var verify = await response.Content.ReadAsStringAsync();
            Check(await users.FindByEmailAsync(email) is null, "OTP request does not create Identity account " + keyword);
            var challenge = HiddenValue(verify, "ChallengeToken");
            var code = sender.Sent[^1].Code;
            Check(await codes.VerifyAsync(challenge, "invalid", CodePurpose.StudentLogin) is null && await users.FindByEmailAsync(email) is null, "Incorrect OTP cannot onboard " + keyword);
            response = await client.PostAsync("/Account/VerifyStudentCode", Form(verify, new() { ["Email"] = email, ["ChallengeToken"] = challenge, ["Code"] = code }));
            var user = await users.FindByEmailAsync(email);
            Check(response.StatusCode == HttpStatusCode.Redirect && user is not null && user.EmailConfirmed && user.PasswordHash is null
                && user.StudentNumber == $"{keyword}-20F-900" && user.Id != user.StudentNumber && user.DepartmentId == department.Id && await users.IsInRoleAsync(user, "Student"), "Verified OTP creates mapped passwordless Student " + keyword);
            Check((await client.GetAsync("/Student/Dashboard")).StatusCode == HttpStatusCode.OK, "New Student receives Identity session " + keyword);
            db.ChangeTracker.Clear();
            Check(await codes.VerifyAsync(challenge, code, CodePurpose.StudentLogin) is null, "Onboarding OTP cannot be replayed " + keyword);
            var originalId = user!.Id;
            clock.Advance(TimeSpan.FromSeconds(61));
            var next = await codes.RequestAsync(email.ToUpperInvariant(), CodePurpose.StudentLogin);
            var reused = await codes.VerifyAsync(next, sender.Sent[^1].Code, CodePurpose.StudentLogin);
            Check(reused?.User.Id == originalId && await db.Users.CountAsync(u => u.NormalizedEmail == email.ToUpperInvariant()) == 1, "Existing account reused case-insensitively " + keyword);
        }
        Check(StudentDepartmentService.StudentNumberFromEmail("CSC20F005@smiu.edu.pk") == "CSC-20F-005", "Exact corrected Fall format accepted");
        Check(StudentDepartmentService.StudentNumberFromEmail("csc20s005@SMIU.EDU.PK") == "CSC-20S-005", "Spring format accepted case-insensitively");
        Check((await resolver.ResolveAsync("CSC20S005@smiu.edu.pk"))?.StudentEmailKeyword == "CSC", "Spring prefix maps to Computer Science");
        foreach (var invalid in new[] { "XYZ20F001@smiu.edu.pk", "", "16F-BUS-001@smiu.edu.pk", "BUS@smiu.edu.pk", "BUS20F001-extra@smiu.edu.pk", "CSC20X005@smiu.edu.pk", "CSC2F005@smiu.edu.pk", "CSC2020F005@smiu.edu.pk", "CSC20F05@smiu.edu.pk", "CSC20F0005@smiu.edu.pk", "20F005CSC@smiu.edu.pk" })
        {
            var count = sender.Sent.Length;
            await codes.RequestAsync(invalid, CodePurpose.StudentLogin);
            Check(await resolver.ResolveAsync(invalid) is null && sender.Sent.Length == count, "Reject malformed/unknown onboarding " + invalid);
        }
        foreach (var email in new[] { "student@smiu.edu.pk", "abc123@smiu.edu.pk", "coordinator@smiu.edu.pk" })
            Check(InstitutionalEmail.IsValid(email), "Required institutional domain example " + email);
        Check(!InstitutionalEmail.IsValid("student@othersmiu.edu.pk"), "Reject misleading institutional domain prefix");

        var pendingEmail = NextStudentEmail("CSC");
        var pendingToken = await codes.RequestAsync(pendingEmail, CodePurpose.StudentLogin);
        var pendingCode = sender.Sent[^1].Code;
        var sentCount = sender.Sent.Length;
        await codes.RequestAsync(pendingEmail, CodePurpose.StudentLogin);
        Check(sender.Sent.Length == sentCount, "Pending onboarding resend cooldown enforced");
        clock.Advance(TimeSpan.FromMinutes(11));
        Check(await codes.VerifyAsync(pendingToken, pendingCode, CodePurpose.StudentLogin) is null && await users.FindByEmailAsync(pendingEmail) is null, "Expired OTP never finalizes new Student");
        pendingToken = await codes.RequestAsync(pendingEmail, CodePurpose.StudentLogin);
        pendingCode = sender.Sent[^1].Code;
        for (var attempt = 0; attempt < 5; attempt++)
            Check(await codes.VerifyAsync(pendingToken, pendingCode == "000000" ? "111111" : "000000", CodePurpose.StudentLogin) is null, "Pending onboarding rejects invalid attempt " + attempt);
        Check(await codes.VerifyAsync(pendingToken, pendingCode, CodePurpose.StudentLogin) is null && await users.FindByEmailAsync(pendingEmail) is null, "Pending onboarding attempt limit prevents account creation");
        clock.Advance(TimeSpan.FromSeconds(61));
        var freshToken = await codes.RequestAsync(pendingEmail, CodePurpose.StudentLogin);
        var freshCode = sender.Sent[^1].Code;
        Check(await codes.VerifyAsync(pendingToken, pendingCode, CodePurpose.StudentLogin) is null, "New OTP invalidates the previous challenge");
        using (var firstScope = factory.Services.CreateScope())
        using (var secondScope = factory.Services.CreateScope())
        {
            var attempts = await Task.WhenAll(firstScope.ServiceProvider.GetRequiredService<AuthenticationCodeService>().VerifyAsync(freshToken, freshCode, CodePurpose.StudentLogin),
                secondScope.ServiceProvider.GetRequiredService<AuthenticationCodeService>().VerifyAsync(freshToken, freshCode, CodePurpose.StudentLogin));
            Check(attempts.Count(a => a is not null) == 1 && await db.Users.CountAsync(u => u.Email == pendingEmail) == 1, "Concurrent OTP verification creates exactly one account and one successful result");
        }
        db.ChangeTracker.Clear();
        foreach (var role in ApplicationRoles.Staff)
        {
            var staffEmail = role + "@smiu.edu.pk";
            clock.Advance(TimeSpan.FromHours(1));
            var token = await codes.RequestAsync(staffEmail, CodePurpose.StaffRecovery);
            var code = sender.Sent[^1].Code;
            sentCount = sender.Sent.Length;
            await codes.RequestAsync(staffEmail, CodePurpose.StaffRecovery);
            Check(sender.Sent.Length == sentCount, role + " recovery resend cooldown");
            for (var attempt = 0; attempt < 5; attempt++)
                Check(await codes.VerifyAsync(token, code == "000000" ? "111111" : "000000", CodePurpose.StaffRecovery) is null, role + " incorrect recovery OTP " + attempt);
            Check(await codes.VerifyAsync(token, code, CodePurpose.StaffRecovery) is null, role + " recovery attempt limit");
            clock.Advance(TimeSpan.FromSeconds(61));
            token = await codes.RequestAsync(staffEmail, CodePurpose.StaffRecovery);
            code = sender.Sent[^1].Code;
            clock.Advance(TimeSpan.FromMinutes(11));
            Check(await codes.VerifyAsync(token, code, CodePurpose.StaffRecovery) is null, role + " recovery OTP expiry");
            token = await codes.RequestAsync(staffEmail, CodePurpose.StaffRecovery);
            var verified = await codes.VerifyAsync(token, sender.Sent[^1].Code, CodePurpose.StaffRecovery);
            Check(verified?.RecoveryGrant is not null, role + " verified recovery grant issued");
            clock.Advance(TimeSpan.FromMinutes(11));
            Check(!(await codes.ResetStaffPasswordAsync(verified!.RecoveryGrant!, "Expired!9Password")).Succeeded, role + " expired recovery grant cannot reset password");
        }

        var existing = await users.FindByEmailAsync("BUS20F900@smiu.edu.pk");
        var originalDepartment = existing!.DepartmentId;
        existing.DepartmentId = await db.Departments.Where(d => d.StudentEmailKeyword == "CSC").Select(d => d.Id).SingleAsync();
        await db.SaveChangesAsync();
        sentCount = sender.Sent.Length;
        await codes.RequestAsync(existing.Email!, CodePurpose.StudentLogin);
        Check(!await resolver.MatchesAsync(existing) && sender.Sent.Length == sentCount, "Existing Student with mismatched department cannot request sign-in OTP");
        existing.DepartmentId = originalDepartment;
        var originalNumber = existing.StudentNumber;
        existing.StudentNumber = null;
        await db.SaveChangesAsync();
        Check(!await resolver.MatchesAsync(existing), "Unmigrated StudentNumber cannot authenticate");
        existing.StudentNumber = originalNumber;
        await db.SaveChangesAsync();
        var duplicate = new ApplicationUser { UserName = "different@smiu.edu.pk", Email = "different@smiu.edu.pk", StudentNumber = existing!.StudentNumber!.ToLowerInvariant() };
        db.Users.Add(duplicate);
        try { await db.SaveChangesAsync(); Check(false, "Duplicate StudentNumber must fail"); }
        catch (DbUpdateException) { Check(true, "Database rejects duplicate StudentNumber case-insensitively"); db.ChangeTracker.Clear(); }
        duplicate = new ApplicationUser { UserName = "other@smiu.edu.pk", NormalizedUserName = "OTHER@SMIU.EDU.PK", Email = existing.Email, NormalizedEmail = existing.NormalizedEmail };
        db.Users.Add(duplicate);
        try { await db.SaveChangesAsync(); Check(false, "Duplicate email must fail"); }
        catch (DbUpdateException) { Check(true, "Database rejects duplicate normalized email with different username"); db.ChangeTracker.Clear(); }

        foreach (var failure in new Exception[] { new HttpRequestException("simulated Graph failure"), new TaskCanceledException("simulated Graph timeout") })
        {
            sender.Failure = failure;
            var email = NextStudentEmail("CSC");
            try
            {
                var token = await codes.RequestAsync(email, CodePurpose.StudentLogin);
                var row = await db.AuthenticationCodes.SingleAsync(c => c.Email == email.ToUpperInvariant());
                Check(row.ConsumedAt.HasValue && await codes.VerifyAsync(token, sender.LastAttemptedCode!, CodePurpose.StudentLogin) is null
                    && await users.FindByEmailAsync(email) is null, "Failed delivery invalidates onboarding code: " + failure.GetType().Name);
            }
            finally { sender.Failure = null; }
        }
    }
}
