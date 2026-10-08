using System.Net;
using System.Text;
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
    static async Task Milestone9Checks(WebApplicationFactory<Program> factory, RecordingAuthenticationEmailSender sender, AdjustableTestClock clock, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var repository = scope.ServiceProvider.GetRequiredService<IRepositoryService>();
        var reports = scope.ServiceProvider.GetRequiredService<IRepositoryReportService>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        var oric = (await users.FindByEmailAsync("ORICQEC@smiu.edu.pk"))!;
        var cscStudent = (await users.FindByEmailAsync("CSC20F005@smiu.edu.pk"))!;
        var busStudent = (await users.FindByEmailAsync("BUS20F005@smiu.edu.pk"))!;
        var cscReviewer = (await users.FindByEmailAsync("review-csc@smiu.edu.pk"))!;
        var busReviewer = (await users.FindByEmailAsync("review-bus@smiu.edu.pk"))!;
        var head = (await users.FindByEmailAsync("m8-csc-head@smiu.edu.pk"))!;
        var cscId = cscStudent.DepartmentId!.Value;
        var cscName = await db.Departments.Where(d => d.Id == cscId).Select(d => d.Name).SingleAsync();
        var busId = busStudent.DepartmentId!.Value;
        var aiId = await db.Departments.Where(d => d.StudentEmailKeyword == "BAI").Select(d => d.Id).SingleAsync();
        var aiStudent = new ApplicationUser { UserName = "BAI20F005@smiu.edu.pk", Email = "BAI20F005@smiu.edu.pk", FullName = "M9 AI Student", DepartmentId = aiId, StudentNumber = "BAI-20F-005", EmailConfirmed = true };
        Check((await users.CreateAsync(aiStudent)).Succeeded && (await users.AddToRoleAsync(aiStudent, "Student")).Succeeded, "M9 AI Student fixture uses existing identity and department model");
        Check(oric.DepartmentId is null && await users.IsInRoleAsync(oric, "ORICQEC"), "ORICQEC institutional account does not require DepartmentId");
        using var client = Client(factory); await LoginAsync(client, oric.Email!, password);
        var primary = await db.ProjectSubmissions.AsNoTracking().SingleAsync(s => s.Title == "AI Based Academic Repository M6 corrected");
        var approved = await db.SubmissionVersions.AsNoTracking().SingleAsync(v => v.ProjectSubmissionId == primary.Id && v.VersionNumber == 2);
        var approvedFile = await db.SubmissionVersionFiles.Where(f => f.SubmissionVersionId == approved.Id && f.ProjectFile.OriginalFileName == "FinalReport.pdf").Select(f => f.ProjectFile).SingleAsync();
        var rejected = await db.SubmissionVersions.SingleAsync(v => v.ProjectSubmissionId == primary.Id && v.VersionNumber == 1);
        var rejectedFile = await db.SubmissionVersionFiles.Where(f => f.SubmissionVersionId == rejected.Id && f.ProjectFile.OriginalFileName == "FinalReport.pdf").Select(f => f.ProjectFile).SingleAsync();
        var masked = await db.ProjectSubmissions.AsNoTracking().Where(s => s.Status == SubmissionStatus.Approved && s.Id != primary.Id).Select(s => s.Id).ToListAsync();
        await db.ProjectSubmissions.Where(s => masked.Contains(s.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SubmissionStatus.Submitted));
        var yearStart = clock.GetUtcNow().Month >= 7 ? clock.GetUtcNow().Year : clock.GetUtcNow().Year - 1;
        var currentYear = $"{yearStart}-{yearStart + 1}";
        async Task<(ProjectSubmission Project, SubmissionVersion Version, ProjectFile File)> Fixture(ApplicationUser student, ApplicationUser reviewer, string title, ProjectType type, int order)
        {
            var approvalDate = new DateTime(2026, order % 2 == 0 ? 8 : 9, 10, 12, 0, order, DateTimeKind.Utc);
            var project = new ProjectSubmission { StudentId = student.Id, DepartmentId = student.DepartmentId!.Value, Title = title,
                Abstract = "M9 artificial intelligence methodology uniqueabstract", Keywords = "m9uniquekeyword", ProjectType = type,
                AcademicYear = currentYear, Semester = "Fall", SupervisorName = "Dr. Ahmed", CourseName = "M9 Course", CourseCode = "M9-101",
                Status = SubmissionStatus.Approved, CreatedAt = approvalDate.AddYears(-1), SubmittedAt = approvalDate.AddMinutes(-3) };
            db.ProjectSubmissions.Add(project); await db.SaveChangesAsync();
            var key = await storage.StoreAsync(new MemoryStream(TestPdf), ".pdf");
            var file = new ProjectFile { ProjectSubmissionId = project.Id, OriginalFileName = "CorrectedFinalReport.pdf", StoredFileName = key, ContentType = "application/pdf",
                FileExtension = ".pdf", FileSize = TestPdf.Length, ResourceType = ProjectResourceType.Report, UploadedAt = approvalDate.AddMinutes(-4), UploadedByUserId = student.Id };
            db.ProjectFiles.Add(file); await db.SaveChangesAsync();
            var version = SubmissionWorkflowServiceForTestsSnapshot(project, 1, project.SubmittedAt!.Value);
            version.Files.Add(new() { ProjectFileId = file.Id }); db.SubmissionVersions.Add(version); await db.SaveChangesAsync();
            db.SubmissionReviews.Add(new() { ProjectSubmissionId = project.Id, SubmissionVersionId = version.Id, ReviewRound = 1, ReviewerId = reviewer.Id,
                Decision = ReviewDecision.Approved, StartedAt = approvalDate.AddMinutes(-1), CompletedAt = approvalDate }); await db.SaveChangesAsync();
            return (project, version, file);
        }
        var csc = new List<(ProjectSubmission Project, SubmissionVersion Version, ProjectFile File)>();
        for (var i = 0; i < 4; i++) csc.Add(await Fixture(cscStudent, cscReviewer, "M9 CSC Academic " + i, (ProjectType)i, i));
        var bus = new List<(ProjectSubmission Project, SubmissionVersion Version, ProjectFile File)>();
        for (var i = 0; i < 10; i++) bus.Add(await Fixture(busStudent, busReviewer, "Digital Marketing Analytics M9 " + i, ProjectType.ResearchProject, i));
        var ai = new List<(ProjectSubmission Project, SubmissionVersion Version, ProjectFile File)>();
        for (var i = 0; i < 4; i++) ai.Add(await Fixture(aiStudent, cscReviewer, "AI Medical Diagnosis M9 " + i, ProjectType.FinalYearProject, i));
        var nonapproved = new List<int>();
        foreach (var status in new[] { SubmissionStatus.Rejected, SubmissionStatus.Rejected, SubmissionStatus.Submitted, SubmissionStatus.Submitted,
            SubmissionStatus.Submitted, SubmissionStatus.Draft, SubmissionStatus.UnderReview, SubmissionStatus.Revision })
        {
            var hidden = new ProjectSubmission { StudentId = status == SubmissionStatus.Submitted ? busStudent.Id : cscStudent.Id,
                DepartmentId = status == SubmissionStatus.Submitted ? busId : cscId, Title = "M9 nonapproved " + status + nonapproved.Count, Status = status, CreatedAt = DateTime.UtcNow };
            db.ProjectSubmissions.Add(hidden); await db.SaveChangesAsync(); nonapproved.Add(hidden.Id);
        }
        var list = await repository.GetApprovedProjectsAsync(oric.Id, new(), true);
        Check(list.Scope.InstitutionWide && list.MatchingRecords == 19 && list.Items.Count == 19
            && list.Items.Any(i => i.Department == "Business Administration") && list.Items.Any(i => i.Department == "Artificial Intelligence"), "M9 ORIC sees all nineteen CSC/BUS/AI approved projects");
        Check((await repository.GetApprovedProjectsAsync(oric.Id, new())).MatchingRecords == 19, "Shared repository independently resolves ORIC institution scope with null department");
        var stats = await reports.GetInstitutionSummaryAsync(oric.Id);
        Check(stats.Total == 19 && stats.Departments.Single(d => d.DepartmentId == cscId).Total == 5
            && stats.Departments.Single(d => d.DepartmentId == busId).Total == 10 && stats.Departments.Single(d => d.DepartmentId == aiId).Total == 4,
            "M9 report exact totals CSC=5 BUS=10 AI=4 total=19 exclude rejected/submitted");
        Check(stats.ProjectTypes.Single(t => t.Type == ProjectType.Assignment).Count == 1 && stats.ProjectTypes.Single(t => t.Type == ProjectType.SemesterProject).Count == 1
            && stats.ProjectTypes.Single(t => t.Type == ProjectType.FinalYearProject).Count == 6 && stats.ProjectTypes.Single(t => t.Type == ProjectType.ResearchProject).Count == 11,
            "Institution project-type statistics match approved snapshots");
        Check(stats.AcademicYears.Sum(g => g.Count) == 19 && stats.Semesters.Sum(g => g.Count) == 19 && stats.CurrentAcademicYear == currentYear && stats.CurrentAcademicYearCount == 18,
            "Institution year/semester/current-year counts use version academic metadata");
        Check(stats.ApprovalTrend.Sum(t => t.Count) == 19 && stats.ApprovalTrend.Where(t => t.Year == 2026 && t.Month == 8).Sum(t => t.Count) == 9
            && stats.ApprovalTrend.Where(t => t.Year == 2026 && t.Month == 9).Sum(t => t.Count) == 9,
            "Approval trends use completed review months, never project creation dates");
        Check(stats.Departments.Sum(d => d.Total) == 19 && stats.Departments.Any(d => d.Total == 0) && stats.Recent.Count == 5
            && stats.Recent.Select(r => r.ApprovedAt).SequenceEqual(stats.Recent.Select(r => r.ApprovedAt).OrderDescending()), "Department comparison includes zero-data departments and recent approvals sort by review completion");
        var dashboard = await client.GetStringAsync("/ORICQEC/Dashboard");
        Check(dashboard.Contains("data-insight=\"approved\">19") && dashboard.Contains("Reports") && dashboard.Contains("Business Administration")
            && !dashboard.Contains("Review Queue") && !dashboard.Contains("Admin User Management") && !dashboard.Contains("Start Review"), "ORIC dashboard/navigation renders institution summary without write actions");
        Check((await client.GetStringAsync("/ORICQEC/Reports")).Contains("Department Comparison")
            && (await client.GetStringAsync("/ORICQEC/Reports")).Contains("Approval Trend"), "Reports render dynamic comparisons and trend data through MVC");
        foreach (var id in nonapproved) Check((await client.GetAsync($"/ORICQEC/Details/{id}")).StatusCode == HttpStatusCode.NotFound, "ORIC rejects nonapproved detail " + id);
        foreach (var search in new[] { "M9 CSC Academic", " UNIQUEABSTRACT ", "m9uniquekeyword", cscStudent.FullName, "csc-20f-005", "dr. ahmed" })
            Check((await repository.GetApprovedProjectsAsync(oric.Id, new() { Search = search }, true)).Items.Any(i => i.SubmissionId == csc[0].Project.Id), "Institution search matches field: " + search);
        Check((await repository.GetApprovedProjectsAsync(oric.Id, new() { Search = "AI" }, true)).Items.Any(i => i.SubmissionId == ai[0].Project.Id), "Institution-wide AI search includes AI department");
        var combined = new RepositoryFilterViewModel { Search = "uniqueabstract", DepartmentId = cscId, ProjectType = ProjectType.FinalYearProject, AcademicYear = currentYear, Semester = "Fall", Supervisor = "ahmed" };
        Check((await repository.GetApprovedProjectsAsync(oric.Id, combined, true)).Items.Single().SubmissionId == csc[2].Project.Id, "Institution combined search/department/type/year/semester/supervisor filters intersect");
        foreach (var sort in Enum.GetValues<RepositorySort>())
        {
            var sorted = (await repository.GetApprovedProjectsAsync(oric.Id, new() { Sort = sort }, true)).Items;
            Check(sort switch
            {
                RepositorySort.NewestApproved => sorted.Select(i => i.ApprovedAt).SequenceEqual(sorted.Select(i => i.ApprovedAt).OrderDescending()),
                RepositorySort.OldestApproved => sorted.Select(i => i.ApprovedAt).SequenceEqual(sorted.Select(i => i.ApprovedAt).Order()),
                RepositorySort.TitleAZ => sorted.Select(i => i.Title).SequenceEqual(sorted.Select(i => i.Title).Order(StringComparer.OrdinalIgnoreCase)),
                _ => sorted.Select(i => i.Title).SequenceEqual(sorted.Select(i => i.Title).OrderDescending(StringComparer.OrdinalIgnoreCase))
            }, "ORIC SQL sort " + sort);
        }
        Check((await client.GetAsync("/ORICQEC/Repository?DepartmentId=-1")).StatusCode == HttpStatusCode.BadRequest
            && (await client.GetAsync("/ORICQEC/Export?Sort=999")).StatusCode == HttpStatusCode.BadRequest, "ORIC screen and CSV reject invalid filter models");
        var details = await repository.GetApprovedProjectDetailsAsync(oric.Id, primary.Id, true);
        Check(details.VersionId == approved.Id && details.VersionNumber == 2 && details.Abstract == "Corrected methodology" && details.StudentEmail == cscStudent.Email
            && !details.Files.Any(f => f.Id == rejectedFile.Id), "ORIC resolves review-linked approved Version 2 despite later unreviewed version");
        var detailsHtml = await client.GetStringAsync($"/ORICQEC/Details/{primary.Id}");
        Check(detailsHtml.Contains(cscStudent.Email!) && detailsHtml.Contains("CSC-20F-005") && detailsHtml.Contains("/ORICQEC/Repository/")
            && !detailsHtml.Contains(rejectedFile.StoredFileName) && !detailsHtml.Contains("Start Revision") && !detailsHtml.Contains("Start Review"), "ORIC details show identity and safe approved download links only");
        string Download(int parent, int version, int file) => $"/ORICQEC/Repository/{parent}/Versions/{version}/Files/{file}";
        foreach (var fixture in new[] { bus[0], ai[0] })
        {
            Check((await client.GetAsync($"/ORICQEC/Details/{fixture.Project.Id}")).StatusCode == HttpStatusCode.OK
                && (await client.GetAsync(Download(fixture.Project.Id, fixture.Version.Id, fixture.File.Id))).StatusCode == HttpStatusCode.OK, "ORIC details/download spans department " + fixture.Project.DepartmentId);
        }
        var download = await client.GetAsync(Download(primary.Id, approved.Id, approvedFile.Id));
        Check(download.StatusCode == HttpStatusCode.OK && download.Headers.CacheControl!.NoStore && download.Headers.GetValues("X-Content-Type-Options").Single() == "nosniff", "ORIC approved resources downloaded privately with nosniff");
        Check((await client.GetAsync(Download(primary.Id, rejected.Id, rejectedFile.Id))).StatusCode == HttpStatusCode.NotFound
            && (await client.GetAsync(Download(primary.Id, approved.Id, rejectedFile.Id))).StatusCode == HttpStatusCode.NotFound
            && (await client.GetAsync($"/SubmissionVersions/{rejected.Id}")).StatusCode == HttpStatusCode.Redirect, "ORIC cannot download rejected versions or use historical endpoint");

        async Task<List<string[]>> Export(RepositoryFilterViewModel filter)
        {
            using var stream = new MemoryStream(); await reports.WriteCsvAsync(oric.Id, filter, stream);
            return ParseRepositoryCsv(Encoding.UTF8.GetString(stream.ToArray()));
        }
        var allCsv = await Export(new());
        Check(allCsv.Count == 20 && allCsv[0].Length == 15 && allCsv.Skip(1).All(r => r.Length == 15), "CSV exports all nineteen eligible records with fifteen safe academic columns");
        var primaryRow = allCsv.Single(r => r[0] == primary.Title);
        Check(primaryRow[13] == "2" && primaryRow[14] == "2" && primaryRow[3] == cscStudent.Email, "CSV uses correct approved version, review round and institutional Student email");
        Check((await Export(new() { DepartmentId = busId })).Count == 11 && (await Export(combined)).Count == 2, "CSV uses identical department and combined filters as screen");
        Check((await Export(new() { ProjectType = ProjectType.Assignment })).Count == 2 && (await Export(new() { AcademicYear = currentYear })).Count == 19
            && (await Export(new() { Semester = "Fall", Supervisor = "Ahmed", Search = "m9uniquekeyword" })).Count == 19, "CSV type/year/semester/supervisor/search semantics match repository");
        Check((await Export(new() { Search = "M9 nonapproved" })).Count == 1 && !string.Join("|", allCsv.SelectMany(r => r)).Contains(approvedFile.StoredFileName), "CSV excludes nonapproved records and private storage keys");
        var exportResponse = await client.GetAsync("/ORICQEC/Export?DepartmentId=" + cscId);
        Check(exportResponse.StatusCode == HttpStatusCode.OK && exportResponse.Content.Headers.ContentType!.MediaType == "text/csv"
            && exportResponse.Content.Headers.ContentDisposition!.FileName!.Contains("academic-repository.csv") && exportResponse.Headers.CacheControl!.NoStore
            && ParseRepositoryCsv(await exportResponse.Content.ReadAsStringAsync()).Count == 6, "Authorized HTTP export produces downloadable filtered CSV with private cache headers");
        var dangerous = new[] { "=SUM(1,2)", "+123", "-123", "@SUM(A1)", "  =1+1", "\t@cmd", "\r\n=1", "\0=1", "\uFEFF@cmd", "plain,quoted \"value\"\nsecond line" };
        using var csvText = new StringWriter(); await CsvWriter.WriteRowAsync(csvText, dangerous);
        var escaped = ParseRepositoryCsv(csvText.ToString()).Single();
        Check(escaped.Take(dangerous.Length - 1).Select((s, i) => s == "'" + dangerous[i]).All(v => v) && escaped[^1] == dangerous[^1], "CSV prefixes formulas including leading whitespace/control characters and round-trips comma/quote/newline");
        await db.SubmissionVersions.Where(v => v.Id == csc[0].Version.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.TitleSnapshot, "=SUM(1,2)\n\"quoted\"")
            .SetProperty(v => v.KeywordsSnapshot, "+formula,\"quoted\"\nnewline"));
        var formulaExport = await client.GetStringAsync("/ORICQEC/Export?DepartmentId=" + cscId + "&ProjectType=0");
        var formulaRow = ParseRepositoryCsv(formulaExport)[1];
        Check(formulaRow[0] == "'=SUM(1,2)\n\"quoted\"" && formulaRow[11] == "'+formula,\"quoted\"\nnewline", "Actual SQL-to-HTTP CSV export sanitizes snapshot formulas and escapes multiline fields");
        await db.SubmissionVersions.Where(v => v.Id == csc[0].Version.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.TitleSnapshot, csc[0].Project.Title).SetProperty(v => v.KeywordsSnapshot, csc[0].Project.Keywords));

        using var coordinator = Client(factory); await LoginAsync(coordinator, cscReviewer.Email!, password);
        // The M8 Head fixture already has ORICQEC as an independently assigned additional role.
        using var headOnly = Client(factory);
        var busHead = (await users.FindByEmailAsync("m8-bus-head@smiu.edu.pk"))!; await LoginAsync(headOnly, busHead.Email!, password);
        clock.Advance(TimeSpan.FromSeconds(61));
        using var student = Client(factory); await LoginStudentAsync(student, cscStudent.Email!, sender);
        Check((await repository.GetApprovedProjectsAsync(cscReviewer.Id, new() { DepartmentId = busId })).Items.All(i => i.Department == cscName)
            && (await coordinator.GetAsync($"/Repository/Details/{bus[0].Project.Id}")).StatusCode == HttpStatusCode.NotFound, "Coordinator department parameter cannot broaden scope or expose BUS detail");
        Check((await repository.GetApprovedProjectsAsync(busHead.Id, new() { DepartmentId = cscId })).Items.All(i => i.Department == "Business Administration")
            && (await headOnly.GetAsync($"/Repository/Details/{primary.Id}")).StatusCode == HttpStatusCode.NotFound, "DepartmentHead remains own-department scoped after institution support");
        Check((await repository.GetApprovedProjectsAsync(cscStudent.Id, new() { DepartmentId = busId })).Items.All(i => i.StudentNumber == cscStudent.StudentNumber)
            && (await student.GetAsync($"/Repository/Details/{bus[0].Project.Id}")).StatusCode == HttpStatusCode.NotFound, "Student remains own approved-project scope after institution support");
        var otherStudentProject = await db.ProjectSubmissions.AsNoTracking().SingleAsync(s => s.Title == "M7 Other Student approved");
        await db.ProjectSubmissions.Where(s => s.Id == otherStudentProject.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, SubmissionStatus.Approved));
        Check((await client.GetAsync($"/ORICQEC/Details/{otherStudentProject.Id}")).StatusCode == HttpStatusCode.OK
            && (await student.GetAsync($"/Repository/Details/{otherStudentProject.Id}")).StatusCode == HttpStatusCode.NotFound
            && !(await repository.GetApprovedProjectsAsync(cscStudent.Id, new())).Items.Any(i => i.SubmissionId == otherStudentProject.Id), "Student cannot see another Student's approved project even within the same department");
        await db.ProjectSubmissions.Where(s => s.Id == otherStudentProject.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, SubmissionStatus.Submitted));
        Check(!(await repository.GetApprovedProjectsAsync(head.Id, new())).Scope.InstitutionWide
            && (await repository.GetApprovedProjectsAsync(head.Id, new(), true)).MatchingRecords == 19, "Additional ORIC role grants institution access only through explicit institution scope, preserving Head dashboard scope");
        await db.Users.Where(u => u.Id == head.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DepartmentId, (int?)null));
        try { await repository.GetApprovedProjectsAsync(head.Id, new(), true); Check(false, "ORIC role must not erase another role's required department"); }
        catch (ReviewOperationException ex) { Check(ex.Status == 403, "Head plus ORIC still requires configured Head department before institution access"); }
        await db.Users.Where(u => u.Id == head.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DepartmentId, (int?)cscId));
        using var admin = Client(factory); await LoginAsync(admin, "admin@smiu.edu.pk", password);
        using var anonymous = Client(factory);
        foreach (var deniedClient in new[] { coordinator, headOnly, student, admin, anonymous })
        {
            foreach (var path in new[] { "/ORICQEC/Dashboard", "/ORICQEC/Reports", "/ORICQEC/Repository", "/ORICQEC/Export", $"/ORICQEC/Details/{primary.Id}", Download(primary.Id, approved.Id, approvedFile.Id) })
                Check((await deniedClient.GetAsync(path)).StatusCode == HttpStatusCode.Redirect, "Institution endpoint denies unassigned role: " + path);
        }
        foreach (var id in new[] { cscReviewer.Id, busHead.Id, cscStudent.Id, (await users.FindByEmailAsync("admin@smiu.edu.pk"))!.Id })
        {
            try { await reports.GetInstitutionSummaryAsync(id); Check(false, "Institution reporting requires role at service"); }
            catch (ReviewOperationException ex) { Check(ex.Status == 403, "Report service rejects unassigned ORIC role"); }
            try { using var destination = new MemoryStream(); await reports.WriteCsvAsync(id, new(), destination); Check(false, "Export service requires role"); }
            catch (ReviewOperationException ex) { Check(ex.Status == 403, "CSV service rejects unassigned ORIC role"); }
            try { await repository.GetApprovedProjectsAsync(id, new(), true); Check(false, "Institution query requires role"); }
            catch (ReviewOperationException ex) { Check(ex.Status == 403, "Institution repository flag cannot bypass service authorization"); }
        }
        async Task<string> Fingerprint() => JsonSerializer.Serialize(new
        {
            Users = await db.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync(), Departments = await db.Departments.AsNoTracking().OrderBy(d => d.Id).ToListAsync(),
            Projects = await db.ProjectSubmissions.AsNoTracking().OrderBy(s => s.Id).ToListAsync(), Versions = await db.SubmissionVersions.AsNoTracking().OrderBy(v => v.Id).ToListAsync(),
            Reviews = await db.SubmissionReviews.AsNoTracking().OrderBy(r => r.Id).ToListAsync(), Files = await db.ProjectFiles.AsNoTracking().OrderBy(f => f.Id).ToListAsync(),
            Membership = await db.SubmissionVersionFiles.AsNoTracking().OrderBy(f => f.SubmissionVersionId).ThenBy(f => f.ProjectFileId).ToListAsync()
        });
        var before = await Fingerprint();
        foreach (var path in new[] { $"/Coordinator/Submissions/StartReview/{primary.Id}", $"/Coordinator/Submissions/Approve/{primary.Id}", $"/Coordinator/Submissions/Reject/{primary.Id}",
            $"/Student/Submissions/{primary.Id}/Edit", $"/Student/Submissions/{primary.Id}/Delete", $"/Student/Submissions/{primary.Id}/StartRevision",
            $"/ProjectFiles/Delete/{approvedFile.Id}", $"/ProjectFiles/Upload/{primary.Id}", $"/Users/Edit/{cscStudent.Id}", $"/Departments/Edit/{cscId}" })
        {
            var response = await client.PostAsync(path, Form(dashboard, new()));
            Check(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location!.OriginalString.Contains("AccessDenied"), "ORIC cannot mutate academic or management data: " + path);
        }
        Check((await client.PostAsync($"/Student/Submissions/{primary.Id}/Edit", Form(dashboard, new() { ["intent"] = "Submit" }))).StatusCode == HttpStatusCode.Redirect,
            "ORIC cannot submit or resubmit through the existing Edit intent endpoint");
        foreach (var path in new[] { "/ORICQEC/Dashboard", "/ORICQEC/Reports", "/ORICQEC/Repository", "/ORICQEC/Export", $"/ORICQEC/Details/{primary.Id}" })
            Check((await client.PostAsync(path, Form(dashboard, new()))).StatusCode == HttpStatusCode.MethodNotAllowed, "ORIC routes are GET-only: " + path);
        await client.GetAsync("/ORICQEC/Reports"); await client.GetAsync("/ORICQEC/Export");
        Check(before == await Fingerprint(), "ORIC browsing/reporting/export and denied POSTs leave all data and rowversions unchanged");

        var future = new Department { Name = "Cyber Security", Code = "BCS", StudentEmailKeyword = "BCS" };
        db.Departments.Add(future); await db.SaveChangesAsync();
        var futureStats = await reports.GetInstitutionSummaryAsync(oric.Id);
        Check(futureStats.Departments.Single(d => d.DepartmentId == future.Id).Total == 0
            && (await repository.GetApprovedProjectsAsync(oric.Id, new(), true)).Departments!.Any(d => d.Id == future.Id), "New Cyber Security department appears automatically with zero counts and in filters");
        var futureStudent = new ApplicationUser { UserName = "BCS20F005@smiu.edu.pk", Email = "BCS20F005@smiu.edu.pk", FullName = "Future Student", StudentNumber = "BCS-20F-005", DepartmentId = future.Id, EmailConfirmed = true };
        Check((await users.CreateAsync(futureStudent)).Succeeded && (await users.AddToRoleAsync(futureStudent, "Student")).Succeeded, "Future department Student uses configured mapping");
        var futureProject = await Fixture(futureStudent, cscReviewer, "Cyber Security M9 approved", ProjectType.ResearchProject, 20);
        Check((await reports.GetInstitutionSummaryAsync(oric.Id)).Departments.Single(d => d.DepartmentId == future.Id).Total == 1
            && (await repository.GetApprovedProjectsAsync(oric.Id, new() { DepartmentId = future.Id }, true)).Items.Single().SubmissionId == futureProject.Project.Id,
            "Future department approvals are included dynamically without source changes");
        var secondFuture = await Fixture(futureStudent, cscReviewer, "Cyber Security M9 second", ProjectType.Assignment, 21);
        var page1 = await repository.GetApprovedProjectsAsync(oric.Id, new(), true); var page2 = await repository.GetApprovedProjectsAsync(oric.Id, new() { Page = 2 }, true);
        Check(page1.Items.Count == 20 && page2.Items.Count == 1 && page1.PageCount == 2 && !page1.Items.Select(i => i.SubmissionId).Intersect(page2.Items.Select(i => i.SubmissionId)).Any(), "ORIC database pagination bounds institution results at twenty per page");
        var pageHtml = await client.GetStringAsync("/ORICQEC/Repository?Sort=2");
        Check(pageHtml.Contains("/ORICQEC/Repository?") && pageHtml.Contains("Sort=2") && pageHtml.Contains("Export CSV"), "ORIC paging/export links preserve institution routes and sort");
        await db.Departments.Where(d => d.Id == future.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.IsActive, false));
        Check(!(await repository.GetApprovedProjectsAsync(oric.Id, new(), true)).Departments!.Any(d => d.Id == future.Id)
            && (await reports.GetInstitutionSummaryAsync(oric.Id)).Departments.Single(d => d.DepartmentId == future.Id).Total == 2, "Inactive department omitted from normal choices while historical approved totals are preserved");
        await db.Departments.Where(d => d.Id == future.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.IsActive, true));

        var invalidApproval = new ProjectSubmission { StudentId = cscStudent.Id, DepartmentId = cscId, Title = "M9 missing approval", Status = SubmissionStatus.Approved };
        db.ProjectSubmissions.Add(invalidApproval); await db.SaveChangesAsync();
        var noVersion = await Fixture(cscStudent, cscReviewer, "M9 missing version", ProjectType.Assignment, 22);
        await db.SubmissionReviews.Where(r => r.ProjectSubmissionId == noVersion.Project.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.SubmissionVersionId, (int?)null));
        var noSnapshot = await Fixture(cscStudent, cscReviewer, "M9 missing resource snapshot", ProjectType.Assignment, 23);
        await db.SubmissionVersionFiles.Where(f => f.SubmissionVersionId == noSnapshot.Version.Id).ExecuteDeleteAsync();
        var missingStorage = await Fixture(cscStudent, cscReviewer, "M9 missing storage", ProjectType.Assignment, 24);
        await storage.DeleteAsync(missingStorage.File.StoredFileName);
        var consistent = await reports.GetInstitutionSummaryAsync(oric.Id);
        Check(consistent.Total == 21 && consistent.HasUnavailableRecords, "Invalid approvals/versions/resource snapshots/storage are withheld without crashing institution statistics");
        Check((await Export(new() { Search = "M9 missing" })).Count == 1
            && (await client.GetAsync($"/ORICQEC/Details/{missingStorage.Project.Id}")).StatusCode == HttpStatusCode.NotFound, "Export and details exclude unavailable approved storage and inconsistent records");
        Check((await client.GetStringAsync("/ORICQEC/Reports")).Contains("Incomplete or unavailable"), "Reports visibly flag incomplete or unavailable repository data");
        for (var i = 0; i < 201; i++) await Fixture(cscStudent, cscReviewer, "M9 batched export " + i.ToString("000"), ProjectType.ResearchProject, i % 60);
        var batchCsv = await Export(new() { Search = "M9 batched export", Sort = RepositorySort.TitleAZ, Page = 2 });
        Check(batchCsv.Count == 202 && batchCsv.Skip(1).Select(r => r[0]).Distinct().Count() == 201
            && batchCsv[1][0] == "M9 batched export 000" && batchCsv[^1][0] == "M9 batched export 200", "CSV exports beyond one 200-row batch without truncation, duplication or screen-page restriction");
        Check((await reports.GetInstitutionSummaryAsync(oric.Id)).Total == 222, "Institution reports validate storage across multiple batches and aggregate SQL totals correctly");
        var batchHtml = await client.GetStringAsync("/ORICQEC/Repository?DepartmentId=" + cscId + "&Search=M9%20batched%20export&Supervisor=Ahmed&Sort=2");
        Check(batchHtml.Contains("/ORICQEC/Repository?") && batchHtml.Contains("DepartmentId=" + cscId) && batchHtml.Contains("Supervisor=Ahmed")
            && batchHtml.Contains("Sort=2") && batchHtml.Contains("Page=2"), "Institution paging retains department, search, supervisor and sort across large filtered results");
        await db.Users.Where(u => u.Id == oric.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DepartmentId, (int?)cscId));
        Check((await repository.GetApprovedProjectsAsync(oric.Id, new() { DepartmentId = busId }, true)).MatchingRecords == 10, "ORIC with optional department assignment still has institution scope");
        await db.Users.Where(u => u.Id == oric.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DepartmentId, (int?)null).SetProperty(u => u.IsActive, false));
        try { await reports.GetInstitutionSummaryAsync(oric.Id); Check(false, "Inactive ORIC must fail"); }
        catch (ReviewOperationException ex) { Check(ex.Status == 403, "Inactive ORIC denied by reporting service"); }
        Check((await client.GetAsync("/ORICQEC/Export")).StatusCode == HttpStatusCode.Redirect, "Inactive ORIC session is revoked before export");
        await db.Users.Where(u => u.Id == oric.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, true));
        await LoginAsync(client, oric.Email!, password);
        Check((await users.RemoveFromRoleAsync(oric, "ORICQEC")).Succeeded, "Remove ORIC role fixture to verify current role authorization");
        try { using var target = new MemoryStream(); await reports.WriteCsvAsync(oric.Id, new(), target); Check(false, "Revoked ORIC role must fail export"); }
        catch (ReviewOperationException ex) { Check(ex.Status == 403, "Role revocation is enforced by CSV service independently of cookie claims"); }
        Check((await client.GetAsync("/ORICQEC/Reports")).StatusCode == HttpStatusCode.Redirect, "Revoking the only staff role invalidates the existing ORIC session");
        Check((await users.AddToRoleAsync(oric, "ORICQEC")).Succeeded, "Restore ORIC role after revocation test");
        await db.ProjectSubmissions.Where(s => masked.Contains(s.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SubmissionStatus.Approved));
    }

    // Independent CSV reader verifies quoted cells, embedded delimiters and multiline round-tripping.
    static List<string[]> ParseRepositoryCsv(string csv)
    {
        var rows = new List<string[]>(); var row = new List<string>(); var cell = new StringBuilder(); var quoted = false;
        csv = csv.TrimStart('\uFEFF');
        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (!quoted && ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (!quoted && ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear(); rows.Add(row.ToArray()); row.Clear();
            }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }
}
