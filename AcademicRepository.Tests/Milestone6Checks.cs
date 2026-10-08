using System.Net;
using AcademicRepository.Data;
using AcademicRepository.Models;
using AcademicRepository.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class IntegrationChecks
{
    static async Task Milestone6Checks(WebApplicationFactory<Program> factory, RecordingAuthenticationEmailSender sender, AdjustableTestClock clock, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        // Earlier resource-limit tests use a three-file limit; this acceptance package needs the default ten.
        scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileStorageOptions>>().Value.MaxFilesPerSubmission = 10;
        var owner = await db.Users.AsNoTracking().SingleAsync(u => u.Email == "CSC20F005@smiu.edu.pk");
        var busDepartment = await db.Departments.SingleAsync(d => d.StudentEmailKeyword == "BUS");
        using var student = Client(factory); using var other = Client(factory); using var coordinator = Client(factory); using var bus = Client(factory);
        clock.Advance(TimeSpan.FromSeconds(61));
        await LoginStudentAsync(student, owner.Email!, sender);
        await LoginStudentAsync(other, "CSC20F006@smiu.edu.pk", sender);
        await LoginAsync(coordinator, "review-csc@smiu.edu.pk", password);
        await LoginAsync(bus, "review-bus@smiu.edu.pk", password);
        async Task<HttpResponseMessage> StudentAction(HttpClient client, int id, string action) => await client.PostAsync($"/Student/Submissions/{id}/{action}", Form(await client.GetStringAsync("/"), new()));
        async Task<HttpResponseMessage> Decide(int id, string action, string comments = "")
        {
            var reviewId = await db.SubmissionReviews.Where(r => r.ProjectSubmissionId == id && r.Decision == ReviewDecision.Pending).Select(r => r.Id).SingleAsync();
            return await coordinator.PostAsync($"/Coordinator/Submissions/{action}/{id}", Form(await coordinator.GetStringAsync("/"), new() { ["ReviewId"] = reviewId.ToString(), ["Comments"] = comments }));
        }
        async Task Start(int id)
        {
            Check((await coordinator.PostAsync($"/Coordinator/Submissions/StartReview/{id}", Form(await coordinator.GetStringAsync("/"), new()))).StatusCode == HttpStatusCode.Redirect, "M6 Coordinator starts review");
        }
        async Task<HttpResponseMessage> Edit(int id, string intent, string title, string abstractText, Dictionary<string,string>? extra = null)
        {
            var page = await student.GetStringAsync($"/Student/Submissions/{id}/Edit");
            var fields = SubmissionFields(title, intent);
            fields["Abstract"] = abstractText; fields["Keywords"] = "academic, repository"; fields["ProjectType"] = "2";
            fields["EditToken"] = HiddenValue(page, "EditToken"); fields["AcademicYear"] = "2026-2027"; fields["Semester"] = "Fall";
            if (extra is not null) foreach (var pair in extra) fields[pair.Key] = pair.Value;
            return await student.PostAsync($"/Student/Submissions/{id}/Edit", Form(page, fields));
        }
        async Task<int> Create(string title, bool package = false)
        {
            Check((await PostFormAsync(student, "/Student/Submissions/Create", SubmissionFields(title, "SaveDraft"))).StatusCode == HttpStatusCode.Redirect, "M6 creates initial Draft");
            var id = await db.ProjectSubmissions.Where(s => s.Title == title).Select(s => s.Id).SingleAsync();
            Check((await UploadTestFileAsync(student, id, "FinalReport.pdf")).StatusCode == HttpStatusCode.Redirect, "M6 uploads initial report");
            if (package)
            {
                Check((await UploadTestFileAsync(student, id, "Presentation.pptx", TestArchive("ppt/presentation.xml"), "3")).StatusCode == HttpStatusCode.Redirect, "M6 uploads initial presentation");
                Check((await UploadTestFileAsync(student, id, "SourceCode.zip", TestArchive(), "2")).StatusCode == HttpStatusCode.Redirect, "M6 uploads initial source ZIP");
            }
            Check((await Edit(id, "Submit", title, "Original methodology")).StatusCode == HttpStatusCode.Redirect, "Initial submission creates version");
            return id;
        }
        var id = await Create("AI Based Academic Repository M6", true);
        var first = await db.SubmissionVersions.AsNoTracking().SingleAsync(v => v.ProjectSubmissionId == id);
        var firstFiles = await db.ProjectFiles.AsNoTracking().Where(f => f.ProjectSubmissionId == id).ToListAsync();
        var original = firstFiles.Single(f => f.OriginalFileName == "FinalReport.pdf");
        byte[] originalBytes;
        await using (var bytes = await storage.OpenReadAsync(original.StoredFileName)) { using var output = new MemoryStream(); await bytes.CopyToAsync(output); originalBytes = output.ToArray(); }
        Check(first.VersionNumber == 1 && first.AbstractSnapshot == "Original methodology" && await db.SubmissionVersionFiles.CountAsync(f => f.SubmissionVersionId == first.Id) == 3, "Version 1 captures initial metadata and three resources");
        Check((await StudentAction(student, id, "StartRevision")).StatusCode == HttpStatusCode.Conflict, "Submitted cannot start revision");
        await Start(id);
        Check((await StudentAction(student, id, "StartRevision")).StatusCode == HttpStatusCode.Conflict, "UnderReview cannot start revision");
        const string feedback = "Please revise the methodology and include the project dataset.";
        Check((await Decide(id, "Reject", feedback)).StatusCode == HttpStatusCode.Redirect, "Round 1 rejection succeeds");
        var round1 = await db.SubmissionReviews.AsNoTracking().SingleAsync(r => r.ProjectSubmissionId == id);
        Check(round1.SubmissionVersionId == first.Id && round1.ReviewRound == 1, "Round 1 links to Version 1");
        Check((await student.GetStringAsync($"/Student/Submissions/{id}")).Contains(feedback), "Owner reads rejection feedback before revision");
        Check((await other.GetAsync($"/Student/Submissions/{id}")).StatusCode == HttpStatusCode.NotFound
            && (await StudentAction(other, id, "StartRevision")).StatusCode == HttpStatusCode.NotFound, "Non-owner cannot read or start revision");
        Check((await student.GetAsync($"/Student/Submissions/{id}/StartRevision")).StatusCode == HttpStatusCode.MethodNotAllowed, "Start Revision requires POST");
        Check((await student.PostAsync($"/Student/Submissions/{id}/StartRevision", new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode == HttpStatusCode.BadRequest, "Start Revision requires antiforgery");
        var attempts = await Task.WhenAll(StudentAction(student, id, "StartRevision"), StudentAction(student, id, "StartRevision"));
        Check(attempts.Count(r => r.StatusCode == HttpStatusCode.Redirect) == 1 && attempts.Count(r => r.StatusCode == HttpStatusCode.Conflict) == 1, "Double Start Revision produces one transition");
        Check(await db.ProjectSubmissions.AnyAsync(s => s.Id == id && s.Status == SubmissionStatus.Revision && s.DepartmentId == owner.DepartmentId), "Rejected becomes Revision in original department");
        var revisionPage = await student.GetStringAsync($"/Student/Submissions/{id}/Edit");
        Check(revisionPage.Contains("Edit Revision") && revisionPage.Contains("Resubmit") && revisionPage.Contains("Project type is locked"), "Revision UI exposes allowed editing and locked type");
        Check((await Edit(id, "SaveDraft", "Tampered type", "Changed", new() { ["ProjectType"] = "0" })).StatusCode == HttpStatusCode.OK
            && await db.ProjectSubmissions.AnyAsync(s => s.Id == id && s.ProjectType == ProjectType.FinalYearProject && s.Title != "Tampered type"), "Revision ProjectType tampering is rejected");
        Check((await Edit(id, "SaveDraft", "AI Based Academic Repository M6 corrected", "Corrected methodology", new()
        { ["DepartmentId"] = busDepartment.Id.ToString(), ["StudentId"] = "forged", ["Status"] = "3", ["SubmittedAt"] = "2000-01-01", ["VersionNumber"] = "999" })).StatusCode == HttpStatusCode.Redirect, "Revision allows explicit metadata changes");
        Check(await db.ProjectSubmissions.AnyAsync(s => s.Id == id && s.StudentId == owner.Id && s.DepartmentId == owner.DepartmentId && s.Status == SubmissionStatus.Revision), "Revision ignores identity, department, status and timestamp overposting");
        Check((await student.PostAsync($"/ProjectFiles/Delete/{original.Id}", Form(await student.GetStringAsync("/"), new()))).StatusCode == HttpStatusCode.Redirect, "Revision removes report from active set");
        Check(await db.ProjectFiles.AnyAsync(f => f.Id == original.Id && !f.IsActive && f.DeletedAt != null), "Historical report is soft removed, not deleted");
        Check((await student.GetByteArrayAsync($"/SubmissionVersions/{first.Id}/Files/{original.Id}")).SequenceEqual(originalBytes)
            && (await coordinator.GetByteArrayAsync($"/SubmissionVersions/{first.Id}/Files/{original.Id}")).SequenceEqual(originalBytes), "Original historical bytes remain available to owner and department Coordinator during Revision");
        Check((await coordinator.GetStringAsync($"/SubmissionVersions/{first.Id}")).Contains(feedback), "Historical version shows its review comments during live revision");
        Check((await UploadTestFileAsync(student, id, "FinalReport.pdf", System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\nCorrected report\n"))).StatusCode == HttpStatusCode.Redirect, "Revision uploads corrected report under new storage key");
        Check((await UploadTestFileAsync(student, id, "Dataset.xlsx", TestArchive("xl/workbook.xml"), "4")).StatusCode == HttpStatusCode.Redirect, "Revision uploads additional dataset");
        var corrected = await db.ProjectFiles.AsNoTracking().SingleAsync(f => f.ProjectSubmissionId == id && f.IsActive && f.OriginalFileName == "FinalReport.pdf");
        Check(corrected.StoredFileName != original.StoredFileName, "Replacement never overwrites the original storage object");
        Check((await UploadTestFileAsync(student, id, "Temporary.txt", System.Text.Encoding.UTF8.GetBytes("temporary"))).StatusCode == HttpStatusCode.Redirect, "Revision accepts temporary resource");
        var temporary = await db.ProjectFiles.AsNoTracking().SingleAsync(f => f.ProjectSubmissionId == id && f.OriginalFileName == "Temporary.txt");
        Check((await student.PostAsync($"/ProjectFiles/Delete/{temporary.Id}", Form(await student.GetStringAsync("/"), new()))).StatusCode == HttpStatusCode.Redirect
            && !await db.ProjectFiles.AnyAsync(f => f.Id == temporary.Id), "Never-submitted revision resource is physically removable");
        try { await using var stream = await storage.OpenReadAsync(temporary.StoredFileName); Check(false, "Temporary bytes removed"); }
        catch (IOException) { Check(true, "Temporary resource bytes are deleted"); }
        Check((await coordinator.GetAsync($"/Coordinator/Submissions/Review/{id}")).StatusCode == HttpStatusCode.NotFound, "Revision live metadata remains private until resubmission");
        Check(!(await coordinator.GetStringAsync("/Coordinator/ReviewQueue")).Contains("M6 corrected"), "Revision is absent from Coordinator queue");
        foreach (var versionId in new[] { first.Id })
        {
            Check((await bus.GetAsync($"/SubmissionVersions/{versionId}")).StatusCode == HttpStatusCode.NotFound
                && (await bus.GetAsync($"/SubmissionVersions/{versionId}/Files/{original.Id}")).StatusCode == HttpStatusCode.NotFound, "BUS Coordinator cannot access CSC version/files");
            Check((await other.GetAsync($"/SubmissionVersions/{versionId}")).StatusCode == HttpStatusCode.NotFound
                && (await other.GetAsync($"/SubmissionVersions/{versionId}/Files/{original.Id}")).StatusCode == HttpStatusCode.NotFound, "Other Student cannot access owner version/files");
        }
        Check((await student.GetStringAsync("/Student/Dashboard")).Contains("data-summary=\"revisions\">1"), "Student dashboard counts active revision");
        var oldReview = await db.SubmissionReviews.AsNoTracking().SingleAsync(r => r.Id == round1.Id);
        Check(oldReview.RowVersion.SequenceEqual(round1.RowVersion) && oldReview.Comments == feedback && oldReview.Decision == ReviewDecision.Rejected, "Revision preserves Round 1 unchanged");
        clock.Advance(TimeSpan.FromMinutes(1));
        var resubmitPage = await student.GetStringAsync($"/Student/Submissions/{id}/Edit");
        var resubmitFields = SubmissionFields("AI Based Academic Repository M6 corrected", "Submit");
        resubmitFields["Abstract"] = "Corrected methodology"; resubmitFields["Keywords"] = "academic, repository"; resubmitFields["ProjectType"] = "2";
        resubmitFields["EditToken"] = HiddenValue(resubmitPage, "EditToken");
        var results = await Task.WhenAll(student.PostAsync($"/Student/Submissions/{id}/Edit", Form(resubmitPage, resubmitFields)), student.PostAsync($"/Student/Submissions/{id}/Edit", Form(resubmitPage, resubmitFields)));
        Check(results.Count(r => r.StatusCode == HttpStatusCode.Redirect) == 1 && results.Count(r => r.StatusCode == HttpStatusCode.Conflict) == 1, "Concurrent resubmission creates one new version");
        var second = await db.SubmissionVersions.AsNoTracking().SingleAsync(v => v.ProjectSubmissionId == id && v.VersionNumber == 2);
        var ids = await db.SubmissionVersionFiles.Where(f => f.SubmissionVersionId == second.Id).Select(f => f.ProjectFileId).ToListAsync();
        Check(second.AbstractSnapshot == "Corrected methodology" && second.SubmittedAt > first.SubmittedAt && ids.Count == 4 && ids.Contains(corrected.Id) && !ids.Contains(original.Id), "Version 2 snapshots corrected metadata and active resource set");
        Check(await db.ProjectSubmissions.AnyAsync(s => s.Id == id && s.Status == SubmissionStatus.Submitted && s.SubmittedAt == second.SubmittedAt), "Resubmission updates most recent SubmittedAt");
        Check((await coordinator.GetStringAsync("/Coordinator/ReviewQueue")).Contains("M6 corrected"), "Resubmission returns to correct Coordinator queue");
        Check((await coordinator.GetStringAsync("/Coordinator/ReviewQueue")).Contains("Resubmission")
            && (await scope.ServiceProvider.GetRequiredService<IReviewService>().GetDepartmentSubmissionsAsync((await users.FindByEmailAsync("review-csc@smiu.edu.pk"))!.Id, new() { Search = "M6 corrected" }, true)).Items.Single().VersionNumber == 2,
            "Coordinator queue distinguishes resubmission Version 2 before second review starts");
        Check((await UploadTestFileAsync(student, id)).StatusCode == HttpStatusCode.Conflict
            && (await student.PostAsync($"/ProjectFiles/Delete/{corrected.Id}", Form(await student.GetStringAsync("/"), new()))).StatusCode == HttpStatusCode.Conflict, "Resubmission freezes uploads and deletions");
        Check((await student.GetAsync($"/SubmissionVersions/{first.Id}/Files/{corrected.Id}")).StatusCode == HttpStatusCode.NotFound, "Version/file mismatch cannot expose unrelated resource");
        Check((await bus.GetAsync($"/SubmissionVersions/{second.Id}")).StatusCode == HttpStatusCode.NotFound
            && (await bus.GetAsync($"/SubmissionVersions/{second.Id}/Files/{corrected.Id}")).StatusCode == HttpStatusCode.NotFound
            && (await bus.GetAsync($"/Coordinator/Submissions/Review/{id}")).StatusCode == HttpStatusCode.NotFound, "Cross-department isolation survives Version 2");
        await Start(id);
        var review2 = await db.SubmissionReviews.AsNoTracking().SingleAsync(r => r.ProjectSubmissionId == id && r.ReviewRound == 2);
        Check(review2.SubmissionVersionId == second.Id && review2.Decision == ReviewDecision.Pending, "Second review round points to Version 2");
        var colleague = new ApplicationUser { UserName = "m6-colleague@smiu.edu.pk", Email = "m6-colleague@smiu.edu.pk", FullName = "M6 colleague", DepartmentId = owner.DepartmentId };
        Check((await users.CreateAsync(colleague, password)).Succeeded && (await users.AddToRoleAsync(colleague, "Coordinator")).Succeeded, "Create second-round Coordinator ownership fixture");
        using (var secondCoordinator = Client(factory))
        {
            await LoginAsync(secondCoordinator, colleague.Email!, password);
            Check((await secondCoordinator.GetAsync($"/SubmissionVersions/{second.Id}")).StatusCode == HttpStatusCode.OK
                && (await secondCoordinator.PostAsync($"/Coordinator/Submissions/Approve/{id}", Form(await secondCoordinator.GetStringAsync("/"), new() { ["ReviewId"] = review2.Id.ToString() }))).StatusCode == HttpStatusCode.Forbidden,
                "Same-department colleague can inspect Round 2 but cannot complete its initiator's review");
        }
        Check((await student.PostAsync($"/Student/Submissions/{id}/Edit", Form(resubmitPage, resubmitFields))).StatusCode == HttpStatusCode.Conflict, "Stale revision edit cannot mutate an active Coordinator review");
        Check((await Decide(id, "Approve", "Required corrections completed.")).StatusCode == HttpStatusCode.Redirect, "Round 2 approval succeeds");
        var finalPage = await student.GetStringAsync($"/Student/Submissions/{id}");
        Check(finalPage.Contains(feedback) && finalPage.Contains("Required corrections completed.") && finalPage.Contains("Round 1") && finalPage.Contains("Round 2"), "Student sees both immutable decisions/comments and versions");
        Check(!finalPage.Contains("Start Revision") && (await StudentAction(student, id, "StartRevision")).StatusCode == HttpStatusCode.Conflict, "Approved is final and cannot start revision");
        Check((await student.GetAsync($"/Student/Submissions/{id}/Edit")).StatusCode == HttpStatusCode.Conflict, "Approved cannot edit or resubmit");
        Check((await student.GetByteArrayAsync($"/SubmissionVersions/{first.Id}/Files/{original.Id}")).SequenceEqual(originalBytes), "Original reviewed report remains intact after approval of replacement");
        Check(await db.SubmissionVersions.CountAsync(v => v.ProjectSubmissionId == id) == 2 && await db.SubmissionReviews.CountAsync(r => r.ProjectSubmissionId == id) == 2, "Acceptance flow has exactly two versions and review rounds");
        var preservedFirst = await db.SubmissionVersions.AsNoTracking().SingleAsync(v => v.Id == first.Id);
        Check(preservedFirst.TitleSnapshot == first.TitleSnapshot && preservedFirst.AbstractSnapshot == first.AbstractSnapshot && preservedFirst.SubmittedAt == first.SubmittedAt
            && await db.SubmissionVersionFiles.CountAsync(f => f.SubmissionVersionId == first.Id) == 3, "Version 1 metadata/date/resource membership remains immutable after Version 2 approval");
        var preservedReview = await db.SubmissionReviews.AsNoTracking().SingleAsync(r => r.Id == round1.Id);
        Check(preservedReview.RowVersion.SequenceEqual(round1.RowVersion), "Round 2 never overwrites Round 1 review");

        // A second project verifies unlimited cycles and zero-active-resource validation.
        var multiId = await Create("M6 multiple rejection");
        await Start(multiId); await Decide(multiId, "Reject", "First correction");
        Check((await StudentAction(student, multiId, "StartRevision")).StatusCode == HttpStatusCode.Redirect, "Additional project enters Revision");
        var onlyFile = await db.ProjectFiles.SingleAsync(f => f.ProjectSubmissionId == multiId && f.IsActive);
        await student.PostAsync($"/ProjectFiles/Delete/{onlyFile.Id}", Form(await student.GetStringAsync("/"), new()));
        Check((await Edit(multiId, "Submit", "M6 multiple rejection", "Corrected")).StatusCode == HttpStatusCode.OK
            && await db.SubmissionVersions.CountAsync(v => v.ProjectSubmissionId == multiId) == 1, "No active files prevents resubmission and version creation");
        Check((await UploadTestFileAsync(student, multiId)).StatusCode == HttpStatusCode.Redirect, "Revision restores active resource set");
        var neverSubmitted = await db.ProjectFiles.AsNoTracking().SingleAsync(f => f.ProjectSubmissionId == multiId && f.IsActive);
        await storage.DeleteAsync(neverSubmitted.StoredFileName);
        Check((await Edit(multiId, "Submit", "M6 multiple rejection", "Corrected")).StatusCode == HttpStatusCode.Conflict
            && await db.SubmissionVersions.CountAsync(v => v.ProjectSubmissionId == multiId) == 1, "Missing active physical resource prevents resubmission");
        await student.PostAsync($"/ProjectFiles/Delete/{neverSubmitted.Id}", Form(await student.GetStringAsync("/"), new()));
        await UploadTestFileAsync(student, multiId);
        await using (var failingDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(db.Database.GetConnectionString()).AddInterceptors(new VersionSaveFailure()).Options))
        {
            await using var transaction = await failingDb.Database.BeginTransactionAsync();
            var locked = await new SubmissionLock(failingDb).OwnedAsync(multiId, owner.Id);
            var workflow = new SubmissionWorkflowService(failingDb, users, new StudentDepartmentService(failingDb), new SubmissionLock(failingDb), storage, clock,
                NullLogger<SubmissionWorkflowService>.Instance);
            await workflow.SubmitAsync(owner.Id, locked!);
            try { await failingDb.SaveChangesAsync(); Check(false, "Injected snapshot save must fail"); }
            catch (DbUpdateException) { await transaction.RollbackAsync(); }
        }
        Check(await db.ProjectSubmissions.AnyAsync(s => s.Id == multiId && s.Status == SubmissionStatus.Revision)
            && await db.SubmissionVersions.CountAsync(v => v.ProjectSubmissionId == multiId) == 1, "Snapshot persistence failure rolls back resubmission/version/resource membership together");
        Check((await Edit(multiId, "Submit", "M6 multiple rejection", "Second state")).StatusCode == HttpStatusCode.Redirect, "Second submitted version succeeds");
        await Start(multiId); await Decide(multiId, "Reject", "Second correction");
        Check((await StudentAction(student, multiId, "StartRevision")).StatusCode == HttpStatusCode.Redirect, "Second rejection can start another revision");
        Check((await Edit(multiId, "Submit", "M6 multiple rejection", "Third state")).StatusCode == HttpStatusCode.Redirect, "Third submitted version succeeds");
        await Start(multiId); await Decide(multiId, "Approve");
        Check((await db.SubmissionReviews.AsNoTracking().Where(r => r.ProjectSubmissionId == multiId).OrderBy(r => r.ReviewRound).Select(r => r.Decision).ToListAsync())
            .SequenceEqual(new[] { ReviewDecision.Rejected, ReviewDecision.Rejected, ReviewDecision.Approved }), "Multiple rejections preserve three ordered review rounds");
        Check(await db.SubmissionVersions.CountAsync(v => v.ProjectSubmissionId == multiId) == 3, "Version numbering supports more than one resubmission");
        var legacy = new ProjectSubmission { StudentId = owner.Id, DepartmentId = owner.DepartmentId!.Value, Title = "M6 ambiguous legacy rejection", Status = SubmissionStatus.Rejected, CreatedAt = DateTime.UtcNow };
        db.ProjectSubmissions.Add(legacy); await db.SaveChangesAsync();
        Check((await StudentAction(student, legacy.Id, "StartRevision")).StatusCode == HttpStatusCode.Conflict, "Rejected legacy record without preserved history requires explicit handling");
        using var anonymous = Client(factory);
        Check((await anonymous.GetAsync($"/SubmissionVersions/{first.Id}")).StatusCode == HttpStatusCode.Redirect, "Version history requires authentication");
        foreach (var role in new[] { "Admin", "DepartmentHead", "ORICQEC" })
        {
            using var staff = Client(factory); await LoginAsync(staff, role + "@smiu.edu.pk", password);
            var denied = await staff.GetAsync($"/SubmissionVersions/{first.Id}");
            Check(denied.StatusCode == HttpStatusCode.Redirect && denied.Headers.Location!.OriginalString.Contains("AccessDenied"), role + " has no Student/Coordinator version access");
        }
    }
}

internal sealed class VersionSaveFailure : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<SubmissionVersion>().Any(e => e.State == EntityState.Added)) throw new DbUpdateException("simulated snapshot failure");
        return ValueTask.FromResult(result);
    }
}
