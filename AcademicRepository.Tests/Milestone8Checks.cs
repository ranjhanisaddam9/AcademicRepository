using System.Net;
using System.Text.Json;
using AcademicRepository.Data;
using AcademicRepository.Models;
using AcademicRepository.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static partial class IntegrationChecks
{
    static async Task Milestone8Checks(WebApplicationFactory<Program> factory, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var repository = scope.ServiceProvider.GetRequiredService<IRepositoryService>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var owner = (await users.FindByEmailAsync("CSC20F005@smiu.edu.pk"))!;
        var busOwner = (await users.FindByEmailAsync("BUS20F005@smiu.edu.pk"))!;
        var cscReviewer = (await users.FindByEmailAsync("review-csc@smiu.edu.pk"))!;
        var busReviewer = (await users.FindByEmailAsync("review-bus@smiu.edu.pk"))!;
        var cscDepartment = await db.Departments.SingleAsync(d => d.Id == owner.DepartmentId);
        async Task<ApplicationUser> Head(string prefix, int? department)
        {
            var user = new ApplicationUser { UserName = prefix + "@smiu.edu.pk", Email = prefix + "@smiu.edu.pk", FullName = prefix, DepartmentId = department };
            Check((await users.CreateAsync(user, password)).Succeeded && (await users.AddToRoleAsync(user, "DepartmentHead")).Succeeded, "M8 creates DepartmentHead fixture " + prefix);
            return user;
        }
        var head = await Head("m8-csc-head", owner.DepartmentId);
        var busHead = await Head("m8-bus-head", busOwner.DepartmentId);
        var unconfigured = await Head("m8-no-department", null);
        using var client = Client(factory); using var busClient = Client(factory);
        await LoginAsync(client, head.Email!, password); await LoginAsync(busClient, busHead.Email!, password);
        var primary = await db.ProjectSubmissions.AsNoTracking().SingleAsync(s => s.Title == "AI Based Academic Repository M6 corrected");
        var approval = await db.SubmissionReviews.AsNoTracking().SingleAsync(r => r.ProjectSubmissionId == primary.Id && r.Decision == ReviewDecision.Approved);
        var approvedVersion = await db.SubmissionVersions.AsNoTracking().SingleAsync(v => v.Id == approval.SubmissionVersionId);
        var approvedFile = await db.SubmissionVersionFiles.Where(f => f.SubmissionVersionId == approvedVersion.Id && f.ProjectFile.OriginalFileName == "FinalReport.pdf").Select(f => f.ProjectFile).SingleAsync();
        var rejectedVersion = await db.SubmissionVersions.SingleAsync(v => v.ProjectSubmissionId == primary.Id && v.VersionNumber == 1);
        var rejectedFile = await db.SubmissionVersionFiles.Where(f => f.SubmissionVersionId == rejectedVersion.Id && f.ProjectFile.OriginalFileName == "FinalReport.pdf").Select(f => f.ProjectFile).SingleAsync();
        // Temporarily isolate the requested five-CSC/twenty-BUS statistics scenario inside this disposable test database.
        var masked = await db.ProjectSubmissions.AsNoTracking().Where(s => s.Status == SubmissionStatus.Approved && s.Id != primary.Id
            && (s.DepartmentId == owner.DepartmentId || s.DepartmentId == busOwner.DepartmentId)).Select(s => s.Id).ToListAsync();
        await db.ProjectSubmissions.Where(s => masked.Contains(s.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SubmissionStatus.Submitted));
        var yearStart = clock.GetUtcNow().Month >= 7 ? clock.GetUtcNow().Year : clock.GetUtcNow().Year - 1;
        var currentYear = $"{yearStart}-{yearStart + 1}";
        async Task<(ProjectSubmission Project, SubmissionVersion Version, ProjectFile File)> Fixture(ApplicationUser student, ApplicationUser reviewer, string title, ProjectType type, string year, string semester, int order)
        {
            var now = clock.GetUtcNow().UtcDateTime.AddSeconds(order);
            var project = new ProjectSubmission { StudentId = student.Id, DepartmentId = student.DepartmentId!.Value, Title = title,
                Abstract = "Artificial intelligence methodology uniqueabstract", Keywords = "m8uniquekeyword", ProjectType = type,
                AcademicYear = year, Semester = semester, SupervisorName = "Dr. Ahmed", Status = SubmissionStatus.Approved, CreatedAt = now.AddDays(-1), SubmittedAt = now.AddMinutes(-3) };
            db.ProjectSubmissions.Add(project); await db.SaveChangesAsync();
            var key = await storage.StoreAsync(new MemoryStream(TestPdf), ".pdf");
            var file = new ProjectFile { ProjectSubmissionId = project.Id, OriginalFileName = "M8Report.pdf", StoredFileName = key, ContentType = "application/pdf",
                FileExtension = ".pdf", FileSize = TestPdf.Length, ResourceType = ProjectResourceType.Report, UploadedAt = now.AddMinutes(-4), UploadedByUserId = student.Id };
            db.ProjectFiles.Add(file); await db.SaveChangesAsync();
            var version = SubmissionWorkflowServiceForTestsSnapshot(project, 1, project.SubmittedAt!.Value);
            version.Files.Add(new() { ProjectFileId = file.Id }); db.SubmissionVersions.Add(version); await db.SaveChangesAsync();
            db.SubmissionReviews.Add(new() { ProjectSubmissionId = project.Id, SubmissionVersionId = version.Id, ReviewRound = 1, ReviewerId = reviewer.Id,
                Decision = ReviewDecision.Approved, StartedAt = now.AddMinutes(-1), CompletedAt = now }); await db.SaveChangesAsync();
            return (project, version, file);
        }
        var csc = new List<(ProjectSubmission Project, SubmissionVersion Version, ProjectFile File)>();
        csc.Add(await Fixture(owner, cscReviewer, "M8 Alpha Academic", ProjectType.Assignment, "2024-2025", "Spring", 1));
        csc.Add(await Fixture(owner, cscReviewer, "M8 Bravo Academic", ProjectType.SemesterProject, currentYear, "Fall", 2));
        csc.Add(await Fixture(owner, cscReviewer, "M8 Charlie Academic", ProjectType.FinalYearProject, currentYear, "Fall", 3));
        csc.Add(await Fixture(owner, cscReviewer, "M8 Delta Academic", ProjectType.ResearchProject, "2025-2026", "Spring", 4));
        var bus = new List<(ProjectSubmission Project, SubmissionVersion Version, ProjectFile File)>();
        for (var i = 0; i < 20; i++) bus.Add(await Fixture(busOwner, busReviewer, "M8 BUS secret Academic " + i, ProjectType.ResearchProject, "BUS-Exclusive-Year", "BUS-Exclusive-Semester", i));
        var nonapproved = new List<int>();
        foreach (var status in new[] { SubmissionStatus.Draft, SubmissionStatus.Submitted, SubmissionStatus.Submitted, SubmissionStatus.Submitted,
            SubmissionStatus.UnderReview, SubmissionStatus.Rejected, SubmissionStatus.Rejected, SubmissionStatus.Revision })
        {
            var hidden = new ProjectSubmission { StudentId = owner.Id, DepartmentId = owner.DepartmentId!.Value, Title = "M8 private " + status + " " + nonapproved.Count,
                Status = status, CreatedAt = DateTime.UtcNow, ProjectType = ProjectType.FinalYearProject };
            db.ProjectSubmissions.Add(hidden); await db.SaveChangesAsync(); nonapproved.Add(hidden.Id);
        }
        var stats = await repository.GetDepartmentRepositoryStatsAsync(head.Id);
        Check(stats.Total == 5 && stats.Department == cscDepartment.Name && stats.ProjectTypes.Sum(t => t.Count) == 5, "M8 CSC dashboard counts exactly five approved repository projects, never twenty-five or all statuses");
        Check(stats.ProjectTypes.Single(t => t.Type == ProjectType.FinalYearProject).Count == 2 && stats.ProjectTypes.Single(t => t.Type == ProjectType.Assignment).Count == 1
            && stats.ProjectTypes.Single(t => t.Type == ProjectType.SemesterProject).Count == 1 && stats.ProjectTypes.Single(t => t.Type == ProjectType.ResearchProject).Count == 1,
            "M8 project-type statistics count only the five CSC approvals");
        Check(stats.AcademicYears.Sum(g => g.Count) == 5 && !stats.AcademicYears.Any(g => g.Label.Contains("BUS"))
            && stats.Semesters.Sum(g => g.Count) == 5 && !stats.Semesters.Any(g => g.Label.Contains("BUS")) && stats.CurrentAcademicYear == currentYear && stats.CurrentAcademicYearCount == 2,
            "Academic year, semester and current-year insights cannot leak BUS totals or labels");
        Check(stats.Recent.Count == 5 && stats.Recent.All(i => i.Department == cscDepartment.Name)
            && stats.Recent.Select(i => i.ApprovedAt).SequenceEqual(stats.Recent.Select(i => i.ApprovedAt).OrderDescending()), "Recent approvals use completed review dates in authorized department");
        var dashboard = await client.GetStringAsync("/DepartmentHead/Dashboard");
        Check(dashboard.Contains("data-insight=\"approved\">5") && dashboard.Contains(cscDepartment.Name) && dashboard.Contains("Academic Repository")
            && !dashboard.Contains("Review Queue") && !dashboard.Contains("Start Review") && !dashboard.Contains("BUS secret"), "DepartmentHead dashboard/navigation is read-only and scoped");
        var redirect = await client.GetAsync("/DepartmentHead/Repository");
        Check(redirect.StatusCode == HttpStatusCode.Redirect && redirect.Headers.Location!.OriginalString.Contains("Repository"), "DepartmentHead repository navigation reaches shared repository");
        var list = await repository.GetApprovedProjectsAsync(head.Id, new());
        Check(list.MatchingRecords == 5 && list.Items.Count == 5 && list.Scope.DepartmentHead && !list.AcademicYears!.Contains("BUS-Exclusive-Year"), "Head list and academic-year choices come only from approved department snapshots");
        foreach (var id in nonapproved)
            Check((await client.GetAsync($"/Repository/Details/{id}")).StatusCode == HttpStatusCode.NotFound, "Head cannot discover nonapproved project " + id);
        foreach (var search in new[] { "Alpha Academic", "  UNIQUEABSTRACT  ", "m8uniquekeyword", owner.FullName, "csc-20f-005", "dr. ahmed" })
        {
            var result = await repository.GetApprovedProjectsAsync(head.Id, new() { Search = search });
            Check(result.Items.Any(i => i.SubmissionId == csc[0].Project.Id) && result.Items.All(i => i.Department == cscDepartment.Name), "Head search trims/case-normalizes and scopes: " + search);
        }
        var combined = await repository.GetApprovedProjectsAsync(head.Id, new() { Search = "Artificial Intelligence", ProjectType = ProjectType.FinalYearProject,
            AcademicYear = currentYear, Semester = "Fall", Supervisor = " ahmed " });
        Check(combined.MatchingRecords == 1 && combined.Items.Single().SubmissionId == csc[2].Project.Id, "Combined title/abstract/type/year/semester/supervisor filters intersect within department");
        Check((await repository.GetApprovedProjectsAsync(head.Id, new() { Search = "M8 BUS secret Academic" })).MatchingRecords == 0
            && (await repository.GetApprovedProjectsAsync(head.Id, new() { Search = "' OR 1=1 --" })).MatchingRecords == 0, "DepartmentHead search never broadens scope or executes SQL text");
        var oldest = await repository.GetApprovedProjectsAsync(head.Id, new() { Search = "M8", Sort = RepositorySort.OldestApproved });
        var az = await repository.GetApprovedProjectsAsync(head.Id, new() { Search = "M8", Sort = RepositorySort.TitleAZ });
        var za = await repository.GetApprovedProjectsAsync(head.Id, new() { Search = "M8", Sort = RepositorySort.TitleZA });
        Check(oldest.Items.Select(i => i.ApprovedAt).SequenceEqual(oldest.Items.Select(i => i.ApprovedAt).Order())
            && az.Items.Select(i => i.Title).SequenceEqual(az.Items.Select(i => i.Title).Order())
            && za.Items.Select(i => i.Title).SequenceEqual(za.Items.Select(i => i.Title).OrderDescending()), "Head sorting executes newest/oldest/title order without changing scope");
        Check((await client.GetStringAsync("/Repository?Search=Artificial%20Intelligence&ProjectType=2&AcademicYear=" + currentYear + "&Semester=Fall&Supervisor=Ahmed&Sort=2")).Contains("1 matching approved records"), "Extended DepartmentHead HTTP filters bind together");
        Check((await client.GetAsync("/Repository?Supervisor=" + new string('a', 151))).StatusCode == HttpStatusCode.BadRequest
            && (await client.GetAsync("/Repository?Sort=999")).StatusCode == HttpStatusCode.BadRequest, "Supervisor and sort validation rejects malformed requests");
        var details = await repository.GetApprovedProjectDetailsAsync(head.Id, primary.Id);
        Check(details.VersionNumber == 2 && details.VersionId == approvedVersion.Id && details.Abstract == "Corrected methodology" && details.StudentEmail == owner.Email
            && !details.Files.Any(f => f.Id == rejectedFile.Id), "Head uses approved Version 2, identity email and approved-only resources");
        var detailsPage = await client.GetStringAsync($"/Repository/Details/{primary.Id}");
        Check(detailsPage.Contains(owner.Email!) && detailsPage.Contains("CSC-20F-005") && !detailsPage.Contains("Start Revision") && !detailsPage.Contains("Start Review")
            && !detailsPage.Contains("Edit / Resubmit") && !detailsPage.Contains(rejectedFile.StoredFileName), "Head details show academic identity without write controls or storage paths");
        string Download(int parent, int version, int file) => $"/Repository/{parent}/Versions/{version}/Files/{file}";
        Check((await client.GetAsync(Download(primary.Id, approvedVersion.Id, approvedFile.Id))).StatusCode == HttpStatusCode.OK, "Head downloads own-department approved-version file");
        Check((await client.GetAsync(Download(primary.Id, rejectedVersion.Id, rejectedFile.Id))).StatusCode == HttpStatusCode.NotFound
            && (await client.GetAsync(Download(primary.Id, approvedVersion.Id, rejectedFile.Id))).StatusCode == HttpStatusCode.NotFound
            && (await client.GetAsync($"/SubmissionVersions/{rejectedVersion.Id}")).StatusCode == HttpStatusCode.Redirect, "Head cannot obtain rejected history through repository or unauthorized historical endpoint");
        Check((await client.GetAsync($"/Repository/Details/{bus[0].Project.Id}")).StatusCode == HttpStatusCode.NotFound
            && (await client.GetAsync(Download(bus[0].Project.Id, bus[0].Version.Id, bus[0].File.Id))).StatusCode == HttpStatusCode.NotFound
            && (await busClient.GetAsync($"/Repository/Details/{primary.Id}")).StatusCode == HttpStatusCode.NotFound, "Head direct project/version/file attacks fail in both department directions");
        Check((await client.GetStringAsync("/Repository?Search=M8%20BUS&DepartmentId=" + busOwner.DepartmentId)).Contains("0 matching approved records"), "Forged department query cannot broaden Head scope");

        async Task<string> AcademicFingerprint() => JsonSerializer.Serialize(new
        {
            Projects = await db.ProjectSubmissions.AsNoTracking().OrderBy(s => s.Id).ToListAsync(),
            Versions = await db.SubmissionVersions.AsNoTracking().OrderBy(v => v.Id).ToListAsync(),
            Reviews = await db.SubmissionReviews.AsNoTracking().OrderBy(r => r.Id).ToListAsync(),
            Files = await db.ProjectFiles.AsNoTracking().OrderBy(f => f.Id).ToListAsync(),
            Membership = await db.SubmissionVersionFiles.AsNoTracking().OrderBy(f => f.SubmissionVersionId).ThenBy(f => f.ProjectFileId).ToListAsync()
        });
        var before = await AcademicFingerprint();
        foreach (var path in new[] { $"/Coordinator/Submissions/StartReview/{primary.Id}", $"/Coordinator/Submissions/Approve/{primary.Id}",
            $"/Coordinator/Submissions/Reject/{primary.Id}", $"/Student/Submissions/{primary.Id}/Edit", $"/Student/Submissions/{primary.Id}/Delete",
            $"/Student/Submissions/{primary.Id}/StartRevision", $"/ProjectFiles/Delete/{approvedFile.Id}", $"/ProjectFiles/Upload/{primary.Id}" })
        {
            var result = await client.PostAsync(path, Form(await client.GetStringAsync("/"), new() { ["ReviewId"] = approval.Id.ToString(), ["intent"] = "Submit" }));
            Check(result.StatusCode == HttpStatusCode.Redirect && result.Headers.Location!.OriginalString.Contains("AccessDenied"), "Read-only Head cannot POST academic mutation: " + path);
        }
        Check((await client.PostAsync("/DepartmentHead/Dashboard", Form(dashboard, new()))).StatusCode == HttpStatusCode.MethodNotAllowed
            && (await client.PostAsync($"/Repository/Details/{primary.Id}", Form(dashboard, new()))).StatusCode == HttpStatusCode.MethodNotAllowed, "Head read pages expose GET only");
        await repository.GetApprovedProjectDetailsAsync(head.Id, primary.Id);
        await repository.GetDepartmentRepositoryStatsAsync(head.Id);
        Check(before == await AcademicFingerprint(), "Head browsing/statistics and denied POSTs modify no submissions, versions, reviews or files");

        try { await repository.GetApprovedProjectsAsync(unconfigured.Id, new()); Check(false, "Unconfigured Head must fail"); }
        catch (ReviewOperationException ex) { Check(ex.Status == 403 && ex.Message.Contains("assigned department"), "Missing Head department denies service access with configuration guidance"); }
        using (var invalid = Client(factory))
        {
            var loginPage = await invalid.GetStringAsync("/Account/Login");
            var denied = await invalid.PostAsync("/Account/Login", Form(loginPage, new() { ["Email"] = unconfigured.Email!, ["Password"] = password }));
            Check(denied.StatusCode == HttpStatusCode.OK && (await denied.Content.ReadAsStringAsync()).Contains("Department Head accounts also require an assigned department"), "Unconfigured Head cannot log in and sees guidance");
        }
        // Unrelated ORICQEC role never broadens a Head's department or grants Coordinator write powers.
        Check((await users.AddToRoleAsync(head, "ORICQEC")).Succeeded, "Add unrelated role to Head fixture");
        Check((await repository.GetDepartmentRepositoryStatsAsync(head.Id)).Total == 5
            && (await repository.GetApprovedProjectsAsync(head.Id, new() { Search = "BUS secret" })).MatchingRecords == 0, "Head plus ORICQEC remains department-scoped");
        try { await scope.ServiceProvider.GetRequiredService<IReviewService>().StartReviewAsync(head.Id, nonapproved[1]); Check(false, "Head must not acquire review powers"); }
        catch (ReviewOperationException ex) { Check(ex.Status == 403, "Service refuses Head review mutation despite unrelated role"); }
        foreach (var role in new[] { "Admin", "Student", "Coordinator", "ORICQEC" })
        {
            var userId = role == "Student" ? owner.Id : role == "Coordinator" ? cscReviewer.Id : (await users.FindByEmailAsync(role + "@smiu.edu.pk"))!.Id;
            try { await repository.GetDepartmentRepositoryStatsAsync(userId); Check(false, "Non-Head insights must fail"); }
            catch (ReviewOperationException ex) { Check(ex.Status == 403, role + " cannot call Head statistics service"); }
        }
        using var anonymous = Client(factory);
        Check((await anonymous.GetAsync("/DepartmentHead/Dashboard")).StatusCode == HttpStatusCode.Redirect, "Anonymous Head dashboard requires authentication");
        await db.Users.Where(u => u.Id == busHead.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        try { await repository.GetApprovedProjectsAsync(busHead.Id, new()); Check(false, "Inactive Head must fail"); }
        catch (ReviewOperationException ex) { Check(ex.Status == 403, "Repository service denies inactive Head"); }
        await db.Users.Where(u => u.Id == busHead.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, true));
        await db.Users.Where(u => u.Id == busHead.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DepartmentId, (int?)null));
        Check((await busClient.GetAsync("/DepartmentHead/Dashboard")).StatusCode == HttpStatusCode.Redirect
            && (await busClient.GetStringAsync("/Account/Login")).Contains("DepartmentHead account has no department assigned"), "Removing department revokes Head session with configuration message");
        await db.Users.Where(u => u.Id == busHead.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DepartmentId, busOwner.DepartmentId));

        // Restore earlier approvals, then exercise multiple pages with the existing valid CSC fixtures.
        await db.ProjectSubmissions.Where(s => masked.Contains(s.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SubmissionStatus.Approved));
        var page1 = await repository.GetApprovedProjectsAsync(head.Id, new() { Search = "M7 PagingCase" });
        var page2 = await repository.GetApprovedProjectsAsync(head.Id, new() { Search = "M7 PagingCase", Page = 2 });
        Check(page1.MatchingRecords == 25 && page1.Items.Count == 20 && page2.Items.Count == 5 && !page1.Items.Select(i => i.SubmissionId).Intersect(page2.Items.Select(i => i.SubmissionId)).Any(), "Head pagination uses bounded disjoint department-scoped pages");
        var paged = await client.GetStringAsync("/Repository?Search=M7%20PagingCase&Supervisor=Repository&Sort=2&Page=2");
        Check(paged.Contains("25 matching approved records") && paged.Contains("Page 2 of 2") && paged.Contains("Supervisor=Repository") && paged.Contains("Sort=2"), "Head paging links preserve supervisor/search/sort filters");
        Check((await repository.GetApprovedProjectsAsync(head.Id, new() { Search = "M7 PagingCase", Page = int.MaxValue })).Filter.Page == 2, "Head extreme page clamps safely");
    }
}
