using System.Net;
using AcademicRepository.Data;
using AcademicRepository.Services;
using AcademicRepository.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static partial class IntegrationChecks
{
    static async Task Milestone3Checks(WebApplicationFactory<Program> factory, string password, RecordingAuthenticationEmailSender emailSender)
    {
        const string root = "/Student/Submissions";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var departmentId = await db.Departments.Where(d => d.Code == "CS").Select(d => d.Id).SingleAsync();
        var otherDepartmentId = await db.Departments.Where(d => d.StudentEmailKeyword == "BSE").Select(d => d.Id).SingleAsync();
        async Task<ApplicationUser> StudentAsync(string name, int? department)
        {
            var email = NextStudentEmail(department == otherDepartmentId ? "BSE" : "CSC");
            var user = new ApplicationUser { UserName = email, Email = email, FullName = "Student " + name, DepartmentId = department };
            user.StudentNumber = StudentDepartmentService.StudentNumberFromEmail(email);
            Check((await users.CreateAsync(user)).Succeeded && (await users.AddToRoleAsync(user, "Student")).Succeeded, "Create passwordless submission test student " + name);
            return user;
        }
        var student = await StudentAsync("author", departmentId);
        var otherStudent = await StudentAsync("other-author", otherDepartmentId);
        var noDepartment = await StudentAsync("no-department", null);
        var inactiveStudent = await StudentAsync("inactive-author", departmentId);
        using var client = Client(factory);
        using var other = Client(factory);
        await LoginStudentAsync(client, student.Email!, emailSender);
        await LoginStudentAsync(other, otherStudent.Email!, emailSender);

        using (var noDept = Client(factory))
        {
            var before = emailSender.Sent.Length;
            var requestPage = await noDept.GetStringAsync("/Account/StudentSignIn");
            var noDepartmentResponse = await noDept.PostAsync("/Account/StudentSignIn", Form(requestPage, new() { ["Email"] = noDepartment.Email! }));
            Check(noDepartmentResponse.StatusCode == HttpStatusCode.OK && emailSender.Sent.Length == before, "No-department Student is not eligible for OTP");
            var blocked = await noDept.GetAsync(root + "/Create");
            Check(blocked.StatusCode == HttpStatusCode.Redirect, "No-department Student cannot sign in or open create");
            Check((await noDept.PostAsync(root + "/Create", Form(requestPage, SubmissionFields("Forbidden", "SaveDraft")))).StatusCode == HttpStatusCode.Redirect, "No-department Student cannot POST create");
        }
        using (var inactive = Client(factory))
        {
            await LoginStudentAsync(inactive, inactiveStudent.Email!, emailSender);
            var page = await inactive.GetStringAsync(root + "/Create");
            inactiveStudent.IsActive = false;
            Check((await users.UpdateAsync(inactiveStudent)).Succeeded, "Deactivate submission test student");
            Check((await inactive.PostAsync(root + "/Create", Form(page, SubmissionFields("Inactive", "SaveDraft")))).StatusCode == HttpStatusCode.Redirect, "Inactive session cannot create submission");
            var login = await inactive.GetStringAsync("/Account/Login");
            Check((await inactive.PostAsync("/Account/Login", Form(login, new() { ["Email"] = inactiveStudent.Email!, ["Password"] = password }))).StatusCode == HttpStatusCode.OK, "Inactive student cannot login");
        }

        var createPage = await client.GetStringAsync(root + "/Create");
        Check(createPage.Contains("Semester Project") && createPage.Contains("Final Year Project") && createPage.Contains("Research Project"), "Friendly project type options");
        Check(!createPage.Contains("type=\"file\"") && !createPage.Contains("name=\"StudentId\"") && !createPage.Contains("name=\"DepartmentId\"") && !createPage.Contains("name=\"Status\""), "Form has no upload or ownership/status fields");
        var draftFields = SubmissionFields("Incomplete draft", "SaveDraft");
        draftFields["StudentId"] = otherStudent.Id;
        draftFields["DepartmentId"] = otherDepartmentId.ToString();
        draftFields["Status"] = "Approved";
        draftFields["CreatedAt"] = "2000-01-01";
        draftFields["SubmittedAt"] = "2000-01-01";
        var response = await client.PostAsync(root + "/Create", Form(createPage, draftFields));
        Check(response.StatusCode == HttpStatusCode.Redirect, "Student saves incomplete draft");
        var draft = await db.ProjectSubmissions.AsNoTracking().SingleAsync(s => s.Title == "Incomplete draft");
        Check(draft.StudentId == student.Id && draft.DepartmentId == departmentId && draft.Status == SubmissionStatus.Draft
            && draft.Abstract == "" && draft.Keywords == "" && draft.CreatedAt > DateTime.UtcNow.AddMinutes(-5) && draft.SubmittedAt is null && draft.UpdatedAt is null, "Server derives ownership, department, status and timestamps");
        Check(!await db.ProjectSubmissions.AnyAsync(s => s.Title == "Forbidden" || s.Title == "Inactive"), "Blocked users create no records");
        var detailsPath = root + "/" + draft.Id;
        var editPath = detailsPath + "/Edit";
        var deletePath = detailsPath + "/Delete";
        var draftDetails = await client.GetStringAsync(detailsPath);
        Check(draftDetails.Contains("Student author") && draftDetails.Contains("Computing") && draftDetails.Contains("Not provided") && draftDetails.Contains($"href=\"{editPath}\""), "Own draft details and Edit action");

        foreach (var path in new[] { detailsPath, editPath, deletePath })
            Check((await other.GetAsync(path)).StatusCode == HttpStatusCode.NotFound, "Other student cannot GET " + path);
        var otherPage = await other.GetStringAsync(root + "/Create");
        foreach (var path in new[] { editPath, deletePath })
            Check((await other.PostAsync(path, Form(otherPage, draftFields))).StatusCode == HttpStatusCode.NotFound, "Other student cannot POST " + path);
        Check(!(await other.GetStringAsync(root)).Contains("Incomplete draft"), "Other student's list is private");
        var listing = await client.GetStringAsync(root);
        Check(listing.Contains("Incomplete draft") && listing.Contains("Computing") && listing.Contains(deletePath), "My Submissions lists own draft and actions");
        var dashboard = await client.GetStringAsync("/Student/Dashboard");
        Check(Summary(dashboard, "total", 1) && Summary(dashboard, "drafts", 1) && Summary(dashboard, "submitted", 0) && Summary(dashboard, "approved", 0) && Summary(dashboard, "rejected", 0), "Initial dashboard counts are owner-scoped");
        Check(dashboard.Contains(root + "/Create") && dashboard.Contains("My Recent Submissions"), "Student dashboard navigation and recent submissions");

        await InvalidPost(client, root + "/Create", SubmissionFields("Missing requirements", "Submit"), "Abstract is required", "Submit requires abstract");
        var missingKeywords = SubmissionFields("Missing keywords", "Submit"); missingKeywords["Abstract"] = "Complete abstract";
        await InvalidPost(client, root + "/Create", missingKeywords, "Keywords are required", "Submit requires keywords");
        await InvalidPost(client, root + "/Create", SubmissionFields(" ", "SaveDraft"), "Title is required", "Draft requires title");
        var invalidType = SubmissionFields("Invalid type", "SaveDraft"); invalidType["ProjectType"] = "999";
        await InvalidPost(client, root + "/Create", invalidType, "valid project type", "Draft rejects invalid enum");
        var absentType = SubmissionFields("Absent type", "SaveDraft"); absentType.Remove("ProjectType");
        await InvalidPost(client, root + "/Create", absentType, "valid project type", "Draft requires project type");
        await InvalidPost(client, root + "/Create", SubmissionFields(new string('x', 251), "SaveDraft"), "maximum length", "Draft title length limit");
        var tooLong = SubmissionFields("Long fields", "SaveDraft"); tooLong["Abstract"] = new string('x', 10001); tooLong["Keywords"] = new string('x', 1001); tooLong["CourseCode"] = new string('x', 31);
        await InvalidPost(client, root + "/Create", tooLong, "maximum length", "Draft metadata length limits");
        await InvalidPost(client, root + "/Create", SubmissionFields("Forged intent", "Approved"), "Choose Save Draft or Submit", "Arbitrary status action rejected");
        Check((await client.PostAsync(root + "/Create", new FormUrlEncodedContent(SubmissionFields("No CSRF", "SaveDraft")))).StatusCode == HttpStatusCode.BadRequest, "Create antiforgery");
        Check(await db.ProjectSubmissions.CountAsync(s => s.StudentId == student.Id) == 1, "Invalid creates leave no records");

        var editPage = await client.GetStringAsync(editPath);
        var editToken = HiddenValue(editPage, "EditToken");
        var editFields = SubmissionFields("Updated draft", "SaveDraft"); editFields["EditToken"] = editToken;
        editFields["Status"] = "Submitted"; editFields["StudentId"] = otherStudent.Id; editFields["DepartmentId"] = otherDepartmentId.ToString();
        Check((await client.PostAsync(editPath, Form(editPage, editFields))).StatusCode == HttpStatusCode.Redirect, "Edit own incomplete draft");
        var updated = await db.ProjectSubmissions.AsNoTracking().SingleAsync(s => s.Id == draft.Id);
        Check(updated.Title == "Updated draft" && updated.Status == SubmissionStatus.Draft && updated.StudentId == student.Id && updated.DepartmentId == departmentId
            && updated.UpdatedAt.HasValue && updated.CreatedAt == draft.CreatedAt && updated.SubmittedAt is null, "Edit ignores ownership/status/timestamp manipulation");
        Check((await client.PostAsync(editPath, Form(editPage, editFields))).StatusCode == HttpStatusCode.Conflict, "Stale edit token rejected");
        var latestEdit = await client.GetStringAsync(editPath);
        var invalidEdit = SubmissionFields("Must stay draft", "Submit"); invalidEdit["EditToken"] = HiddenValue(latestEdit, "EditToken");
        var validation = await client.PostAsync(editPath, Form(latestEdit, invalidEdit));
        Check(validation.StatusCode == HttpStatusCode.OK && (await validation.Content.ReadAsStringAsync()).Contains("Abstract is required"), "Edit Submit enforces mandatory fields");
        Check(await db.ProjectSubmissions.Where(s => s.Id == draft.Id).Select(s => s.Status).SingleAsync() == SubmissionStatus.Draft, "Invalid submission remains unchanged draft");
        var forgedToken = SubmissionFields("Forged token", "SaveDraft"); forgedToken["EditToken"] = "not-a-protected-token";
        Check((await client.PostAsync(editPath, Form(latestEdit, forgedToken))).StatusCode == HttpStatusCode.Conflict, "Tampered edit token rejected");
        Check((await client.PostAsync(editPath, new FormUrlEncodedContent(editFields))).StatusCode == HttpStatusCode.BadRequest, "Edit antiforgery");
        var deleteBeforeSubmit = await client.GetStringAsync(deletePath);
        Check((await UploadTestFileAsync(client, draft.Id)).StatusCode == HttpStatusCode.Redirect, "Upload required resource before submitting draft");
        latestEdit = await client.GetStringAsync(editPath);

        var complete = SubmissionFields("Final draft title", "Submit");
        complete["Abstract"] = "An academic abstract."; complete["Keywords"] = "education, database";
        complete["SupervisorName"] = "Dr. Supervisor"; complete["CourseName"] = "Database Systems"; complete["CourseCode"] = "CS301";
        complete["AcademicYear"] = "2026-2027"; complete["Semester"] = "Fall"; complete["ProjectType"] = "2";
        complete["EditToken"] = HiddenValue(latestEdit, "EditToken"); complete["Status"] = "Approved";
        response = await client.PostAsync(editPath, Form(latestEdit, complete));
        Check(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location!.OriginalString == detailsPath, "Valid draft becomes Submitted and redirects to Details");
        var submitted = await db.ProjectSubmissions.AsNoTracking().SingleAsync(s => s.Id == draft.Id);
        Check(submitted.Status == SubmissionStatus.Submitted && submitted.SubmittedAt.HasValue && submitted.SubmittedAt == submitted.UpdatedAt && submitted.CreatedAt == draft.CreatedAt, "Submitted timestamp and status recorded by server");
        var details = await client.GetStringAsync(detailsPath);
        Check(details.Contains("Your submission has been submitted successfully.") && details.Contains("Final Year Project") && details.Contains("Student author") && details.Contains("Computing")
            && details.Contains("An academic abstract.") && details.Contains("education, database") && details.Contains("Dr. Supervisor") && details.Contains("Database Systems") && details.Contains("CS301")
            && details.Contains("2026-2027") && details.Contains("Fall") && !details.Contains($"href=\"{editPath}\""), "Submitted details show metadata, friendly type, success message and no Edit");
        foreach (var path in new[] { editPath, deletePath })
        {
            Check((await client.GetAsync(path)).StatusCode == HttpStatusCode.Conflict, "Submitted blocks GET " + path);
            Check((await client.PostAsync(path, Form(deleteBeforeSubmit, new() { ["EditToken"] = HiddenValue(deleteBeforeSubmit, "EditToken"), ["intent"] = "SaveDraft", ["Title"] = "Attempt overwrite", ["ProjectType"] = "0" }))).StatusCode == HttpStatusCode.Conflict, "Submitted blocks POST " + path);
        }
        Check((await client.PostAsync(editPath, Form(latestEdit, complete))).StatusCode == HttpStatusCode.Conflict, "Repeated submission rejected");
        Check(await db.ProjectSubmissions.AnyAsync(s => s.Id == draft.Id && s.Title == "Final draft title" && s.Status == SubmissionStatus.Submitted), "Submitted record unchanged after edit/delete attacks");
        var deleteDraftFields = SubmissionFields("Delete me", "SaveDraft");
        Check((await PostFormAsync(client, root + "/Create", deleteDraftFields)).StatusCode == HttpStatusCode.Redirect, "Create deletion draft");
        var deleteId = await db.ProjectSubmissions.Where(s => s.Title == "Delete me").Select(s => s.Id).SingleAsync();
        var deletionPath = root + "/" + deleteId + "/Delete";
        var confirmation = await client.GetStringAsync(deletionPath);
        Check(confirmation.Contains("Confirm Delete") && await db.ProjectSubmissions.AnyAsync(s => s.Id == deleteId), "Delete GET is confirmation only");
        Check((await client.PostAsync(deletionPath, new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode == HttpStatusCode.BadRequest, "Delete antiforgery");
        var newerDeleteDraftEdit = await client.GetStringAsync(root + "/" + deleteId + "/Edit");
        var newerDeleteDraftFields = SubmissionFields("Delete me updated", "SaveDraft"); newerDeleteDraftFields["EditToken"] = HiddenValue(newerDeleteDraftEdit, "EditToken");
        Check((await client.PostAsync(root + "/" + deleteId + "/Edit", Form(newerDeleteDraftEdit, newerDeleteDraftFields))).StatusCode == HttpStatusCode.Redirect, "Update draft after opening delete confirmation");
        Check((await client.PostAsync(deletionPath, Form(confirmation, new() { ["EditToken"] = HiddenValue(confirmation, "EditToken") }))).StatusCode == HttpStatusCode.Conflict, "Stale delete confirmation rejected");
        confirmation = await client.GetStringAsync(deletionPath);
        Check((await client.PostAsync(deletionPath, Form(confirmation, new() { ["EditToken"] = HiddenValue(confirmation, "EditToken") }))).StatusCode == HttpStatusCode.Redirect, "Delete own Draft by protected POST");
        Check(!await db.ProjectSubmissions.AnyAsync(s => s.Id == deleteId), "Draft removed");
        Check((await client.GetAsync(root + "/999999")).StatusCode == HttpStatusCode.NotFound, "Missing submission returns 404");

        var directSubmit = SubmissionFields("Direct submitted project", "Submit"); directSubmit["Abstract"] = "Direct abstract"; directSubmit["Keywords"] = "research"; directSubmit["ProjectType"] = "3";
        await InvalidPost(client, root + "/Create", directSubmit, "upload at least one resource", "Create cannot bypass required resource");
        directSubmit["intent"] = "SaveDraft";
        Check((await PostFormAsync(client, root + "/Create", directSubmit)).StatusCode == HttpStatusCode.Redirect, "Create draft before uploading resources");
        var direct = await db.ProjectSubmissions.AsNoTracking().SingleAsync(s => s.Title == "Direct submitted project");
        Check((await UploadTestFileAsync(client, direct.Id)).StatusCode == HttpStatusCode.Redirect, "Add required resource to second draft");
        var directEdit = await client.GetStringAsync(root + "/" + direct.Id + "/Edit");
        directSubmit["intent"] = "Submit"; directSubmit["EditToken"] = HiddenValue(directEdit, "EditToken");
        Check((await client.PostAsync(root + "/" + direct.Id + "/Edit", Form(directEdit, directSubmit))).StatusCode == HttpStatusCode.Redirect, "Second draft submitted with resource");
        direct = await db.ProjectSubmissions.AsNoTracking().SingleAsync(s => s.Id == direct.Id);
        Check(direct.Status == SubmissionStatus.Submitted && direct.CreatedAt <= direct.SubmittedAt && direct.SubmittedAt == direct.UpdatedAt, "Submission timestamps preserved");
        Check((await PostFormAsync(other, root + "/Create", SubmissionFields("Private other draft", "SaveDraft"))).StatusCode == HttpStatusCode.Redirect, "Other student creates own draft");
        dashboard = await client.GetStringAsync("/Student/Dashboard");
        Check(Summary(dashboard, "total", 2) && Summary(dashboard, "drafts", 0) && Summary(dashboard, "submitted", 2) && !dashboard.Contains("Private other draft"), "Dashboard final counts and privacy");
        listing = await client.GetStringAsync(root);
        Check(listing.Contains("Final draft title") && listing.Contains("Direct submitted project") && !listing.Contains("Private other draft") && !listing.Contains(editPath) && !listing.Contains(deletePath), "Submitted list is private and read-only");
        Check((await client.GetAsync("/Student/MySubmissions")).Headers.Location!.OriginalString == root, "Existing My Submissions route preserved");

        foreach (var role in IdentitySeeder.Roles.Where(r => r != "Student"))
        {
            using var nonStudent = Client(factory);
            await LoginAsync(nonStudent, role == "Admin" ? "admin@smiu.edu.pk" : role + "@smiu.edu.pk", password);
            var page = await nonStudent.GetStringAsync("/");
            Check(!page.Contains(root + "/Create"), role + " has no submission navigation");
            foreach (var path in new[] { root, root + "/Create", detailsPath, editPath, deletePath })
            {
                var blocked = await nonStudent.GetAsync(path);
                Check(blocked.StatusCode == HttpStatusCode.Redirect && blocked.Headers.Location!.OriginalString.Contains("AccessDenied"), role + " blocked GET " + path);
            }
            foreach (var path in new[] { root + "/Create", editPath, deletePath })
            {
                var blocked = await nonStudent.PostAsync(path, Form(page, complete));
                Check(blocked.StatusCode == HttpStatusCode.Redirect && blocked.Headers.Location!.OriginalString.Contains("AccessDenied"), role + " blocked POST " + path);
            }
        }
        using var anonymous = Client(factory);
        foreach (var path in new[] { root, root + "/Create", detailsPath, editPath, deletePath })
            Check((await anonymous.GetAsync(path)).StatusCode == HttpStatusCode.Redirect, "Anonymous blocked " + path);
        Check(!await db.ProjectSubmissions.AnyAsync(s => s.Status != SubmissionStatus.Draft && s.Status != SubmissionStatus.Submitted), "Only Draft/Submitted transitions implemented");

        // Simulate two requests that both loaded a Draft before one submitted it.
        var raceDraft = new ProjectSubmission { StudentId = student.Id, DepartmentId = departmentId, Title = "Race protection", Abstract = "Complete", Keywords = "race", CreatedAt = DateTime.UtcNow };
        db.ProjectSubmissions.Add(raceDraft);
        await db.SaveChangesAsync();
        var dbOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<ApplicationDbContext>>();
        await using var staleDb = new ApplicationDbContext(dbOptions);
        await using var submitDb = new ApplicationDbContext(dbOptions);
        var staleDraft = await staleDb.ProjectSubmissions.SingleAsync(s => s.Id == raceDraft.Id);
        var raceSubmission = await submitDb.ProjectSubmissions.SingleAsync(s => s.Id == raceDraft.Id);
        raceSubmission.Status = SubmissionStatus.Submitted;
        raceSubmission.SubmittedAt = raceSubmission.UpdatedAt = DateTime.UtcNow;
        await submitDb.SaveChangesAsync();
        staleDraft.Title = "Stale overwrite";
        var updateBlocked = false;
        try { await staleDb.SaveChangesAsync(); } catch (DbUpdateConcurrencyException) { updateBlocked = true; }
        Check(updateBlocked, "SQL rowversion blocks concurrent edit after submit");
        staleDb.ProjectSubmissions.Remove(staleDraft);
        var deleteBlocked = false;
        try { await staleDb.SaveChangesAsync(); } catch (DbUpdateConcurrencyException) { deleteBlocked = true; }
        Check(deleteBlocked && await db.ProjectSubmissions.AnyAsync(s => s.Id == raceDraft.Id && s.Status == SubmissionStatus.Submitted && s.Title == "Race protection"), "SQL rowversion blocks concurrent delete after submit");
    }

    static Dictionary<string,string> SubmissionFields(string title, string intent) => new() { ["Title"] = title, ["ProjectType"] = "0", ["intent"] = intent };
    static bool Summary(string html, string key, int count) => html.Contains($"data-summary=\"{key}\">{count}</strong>");
}
