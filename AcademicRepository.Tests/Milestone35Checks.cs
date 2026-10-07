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
    static async Task Milestone35Checks(WebApplicationFactory<Program> factory, string password,
        RecordingAuthenticationEmailSender emailSender, AdjustableTestClock clock)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var department = await db.Departments.AsNoTracking().SingleAsync(d => d.Code == "CS");

        foreach (var validEmail in new[] { "user@smiu.edu.pk", "USER@SMIU.EDU.PK", "first.last+tag@smiu.edu.pk" })
            Check(InstitutionalEmail.IsValid(validEmail), "Accept institutional email " + validEmail);
        foreach (var invalid in new[] { "", "user@gmail.com", "user@yahoo.com", "user@outlook.com", "user@smiu.edu.pk.fake.com", "user@smiu.edu.com", "@smiu.edu.pk", "user@@smiu.edu.pk", "user name@smiu.edu.pk" })
            Check(!InstitutionalEmail.IsValid(invalid), "Reject non-institutional/malformed email " + invalid);

        var otpStudent = await CreateStudentAsync(users, "otp-student", department.Id, password);
        var studentOne = Client(factory);
        var signInPage = await studentOne.GetStringAsync("/Account/StudentSignIn");
        var beforeSend = emailSender.Sent.Length;
        var requested = await studentOne.PostAsync("/Account/StudentSignIn", Form(signInPage, new() { ["Email"] = otpStudent.Email! }));
        var verification = await requested.Content.ReadAsStringAsync();
        Check(requested.StatusCode == HttpStatusCode.OK && verification.Contains("If an eligible active account exists") && verification.Contains("Verify Student Code"), "Student requests generic verification challenge");
        Check(emailSender.Sent.Length == beforeSend + 1 && emailSender.Sent[^1] is { Purpose: CodePurpose.StudentLogin } sentStudent && sentStudent.Email == otpStudent.Email, "Student OTP sent via email abstraction");
        var challenge = HiddenValue(verification, "ChallengeToken");
        var code = emailSender.Sent[^1].Code;
        Check(code.Length == 6 && code.All(char.IsAsciiDigit), "Student OTP is six random digits");
        var codeRow = await db.AuthenticationCodes.SingleAsync(c => c.UserId == otpStudent.Id && c.Purpose == CodePurpose.StudentLogin);
        Check(codeRow.CodeHash.Length == 64 && codeRow.CodeHash != code && codeRow.Attempts == 0, "OTP is stored as keyed digest, never plaintext");
        Check(!await users.HasPasswordAsync(otpStudent), "New Student has no password hash");
        Check((await users.AddPasswordAsync(otpStudent, password)).Succeeded, "Create legacy Student password for negative test");
        using (var passwordLogin = Client(factory))
        {
            var page = await passwordLogin.GetStringAsync("/Account/Login");
            var attempt = await passwordLogin.PostAsync("/Account/Login", Form(page, new() { ["Email"] = otpStudent.Email!, ["Password"] = password }));
            Check(attempt.StatusCode == HttpStatusCode.OK && (await attempt.Content.ReadAsStringAsync()).Contains("Invalid login attempt"), "Student password login blocked even with old hash");
        }

        // Adding an old-style password changes the Identity security stamp, so obtain a fresh OTP.
        clock.Advance(TimeSpan.FromSeconds(61));
        signInPage = await studentOne.GetStringAsync("/Account/StudentSignIn");
        requested = await studentOne.PostAsync("/Account/StudentSignIn", Form(signInPage, new() { ["Email"] = otpStudent.Email! }));
        verification = await requested.Content.ReadAsStringAsync();
        challenge = HiddenValue(verification, "ChallengeToken");
        code = emailSender.Sent[^1].Code;
        Check(emailSender.Sent[^1].Email == otpStudent.Email, "OTP refreshed after Identity security stamp change");
        beforeSend = emailSender.Sent.Length;
        var cooldownPage = await studentOne.GetStringAsync("/Account/StudentSignIn");
        var cooldown = await studentOne.PostAsync("/Account/StudentSignIn", Form(cooldownPage, new() { ["Email"] = otpStudent.Email! }));
        Check(cooldown.StatusCode == HttpStatusCode.OK && emailSender.Sent.Length == beforeSend, "OTP resend cooldown suppresses repeated delivery");
        var valid = await studentOne.PostAsync("/Account/VerifyStudentCode", Form(verification, new() { ["ChallengeToken"] = challenge, ["Email"] = otpStudent.Email!, ["Code"] = code }));
        Check(valid.StatusCode == HttpStatusCode.Redirect && valid.Headers.Location!.OriginalString.Contains("/Student/Dashboard"), "Valid Student OTP signs in");
        var dashboard = await studentOne.GetStringAsync("/Student/Dashboard");
        Check(dashboard.Contains("Student otp-student") && dashboard.Contains("Computing"), "OTP sign-in reaches assigned Student dashboard");
        var codeService = scope.ServiceProvider.GetRequiredService<AuthenticationCodeService>();
        Check(await codeService.VerifyAsync(challenge, code, CodePurpose.StudentLogin) is null, "Used Student OTP is rejected");
        db.Entry(codeRow).State = EntityState.Detached;

        var oldDomainPage = await studentOne.GetStringAsync("/Account/StudentSignIn");
        var oldDomain = await studentOne.PostAsync("/Account/StudentSignIn", Form(oldDomainPage, new() { ["Email"] = "legacy@example.test" }));
        Check(oldDomain.StatusCode == HttpStatusCode.OK && (await oldDomain.Content.ReadAsStringAsync()).Contains("valid @smiu.edu.pk"), "Non-SMIU account must be corrected manually");
        using (var typoDomain = Client(factory))
        {
            var page = await typoDomain.GetStringAsync("/Account/StudentSignIn");
            var result = await typoDomain.PostAsync("/Account/StudentSignIn", Form(page, new() { ["Email"] = "user@smiu.edu.pk.fake.com" }));
            Check(result.StatusCode == HttpStatusCode.OK && (await result.Content.ReadAsStringAsync()).Contains("valid @smiu.edu.pk"), "Student OTP rejects fake domain suffix");
        }

        var codeCount = await db.AuthenticationCodes.CountAsync();
        beforeSend = emailSender.Sent.Length;
        using (var unknownClient = Client(factory))
        {
            var page = await unknownClient.GetStringAsync("/Account/StudentSignIn");
            var result = await unknownClient.PostAsync("/Account/StudentSignIn", Form(page, new() { ["Email"] = "missing@smiu.edu.pk" }));
            Check(result.StatusCode == HttpStatusCode.OK && (await result.Content.ReadAsStringAsync()).Contains("If an eligible active account exists"), "Nonexistent SMIU Student receives generic challenge");
        }
        Check(await db.AuthenticationCodes.CountAsync() == codeCount && emailSender.Sent.Length == beforeSend, "Nonexistent Student receives no code and leaves no OTP record");

        var rateLimitedStudent = await CreateStudentAsync(users, "rate-limited-otp", department.Id, password);
        beforeSend = emailSender.Sent.Length;
        await codeService.RequestAsync(rateLimitedStudent.Email!, CodePurpose.StudentLogin);
        for (var request = 1; request < 5; request++)
        {
            clock.Advance(TimeSpan.FromSeconds(61));
            await codeService.RequestAsync(rateLimitedStudent.Email!, CodePurpose.StudentLogin);
        }
        Check(emailSender.Sent.Length == beforeSend + 5, "Five OTP deliveries are allowed within the hourly window");
        clock.Advance(TimeSpan.FromSeconds(61));
        await codeService.RequestAsync(rateLimitedStudent.Email!, CodePurpose.StudentLogin);
        Check(emailSender.Sent.Length == beforeSend + 5, "Hourly OTP request limit suppresses additional email");

        var expiredStudent = await CreateStudentAsync(users, "expired-otp", department.Id, password);
        var expiredClient = Client(factory);
        var expiredForm = await expiredClient.GetStringAsync("/Account/StudentSignIn");
        var expiredChallenge = await expiredClient.PostAsync("/Account/StudentSignIn", Form(expiredForm, new() { ["Email"] = expiredStudent.Email! }));
        var expiredHtml = await expiredChallenge.Content.ReadAsStringAsync();
        var expiredToken = HiddenValue(expiredHtml, "ChallengeToken");
        var expiredCode = emailSender.Sent[^1].Code;
        clock.Advance(TimeSpan.FromMinutes(11));
        Check((await expiredClient.PostAsync("/Account/VerifyStudentCode", Form(expiredHtml, new() { ["ChallengeToken"] = expiredToken, ["Email"] = expiredStudent.Email!, ["Code"] = expiredCode }))).StatusCode == HttpStatusCode.OK, "Expired Student OTP is rejected");

        var lockedStudent = await CreateStudentAsync(users, "locked-otp", department.Id, password);
        using var lockedClient = Client(factory);
        var lockPage = await lockedClient.GetStringAsync("/Account/StudentSignIn");
        var lockResponse = await lockedClient.PostAsync("/Account/StudentSignIn", Form(lockPage, new() { ["Email"] = lockedStudent.Email! }));
        var lockedHtml = await lockResponse.Content.ReadAsStringAsync();
        var lockedToken = HiddenValue(lockedHtml, "ChallengeToken");
        var lockedCode = emailSender.Sent[^1].Code;
        var wrongCode = (int.Parse(lockedCode) + 1).ToString("D6");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failed = await lockedClient.PostAsync("/Account/VerifyStudentCode", Form(lockedHtml, new() { ["ChallengeToken"] = lockedToken, ["Email"] = lockedStudent.Email!, ["Code"] = wrongCode }));
            Check(failed.StatusCode == HttpStatusCode.OK && (await failed.Content.ReadAsStringAsync()).Contains("Invalid or expired verification code"), "Invalid OTP attempt " + (attempt + 1));
        }
        Check((await lockedClient.PostAsync("/Account/VerifyStudentCode", Form(lockedHtml, new() { ["ChallengeToken"] = lockedToken, ["Email"] = lockedStudent.Email!, ["Code"] = lockedCode }))).StatusCode == HttpStatusCode.OK, "OTP locked after maximum attempts");

        var inactiveStudent = await CreateStudentAsync(users, "inactive-otp", department.Id, password);
        inactiveStudent.IsActive = false;
        Check((await users.UpdateAsync(inactiveStudent)).Succeeded, "Deactivate OTP test user");
        codeCount = await db.AuthenticationCodes.CountAsync(); beforeSend = emailSender.Sent.Length;
        using (var inactiveClient = Client(factory))
        {
            var page = await inactiveClient.GetStringAsync("/Account/StudentSignIn");
            var result = await inactiveClient.PostAsync("/Account/StudentSignIn", Form(page, new() { ["Email"] = inactiveStudent.Email! }));
            Check(result.StatusCode == HttpStatusCode.OK && (await result.Content.ReadAsStringAsync()).Contains("If an eligible active account exists"), "Inactive Student receives generic challenge");
        }
        Check(await db.AuthenticationCodes.CountAsync() == codeCount && emailSender.Sent.Length == beforeSend, "Inactive Student receives no code");

        beforeSend = emailSender.Sent.Length;
        using (var studentRecovery = Client(factory))
        {
            var page = await studentRecovery.GetStringAsync("/Account/ForgotPassword");
            var result = await studentRecovery.PostAsync("/Account/ForgotPassword", Form(page, new() { ["Email"] = otpStudent.Email! }));
            Check(result.StatusCode == HttpStatusCode.OK && (await result.Content.ReadAsStringAsync()).Contains("If an eligible active account exists"), "Student recovery attempt remains generic");
        }
        Check(emailSender.Sent.Length == beforeSend, "Student cannot use Staff password recovery");

        var invalidRecoveryUser = new ApplicationUser { UserName = "invalid-recovery@smiu.edu.pk", Email = "invalid-recovery@smiu.edu.pk", FullName = "Invalid Recovery", DepartmentId = department.Id };
        Check((await users.CreateAsync(invalidRecoveryUser, password)).Succeeded && (await users.AddToRoleAsync(invalidRecoveryUser, "Coordinator")).Succeeded, "Create staff invalid recovery fixture");
        var invalidRecoveryToken = await codeService.RequestAsync(invalidRecoveryUser.Email!, CodePurpose.StaffRecovery);
        var invalidRecoveryCode = emailSender.Sent[^1].Code;
        var wrongRecoveryCode = (int.Parse(invalidRecoveryCode) + 1).ToString("D6");
        Check(await codeService.VerifyAsync(invalidRecoveryToken, wrongRecoveryCode, CodePurpose.StaffRecovery) is null, "Invalid staff recovery code is rejected");

        var expiredRecoveryUser = new ApplicationUser { UserName = "expired-recovery@smiu.edu.pk", Email = "expired-recovery@smiu.edu.pk", FullName = "Expired Recovery", DepartmentId = department.Id };
        Check((await users.CreateAsync(expiredRecoveryUser, password)).Succeeded && (await users.AddToRoleAsync(expiredRecoveryUser, "Coordinator")).Succeeded, "Create staff expired recovery fixture");
        clock.Advance(TimeSpan.FromSeconds(61));
        var expiredRecoveryToken = await codeService.RequestAsync(expiredRecoveryUser.Email!, CodePurpose.StaffRecovery);
        var expiredRecoveryCode = emailSender.Sent[^1].Code;
        clock.Advance(TimeSpan.FromMinutes(11));
        Check(await codeService.VerifyAsync(expiredRecoveryToken, expiredRecoveryCode, CodePurpose.StaffRecovery) is null, "Expired staff recovery code is rejected");

        var replacementPasswords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in ApplicationRoles.Staff)
        {
            var staffEmail = role == "Admin" ? "admin@smiu.edu.pk" : role.ToLowerInvariant() + "@smiu.edu.pk";
            using var recovery = Client(factory);
            var requestPage = await recovery.GetStringAsync("/Account/ForgotPassword");
            var requestResponse = await recovery.PostAsync("/Account/ForgotPassword", Form(requestPage, new() { ["Email"] = staffEmail }));
            var verifyPage = await requestResponse.Content.ReadAsStringAsync();
            var grantCode = emailSender.Sent[^1].Code;
            Check(requestResponse.StatusCode == HttpStatusCode.OK && emailSender.Sent[^1] is { Purpose: CodePurpose.StaffRecovery } sentRecovery
                && string.Equals(sentRecovery.Email, staffEmail, StringComparison.OrdinalIgnoreCase), role + " recovery code sent");
            var challengeToken = HiddenValue(verifyPage, "ChallengeToken");
            var verified = await recovery.PostAsync("/Account/VerifyRecoveryCode", Form(verifyPage, new() { ["ChallengeToken"] = challengeToken, ["Email"] = staffEmail, ["Code"] = grantCode }));
            var resetPage = await verified.Content.ReadAsStringAsync();
            Check(verified.StatusCode == HttpStatusCode.OK && resetPage.Contains("New Password") && resetPage.Contains("Confirm Password"), role + " verifies recovery code");
            var grant = HiddenValue(resetPage, "GrantToken");
            Check(await codeService.VerifyAsync(challengeToken, grantCode, CodePurpose.StaffRecovery) is null, role + " recovery code cannot be reused");
            var replacement = "Staff!9" + Guid.NewGuid().ToString("N");
            replacementPasswords[role] = replacement;
            Check((await recovery.PostAsync("/Account/ResetStaffPassword", Form(resetPage, new() { ["GrantToken"] = grant, ["Password"] = replacement, ["ConfirmPassword"] = replacement }))).StatusCode == HttpStatusCode.Redirect, role + " resets through Identity");
            Check((await recovery.PostAsync("/Account/ResetStaffPassword", Form(resetPage, new() { ["GrantToken"] = grant, ["Password"] = replacement + "X", ["ConfirmPassword"] = replacement + "X" }))).StatusCode == HttpStatusCode.OK, role + " recovery grant cannot be reused");
            using var oldLogin = Client(factory);
            var loginPage = await oldLogin.GetStringAsync("/Account/Login");
            Check((await oldLogin.PostAsync("/Account/Login", Form(loginPage, new() { ["Email"] = staffEmail, ["Password"] = password }))).StatusCode == HttpStatusCode.OK, role + " old staff password rejected");
            using var newLogin = Client(factory);
            loginPage = await newLogin.GetStringAsync("/Account/Login");
            Check((await newLogin.PostAsync("/Account/Login", Form(loginPage, new() { ["Email"] = staffEmail, ["Password"] = replacement }))).StatusCode == HttpStatusCode.Redirect, role + " new staff password works");
        }

        beforeSend = emailSender.Sent.Length;
        using (var unknownStaff = Client(factory))
        {
            var page = await unknownStaff.GetStringAsync("/Account/ForgotPassword");
            var result = await unknownStaff.PostAsync("/Account/ForgotPassword", Form(page, new() { ["Email"] = "missing-staff@smiu.edu.pk" }));
            Check(result.StatusCode == HttpStatusCode.OK && (await result.Content.ReadAsStringAsync()).Contains("If an eligible active account exists"), "Unknown staff recovery response is generic");
        }
        Check(emailSender.Sent.Length == beforeSend, "Unknown staff recovery sends no email");

        // The newly added ORIC/QEC user is institution-wide and may omit a department.
        var oric = await users.FindByEmailAsync("oricqec@smiu.edu.pk");
        Check(oric is not null && oric.DepartmentId is null && await users.IsInRoleAsync(oric, "ORICQEC"), "ORICQEC role/account has institution-wide department-optional profile");
        using var oricClient = Client(factory);
        var oricLoginPage = await oricClient.GetStringAsync("/Account/Login");
        Check((await oricClient.PostAsync("/Account/Login", Form(oricLoginPage, new() { ["Email"] = oric!.Email!, ["Password"] = replacementPasswords["ORICQEC"] }))).StatusCode == HttpStatusCode.Redirect, "ORICQEC uses Staff password login");
        var dashboardResponse = await oricClient.GetAsync("/ORICQEC/Dashboard");
        var dashboardHtml = await dashboardResponse.Content.ReadAsStringAsync();
        Check(dashboardResponse.StatusCode == HttpStatusCode.OK && dashboardHtml.Contains("ORIC / QEC Dashboard") && dashboardHtml.Contains("Institution-wide Academic Repository Access"), "ORICQEC dashboard protected and renders placeholder");
        using var nonOric = Client(factory);
        await LoginAsync(nonOric, "coordinator@smiu.edu.pk", replacementPasswords["Coordinator"]);
        Check((await nonOric.GetAsync("/ORICQEC/Dashboard")).StatusCode == HttpStatusCode.Redirect, "Non-ORICQEC role is denied ORICQEC dashboard");
        using var anonymous = Client(factory);
        Check((await anonymous.GetAsync("/ORICQEC/Dashboard")).StatusCode == HttpStatusCode.Redirect, "Anonymous denied ORICQEC dashboard");

        // Switching a Student profile's department must not permit a browser-supplied DepartmentId.
        var transferStudent = await CreateStudentAsync(users, "transfer-security", department.Id, password);
        using var transferClient = Client(factory);
        await LoginStudentAsync(transferClient, transferStudent.Email!, emailSender);
        var createPage = await transferClient.GetStringAsync("/Student/Submissions/Create");
        Check(createPage.Contains("id=\"submission-department\" class=\"form-select\" disabled") && createPage.Contains("<option selected>Computing</option>")
            && !createPage.Contains("name=\"DepartmentId\""), "Submission form displays assigned department in a locked dropdown");
        var anotherDepartmentId = await db.Departments.Where(d => d.StudentEmailKeyword == "BUS").Select(d => d.Id).SingleAsync();
        var created = await transferClient.PostAsync("/Student/Submissions/Create", Form(createPage,
            new() { ["Title"] = "Department scoped", ["ProjectType"] = "0", ["DepartmentId"] = anotherDepartmentId.ToString(), ["StudentId"] = "forged", ["Status"] = "4", ["intent"] = "SaveDraft" }));
        Check(created.StatusCode == HttpStatusCode.Redirect, "Forged department create safely saves Draft");
        var submissionId = await db.ProjectSubmissions.Where(s => s.Title == "Department scoped").Select(s => s.Id).SingleAsync();
        Check((await UploadTestFileAsync(transferClient, submissionId)).StatusCode == HttpStatusCode.Redirect, "Transfer fixture has required resource");
        transferStudent.DepartmentId = anotherDepartmentId;
        transferStudent.Email = transferStudent.UserName = NextStudentEmail("BUS");
        transferStudent.StudentNumber = StudentDepartmentService.StudentNumberFromEmail(transferStudent.Email);
        Check((await users.UpdateAsync(transferStudent)).Succeeded, "Change Student's current department");
        var editPage = await transferClient.GetStringAsync($"/Student/Submissions/{submissionId}/Edit");
        var editValues = new Dictionary<string,string> { ["EditToken"] = HiddenValue(editPage, "EditToken"), ["Title"] = "Department scoped after transfer", ["ProjectType"] = "0", ["DepartmentId"] = department.Id.ToString(), ["StudentId"] = "forged", ["Status"] = "4", ["intent"] = "Submit", ["Abstract"] = "Done", ["Keywords"] = "department" };
        var editResult = await transferClient.PostAsync($"/Student/Submissions/{submissionId}/Edit", Form(editPage, editValues));
        var finalSubmission = await db.ProjectSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId);
        Check(editResult.StatusCode == HttpStatusCode.Redirect && finalSubmission.Status == SubmissionStatus.Submitted && finalSubmission.StudentId == transferStudent.Id && finalSubmission.DepartmentId == anotherDepartmentId, "Edited Draft always adopts current server-side Student department");
        Check(!await db.ProjectSubmissions.AnyAsync(s => s.StudentId == "forged")
            && await db.ProjectSubmissions.Where(s => s.StudentId == transferStudent.Id).AllAsync(s => s.DepartmentId == anotherDepartmentId),
            "Forged submission ownership did not affect other records");
    }

    private static async Task<ApplicationUser> CreateStudentAsync(UserManager<ApplicationUser> users, string name, int departmentId, string password)
    {
        var email = NextStudentEmail("CSC");
        var student = new ApplicationUser { UserName = email, Email = email, FullName = "Student " + name, DepartmentId = departmentId };
        student.StudentNumber = StudentDepartmentService.StudentNumberFromEmail(email);
        Check((await users.CreateAsync(student)).Succeeded, "Create passwordless SMIU Student " + name);
        Check((await users.AddToRoleAsync(student, "Student")).Succeeded, "Assign Student role " + name);
        return student;
    }
    private static int studentSequence = 100;
    private static string NextStudentEmail(string keyword) => $"{keyword}20F{Interlocked.Increment(ref studentSequence):D3}@smiu.edu.pk";
}

internal sealed class AdjustableTestClock(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset now = initial;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now = now.Add(duration);
}

internal sealed record TestAuthenticationEmail(string Email, string Code, CodePurpose Purpose);

internal sealed class RecordingAuthenticationEmailSender : IAuthenticationEmailSender
{
    public Exception? Failure { get; set; }
    public string? LastAttemptedCode { get; private set; }
    private readonly System.Collections.Concurrent.ConcurrentQueue<TestAuthenticationEmail> sent = new();
    public TestAuthenticationEmail[] Sent => sent.ToArray();
    public Task SendCodeAsync(string email, string code, CodePurpose purpose, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastAttemptedCode = code;
        if (Failure is not null) return Task.FromException(Failure);
        sent.Enqueue(new(email, code, purpose));
        return Task.CompletedTask;
    }
}
