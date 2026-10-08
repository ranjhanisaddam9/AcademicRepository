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
    static async Task Milestone7Checks(WebApplicationFactory<Program> factory, RecordingAuthenticationEmailSender sender, AdjustableTestClock clock, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var repository = scope.ServiceProvider.GetRequiredService<IRepositoryService>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        var owner = await users.FindByEmailAsync("CSC20F005@smiu.edu.pk");
        var otherOwner = await users.FindByEmailAsync("CSC20F006@smiu.edu.pk");
        var busOwner = await users.FindByEmailAsync("BUS20F005@smiu.edu.pk");
        var cscReviewer = await users.FindByEmailAsync("review-csc@smiu.edu.pk");
        var busReviewer = await users.FindByEmailAsync("review-bus@smiu.edu.pk");
        clock.Advance(TimeSpan.FromSeconds(61));
        using var student = Client(factory); using var other = Client(factory); using var coordinator = Client(factory); using var bus = Client(factory);
        await LoginStudentAsync(student, owner!.Email!, sender); await LoginStudentAsync(other, otherOwner!.Email!, sender);
        await LoginAsync(coordinator, cscReviewer!.Email!, password); await LoginAsync(bus, busReviewer!.Email!, password);
        var project = await db.ProjectSubmissions.AsNoTracking().SingleAsync(s => s.Title == "AI Based Academic Repository M6 corrected");
        var rejected = await db.SubmissionVersions.AsNoTracking().SingleAsync(v => v.ProjectSubmissionId == project.Id && v.VersionNumber == 1);
        var approvedReview = await db.SubmissionReviews.AsNoTracking().SingleAsync(r => r.ProjectSubmissionId == project.Id && r.Decision == ReviewDecision.Approved);
        var approved = await db.SubmissionVersions.AsNoTracking().SingleAsync(v => v.Id == approvedReview.SubmissionVersionId);
        var oldReport = await db.SubmissionVersionFiles.Where(f => f.SubmissionVersionId == rejected.Id && f.ProjectFile.OriginalFileName == "FinalReport.pdf").Select(f => f.ProjectFile).SingleAsync();
        var approvedFiles = await db.SubmissionVersionFiles.AsNoTracking().Where(f => f.SubmissionVersionId == approved.Id).Select(f => f.ProjectFile).ToListAsync();
        var report = approvedFiles.Single(f => f.OriginalFileName == "FinalReport.pdf");
        string Download(int id, int versionId, int fileId) => $"/Repository/{id}/Versions/{versionId}/Files/{fileId}";
        byte[] approvedBytes;
        await using (var stream = await storage.OpenReadAsync(report.StoredFileName)) { using var output = new MemoryStream(); await stream.CopyToAsync(output); approvedBytes = output.ToArray(); }

        var ownList = await repository.GetApprovedProjectsAsync(owner.Id, new());
        Check(ownList.Items.Any(i => i.SubmissionId == project.Id) && ownList.Scope.Label == "My Approved Projects", "M7 Student list contains own approved repository project");
        var details = await repository.GetApprovedProjectDetailsAsync(owner.Id, project.Id);
        Check(details.VersionId == approved.Id && details.VersionNumber == 2 && details.ReviewRound == 2 && details.Abstract == "Corrected methodology"
            && details.ApprovedAt == approvedReview.CompletedAt && details.ApprovedAt != project.CreatedAt, "M7 resolves approved Version 2 and review completion date");
        Check(details.Files.Count == 4 && details.Files.Select(f => f.Id).ToHashSet().SetEquals(approvedFiles.Select(f => f.Id)) && !details.Files.Any(f => f.Id == oldReport.Id), "M7 resource set comes only from approved Version 2 membership");
        var page = await student.GetStringAsync($"/Repository/Details/{project.Id}");
        Check(page.Contains("Corrected methodology") && page.Contains("Approved Version 2") && page.Contains("Review Round 2") && page.Contains("CSC-20F-005")
            && page.Contains("Required corrections completed.") && !page.Contains("Original methodology") && !page.Contains(oldReport.StoredFileName) && !page.Contains("App_Data"), "Repository Razor details display snapshot/outcome without rejected metadata or storage paths");
        Check((await student.GetByteArrayAsync(Download(project.Id, approved.Id, report.Id))).SequenceEqual(approvedBytes)
            && (await coordinator.GetByteArrayAsync(Download(project.Id, approved.Id, report.Id))).SequenceEqual(approvedBytes), "Owner and department Coordinator download approved resource bytes");
        var response = await student.GetAsync(Download(project.Id, approved.Id, report.Id));
        Check(response.Content.Headers.ContentDisposition?.DispositionType == "attachment" && response.Headers.CacheControl!.NoStore
            && response.Headers.GetValues("X-Content-Type-Options").Single() == "nosniff", "Repository downloads are private attachments with nosniff");
        foreach (var client in new[] { student, coordinator })
        {
            Check((await client.GetAsync(Download(project.Id, rejected.Id, oldReport.Id))).StatusCode == HttpStatusCode.NotFound
                && (await client.GetAsync(Download(project.Id, approved.Id, oldReport.Id))).StatusCode == HttpStatusCode.NotFound, "Repository cannot expose rejected version or rejected-only resource by ID manipulation");
        }
        Check((await student.GetAsync($"/SubmissionVersions/{rejected.Id}")).StatusCode == HttpStatusCode.OK, "Existing authorized rejected-history access remains separate from repository");
        foreach (var client in new[] { other, bus })
        {
            Check((await client.GetAsync($"/Repository/Details/{project.Id}")).StatusCode == HttpStatusCode.NotFound
                && (await client.GetAsync(Download(project.Id, approved.Id, report.Id))).StatusCode == HttpStatusCode.NotFound, "Other Student/BUS Coordinator denied current approved repository IDs");
            Check(!(await client.GetStringAsync("/Repository?Search=M6%20corrected")).Contains("AI Based Academic Repository M6 corrected"), "Repository search never exposes unauthorized project");
        }
        foreach (var id in new[] { otherOwner.Id, busReviewer.Id })
        {
            try { await repository.GetApprovedProjectDetailsAsync(id, project.Id); Check(false, "Direct repository detail authorization must fail"); }
            catch (ReviewOperationException ex) { Check(ex.Status == 404, "Repository service independently protects unauthorized details"); }
            try { var file = await repository.DownloadAsync(id, project.Id, approved.Id, report.Id); await file.Stream.DisposeAsync(); Check(false, "Direct repository download authorization must fail"); }
            catch (ReviewOperationException ex) { Check(ex.Status == 404, "Repository service independently protects unauthorized resources"); }
        }
        Check((await student.GetStringAsync("/")).Contains("My Approved Projects") && (await coordinator.GetStringAsync("/")).Contains("Approved Repository"), "Repository navigation is role appropriate");

        // Authoritative link must win over arbitrary later snapshots and live metadata.
        await db.ProjectSubmissions.Where(s => s.Id == project.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, "UNAPPROVED LIVE TITLE").SetProperty(x => x.Abstract, "UNAPPROVED LIVE ABSTRACT"));
        var extraVersion = SubmissionWorkflowServiceForTestsSnapshot(project, 3, clock.GetUtcNow().UtcDateTime);
        extraVersion.TitleSnapshot = "UNREVIEWED VERSION 3";
        db.SubmissionVersions.Add(extraVersion); await db.SaveChangesAsync();
        var authoritative = await repository.GetApprovedProjectDetailsAsync(owner.Id, project.Id);
        Check(authoritative.VersionId == approved.Id && authoritative.Title == approved.TitleSnapshot && authoritative.Abstract == approved.AbstractSnapshot,
            "Repository ignores mutable current fields and arbitrary latest Version 3");
        Check((await student.GetAsync(Download(project.Id, extraVersion.Id, report.Id))).StatusCode == HttpStatusCode.NotFound, "Unreviewed later version cannot be selected for repository download");
        await db.ProjectSubmissions.Where(s => s.Id == project.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, project.Title).SetProperty(x => x.Abstract, project.Abstract));

        foreach (var status in new[] { SubmissionStatus.Draft, SubmissionStatus.Submitted, SubmissionStatus.UnderReview, SubmissionStatus.Rejected, SubmissionStatus.Revision })
        {
            await db.ProjectSubmissions.Where(s => s.Id == project.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status));
            Check(!(await repository.GetApprovedProjectsAsync(owner.Id, new() { Search = "M6 corrected" })).Items.Any(i => i.SubmissionId == project.Id)
                && (await student.GetAsync($"/Repository/Details/{project.Id}")).StatusCode == HttpStatusCode.NotFound
                && (await student.GetAsync(Download(project.Id, approved.Id, report.Id))).StatusCode == HttpStatusCode.NotFound, "Repository excludes " + status + " even with an earlier approved review");
        }
        await db.ProjectSubmissions.Where(s => s.Id == project.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SubmissionStatus.Approved));
        // Alter isolated fixture data to simulate broken approvals; restore after every case.
        await db.SubmissionReviews.Where(r => r.Id == approvedReview.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.SubmissionVersionId, (int?)null));
        Check((await student.GetAsync($"/Repository/Details/{project.Id}")).StatusCode == HttpStatusCode.NotFound
            && (await repository.GetApprovedProjectsAsync(owner.Id, new())).HasUnavailableRecords, "Approved review without linked version is withheld/reported");
        await db.SubmissionReviews.Where(r => r.Id == approvedReview.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.SubmissionVersionId, approved.Id));
        await db.SubmissionReviews.Where(r => r.Id == approvedReview.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Decision, ReviewDecision.Rejected).SetProperty(r => r.Comments, "fixture mismatch"));
        Check((await student.GetAsync($"/Repository/Details/{project.Id}")).StatusCode == HttpStatusCode.NotFound, "Approved parent without Approved review is denied");
        await db.SubmissionReviews.Where(r => r.Id == approvedReview.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Decision, ReviewDecision.Approved).SetProperty(r => r.Comments, approvedReview.Comments));
        await db.SubmissionVersions.Where(v => v.Id == approved.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.DepartmentIdSnapshot, busOwner!.DepartmentId!.Value));
        Check((await student.GetAsync($"/Repository/Details/{project.Id}")).StatusCode == HttpStatusCode.NotFound, "Snapshot department mismatch cannot create a repository authorization bypass");
        await db.SubmissionVersions.Where(v => v.Id == approved.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.DepartmentIdSnapshot, project.DepartmentId));
        await db.SubmissionVersionFiles.Where(f => f.SubmissionVersionId == approved.Id).ExecuteDeleteAsync();
        Check((await student.GetAsync($"/Repository/Details/{project.Id}")).StatusCode == HttpStatusCode.NotFound, "Approved version without resource snapshot is denied");
        db.SubmissionVersionFiles.AddRange(approvedFiles.Select(f => new SubmissionVersionFile { SubmissionVersionId = approved.Id, ProjectFileId = f.Id })); await db.SaveChangesAsync();
        // Malformed round/date/parent relationships must fail closed even if created outside the workflow.
        await db.SubmissionReviews.Where(r => r.Id == approvedReview.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ReviewRound, 4));
        Check((await student.GetAsync($"/Repository/Details/{project.Id}")).StatusCode == HttpStatusCode.NotFound, "Approved review/version round mismatch is denied");
        await db.SubmissionReviews.Where(r => r.Id == approvedReview.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ReviewRound, 2));
        await db.SubmissionReviews.Where(r => r.Id == approvedReview.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.CompletedAt, r => (DateTime?)r.StartedAt.AddMinutes(-1)));
        Check((await student.GetAsync($"/Repository/Details/{project.Id}")).StatusCode == HttpStatusCode.NotFound, "Invalid approval date is withheld");
        await db.SubmissionReviews.Where(r => r.Id == approvedReview.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.CompletedAt, approvedReview.CompletedAt));
        var forgedReview = new SubmissionReview { ProjectSubmissionId = project.Id, ReviewerId = cscReviewer.Id, ReviewRound = 5,
            Decision = ReviewDecision.Approved, StartedAt = clock.GetUtcNow().UtcDateTime, CompletedAt = clock.GetUtcNow().UtcDateTime };
        db.SubmissionReviews.Add(forgedReview); await db.SaveChangesAsync();
        Check((await student.GetAsync($"/Repository/Details/{project.Id}")).StatusCode == HttpStatusCode.NotFound, "Ambiguous multiple approvals cannot arbitrarily select a version");
        db.SubmissionReviews.Remove(forgedReview); await db.SaveChangesAsync();

        async Task<(ProjectSubmission Project, SubmissionVersion Version, ProjectFile File)> Fixture(ApplicationUser studentUser, ApplicationUser reviewer, string title, int index = 0)
        {
            var now = clock.GetUtcNow().UtcDateTime.AddSeconds(index);
            var submission = new ProjectSubmission { StudentId = studentUser.Id, DepartmentId = studentUser.DepartmentId!.Value,
                Title = title, Abstract = "Repository fixture abstract", Keywords = "quantum-keyword", ProjectType = ProjectType.ResearchProject,
                AcademicYear = "RepositoryYear", Semester = "Spring", SupervisorName = "Repository Supervisor", Status = SubmissionStatus.Approved,
                CreatedAt = now.AddDays(-1), SubmittedAt = now.AddMinutes(-5) };
            db.ProjectSubmissions.Add(submission); await db.SaveChangesAsync();
            var key = await storage.StoreAsync(new MemoryStream(TestPdf), ".pdf");
            var file = new ProjectFile { ProjectSubmissionId = submission.Id, OriginalFileName = "RepositoryReport.pdf", StoredFileName = key,
                ContentType = "application/pdf", FileExtension = ".pdf", FileSize = TestPdf.Length, ResourceType = ProjectResourceType.Report, UploadedAt = now.AddMinutes(-6), UploadedByUserId = studentUser.Id };
            db.ProjectFiles.Add(file); await db.SaveChangesAsync();
            var version = SubmissionWorkflowServiceForTestsSnapshot(submission, 1, submission.SubmittedAt!.Value);
            version.Files.Add(new() { ProjectFileId = file.Id }); db.SubmissionVersions.Add(version); await db.SaveChangesAsync();
            db.SubmissionReviews.Add(new() { ProjectSubmissionId = submission.Id, SubmissionVersionId = version.Id, ReviewRound = 1,
                ReviewerId = reviewer.Id, Decision = ReviewDecision.Approved, StartedAt = now.AddMinutes(-2), CompletedAt = now }); await db.SaveChangesAsync();
            return (submission, version, file);
        }
        var otherApproved = await Fixture(otherOwner, cscReviewer, "M7 Other Student approved");
        var busApproved = await Fixture(busOwner!, busReviewer, "M7 BUS approved");
        Check((await coordinator.GetAsync($"/Repository/Details/{otherApproved.Project.Id}")).StatusCode == HttpStatusCode.OK
            && (await student.GetAsync($"/Repository/Details/{otherApproved.Project.Id}")).StatusCode == HttpStatusCode.NotFound
            && (await student.GetAsync(Download(otherApproved.Project.Id, otherApproved.Version.Id, otherApproved.File.Id))).StatusCode == HttpStatusCode.NotFound,
            "Coordinator can browse same-department other-owner approval while Student remains own-only");
        Check((await bus.GetAsync($"/Repository/Details/{busApproved.Project.Id}")).StatusCode == HttpStatusCode.OK
            && (await coordinator.GetAsync($"/Repository/Details/{busApproved.Project.Id}")).StatusCode == HttpStatusCode.NotFound
            && (await coordinator.GetAsync(Download(busApproved.Project.Id, busApproved.Version.Id, busApproved.File.Id))).StatusCode == HttpStatusCode.NotFound,
            "Coordinator department isolation works in both directions");
        Check((await repository.GetApprovedProjectsAsync(owner.Id, new() { Search = "Other Student approved" })).MatchingRecords == 0, "Unauthorized entries do not leak through result counts");
        foreach (var filter in new RepositoryFilterViewModel[]
        {
            new() { Search = "M7 Other Student" }, new() { Search = "quantum-keyword" }, new() { ProjectType = ProjectType.ResearchProject },
            new() { AcademicYear = "RepositoryYear" }, new() { Semester = "Spring" },
            new() { Search = "quantum-keyword", ProjectType = ProjectType.ResearchProject, AcademicYear = "RepositoryYear", Semester = "Spring" }
        })
            Check((await repository.GetApprovedProjectsAsync(cscReviewer.Id, filter)).Items.Any(i => i.SubmissionId == otherApproved.Project.Id)
                && !(await repository.GetApprovedProjectsAsync(cscReviewer.Id, filter)).Items.Any(i => i.SubmissionId == busApproved.Project.Id), "Search/filter stays in scope: " + (filter.Search ?? filter.AcademicYear ?? filter.Semester ?? "type"));
        Check((await repository.GetApprovedProjectsAsync(cscReviewer.Id, new() { Search = "' OR 1=1 --" })).MatchingRecords == 0, "Search text is parameterized, not executable SQL");
        Check((await coordinator.GetStringAsync("/Repository?Search=no-such-project-m7")).Contains("No approved projects found"), "Empty search has a safe friendly result");
        Check((await coordinator.GetAsync("/Repository?Search=" + new string('a', 201))).StatusCode == HttpStatusCode.BadRequest
            && (await coordinator.GetAsync("/Repository?ProjectType=999")).StatusCode == HttpStatusCode.BadRequest
            && (await coordinator.GetAsync("/Repository?Page=0")).StatusCode == HttpStatusCode.BadRequest, "Invalid repository filter inputs rejected server-side");
        Check((await bus.GetStringAsync("/Repository?Search=M6&DepartmentId=" + owner.DepartmentId)).Contains("0 matching approved records"), "Forged department filter cannot broaden scope");

        for (var i = 0; i < 25; i++) await Fixture(owner, cscReviewer, "M7 PagingCase " + i, i);
        await Fixture(busOwner!, busReviewer, "M7 PagingCase secret", 99);
        var firstPage = await repository.GetApprovedProjectsAsync(cscReviewer.Id, new() { Search = "M7 PagingCase" });
        var secondPage = await repository.GetApprovedProjectsAsync(cscReviewer.Id, new() { Search = "M7 PagingCase", Page = 2 });
        Check(firstPage.MatchingRecords == 25 && firstPage.Items.Count == 20 && secondPage.Items.Count == 5 && firstPage.PageCount == 2
            && !firstPage.Items.Select(i => i.SubmissionId).Intersect(secondPage.Items.Select(i => i.SubmissionId)).Any(), "Repository pagination is bounded, disjoint and scoped at database level");
        Check(firstPage.Items.Select(i => i.ApprovedAt).SequenceEqual(firstPage.Items.Select(i => i.ApprovedAt).OrderDescending()), "Repository sorts newest approval first");
        Check((await repository.GetApprovedProjectsAsync(cscReviewer.Id, new() { Search = "M7 PagingCase", Page = int.MaxValue })).Filter.Page == 2, "Large repository page clamps safely without overflow");
        Check((await coordinator.GetStringAsync("/Repository?Search=M7%20PagingCase&ProjectType=3&AcademicYear=RepositoryYear&Semester=Spring&Page=2")).Contains("25 matching approved records"), "HTTP search/filter/pagination bind together");
        var broken = await Fixture(owner, cscReviewer, "M7 Missing physical report");
        await storage.DeleteAsync(broken.File.StoredFileName);
        var brokenPage = await student.GetAsync($"/Repository/Details/{broken.Project.Id}");
        var error = await brokenPage.Content.ReadAsStringAsync();
        Check(brokenPage.StatusCode == HttpStatusCode.NotFound && !error.Contains(broken.File.StoredFileName) && !error.Contains("App_Data"), "Missing physical repository resource produces safe error without paths");
        Check((await student.GetAsync(Download(broken.Project.Id, broken.Version.Id, broken.File.Id))).StatusCode == HttpStatusCode.NotFound, "Missing physical repository download cannot crash or succeed");
        var withheld = await repository.GetApprovedProjectsAsync(owner.Id, new() { Search = "Missing physical report" });
        Check(withheld.Items.Count == 0 && withheld.HasUnavailableRecords, "Broken physical entry is withheld from results and reported");

        using var anonymous = Client(factory);
        foreach (var path in new[] { "/Repository", $"/Repository/Details/{project.Id}", Download(project.Id, approved.Id, report.Id) })
            Check((await anonymous.GetAsync(path)).StatusCode == HttpStatusCode.Redirect, "Anonymous repository access denied: " + path);
        foreach (var role in new[] { "Admin" })
        {
            using var staff = Client(factory); await LoginAsync(staff, role + "@smiu.edu.pk", password);
            foreach (var path in new[] { "/Repository", $"/Repository/Details/{project.Id}", Download(project.Id, approved.Id, report.Id) })
            {
                var denied = await staff.GetAsync(path);
                Check(denied.StatusCode == HttpStatusCode.Redirect && denied.Headers.Location!.OriginalString.Contains("AccessDenied"), role + " does not inherit Milestone 7 repository access");
            }
            try { await repository.GetApprovedProjectsAsync((await users.FindByEmailAsync(role + "@smiu.edu.pk"))!.Id, new()); Check(false, "Service must reject unrelated role"); }
            catch (ReviewOperationException ex) { Check(ex.Status == 403, "Repository service independently rejects " + role); }
        }
        var noDepartment = await users.FindByEmailAsync("review-no-department@smiu.edu.pk");
        try { await repository.GetApprovedProjectsAsync(noDepartment!.Id, new()); Check(false, "Missing department must fail"); }
        catch (ReviewOperationException ex) { Check(ex.Status == 403, "Missing Coordinator department never becomes global repository scope"); }
        await db.Users.Where(u => u.Id == otherOwner.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        try { await repository.GetApprovedProjectDetailsAsync(otherOwner.Id, otherApproved.Project.Id); Check(false, "Inactive repository user must fail"); }
        catch (ReviewOperationException ex) { Check(ex.Status == 403, "Service rejects inactive repository user"); }
        await db.Users.Where(u => u.Id == otherOwner.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, true));
        Check((await student.GetAsync($"/Student/Submissions/{project.Id}/Edit")).StatusCode == HttpStatusCode.Conflict
            && (await UploadTestFileAsync(student, project.Id)).StatusCode == HttpStatusCode.Conflict
            && (await student.PostAsync($"/Student/Submissions/{project.Id}/StartRevision", Form(await student.GetStringAsync("/"), new()))).StatusCode == HttpStatusCode.Conflict, "Approved repository artifact retains edit/upload/revision freeze");
        Check((await db.SubmissionReviews.AsNoTracking().SingleAsync(r => r.ProjectSubmissionId == project.Id && r.ReviewRound == 1)).Decision == ReviewDecision.Rejected
            && await db.SubmissionVersionFiles.CountAsync(f => f.SubmissionVersionId == rejected.Id) == 3, "Repository browsing never alters rejected review/resource history");
    }

    // Integration fixtures intentionally use existing schema; no production publication entity is introduced.
    static SubmissionVersion SubmissionWorkflowServiceForTestsSnapshot(ProjectSubmission s, int number, DateTime submittedAt) => new()
    {
        ProjectSubmissionId = s.Id, VersionNumber = number, CreatedAt = submittedAt, SubmittedAt = submittedAt,
        CreatedByUserId = s.StudentId, DepartmentIdSnapshot = s.DepartmentId, TitleSnapshot = s.Title, AbstractSnapshot = s.Abstract,
        KeywordsSnapshot = s.Keywords, ProjectTypeSnapshot = s.ProjectType, SupervisorNameSnapshot = s.SupervisorName,
        CourseNameSnapshot = s.CourseName, CourseCodeSnapshot = s.CourseCode, AcademicYearSnapshot = s.AcademicYear, SemesterSnapshot = s.Semester
    };
}
