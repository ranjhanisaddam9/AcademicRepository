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
    static async Task Milestone5Checks(WebApplicationFactory<Program> factory, RecordingAuthenticationEmailSender sender, AdjustableTestClock clock, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var service = scope.ServiceProvider.GetRequiredService<IReviewService>();
        var cscDepartment = await db.Departments.AsNoTracking().SingleAsync(d => d.StudentEmailKeyword == "CSC");
        var busDepartment = await db.Departments.AsNoTracking().SingleAsync(d => d.StudentEmailKeyword == "BUS");
        async Task<ApplicationUser> Coordinator(string prefix, int? department)
        {
            var user = new ApplicationUser { UserName = prefix + "@smiu.edu.pk", Email = prefix + "@smiu.edu.pk", FullName = prefix, DepartmentId = department };
            Check((await users.CreateAsync(user, password)).Succeeded && (await users.AddToRoleAsync(user, "Coordinator")).Succeeded, "Create Coordinator fixture " + prefix);
            return user;
        }
        var reviewer = await Coordinator("review-csc", cscDepartment.Id);
        var colleague = await Coordinator("review-csc-second", cscDepartment.Id);
        var busReviewer = await Coordinator("review-bus", busDepartment.Id);
        var missingDepartment = await Coordinator("review-no-department", null);
        using var csc = Client(factory);
        using var second = Client(factory);
        using var bus = Client(factory);
        await LoginAsync(csc, reviewer.Email!, password);
        await LoginAsync(second, colleague.Email!, password);
        await LoginAsync(bus, busReviewer.Email!, password);
        clock.Advance(TimeSpan.FromSeconds(61));
        using var student = Client(factory);
        using var otherStudent = Client(factory);
        using var busStudent = Client(factory);
        await LoginStudentAsync(student, "CSC20F005@smiu.edu.pk", sender);
        await LoginStudentAsync(otherStudent, "CSC20F006@smiu.edu.pk", sender);
        await LoginStudentAsync(busStudent, "BUS20F005@smiu.edu.pk", sender);
        var owner = await db.Users.AsNoTracking().SingleAsync(u => u.Email == "CSC20F005@smiu.edu.pk");
        var busOwner = await db.Users.AsNoTracking().SingleAsync(u => u.Email == "BUS20F005@smiu.edu.pk");
        Check(owner.StudentNumber == "CSC-20F-005" && owner.DepartmentId == cscDepartment.Id, "Review milestone preserves SMIU Student identity");
        async Task<int> Project(HttpClient client, string title, bool submit = true, bool fullPackage = false)
        {
            Check((await PostFormAsync(client, "/Student/Submissions/Create", SubmissionFields(title, "SaveDraft"))).StatusCode == HttpStatusCode.Redirect, "Review acceptance creates Draft " + title);
            var id = await db.ProjectSubmissions.Where(s => s.Title == title).Select(s => s.Id).SingleAsync();
            Check((await UploadTestFileAsync(client, id, "FinalReport.pdf")).StatusCode == HttpStatusCode.Redirect, "Review acceptance uploads report");
            if (fullPackage)
            {
                Check((await UploadTestFileAsync(client, id, "SourceCode.zip", TestArchive(), "2")).StatusCode == HttpStatusCode.Redirect, "Review acceptance uploads source ZIP");
                Check((await UploadTestFileAsync(client, id, "Presentation.pptx", TestArchive("ppt/presentation.xml"), "3")).StatusCode == HttpStatusCode.Redirect, "Review acceptance uploads presentation");
            }
            if (submit)
            {
                var page = await client.GetStringAsync($"/Student/Submissions/{id}/Edit");
                var values = SubmissionFields(title, "Submit"); values["EditToken"] = HiddenValue(page, "EditToken");
                values["Abstract"] = "Academic methodology"; values["Keywords"] = "academic, repository"; values["ProjectType"] = "2";
                values["AcademicYear"] = "2026-2027"; values["Semester"] = "Fall";
                Check((await client.PostAsync($"/Student/Submissions/{id}/Edit", Form(page, values))).StatusCode == HttpStatusCode.Redirect, "Review acceptance submits package");
            }
            return id;
        }
        async Task<HttpResponseMessage> Action(HttpClient client, string action, int id, Dictionary<string, string>? fields = null)
        {
            var page = await client.GetStringAsync("/");
            return await client.PostAsync($"/Coordinator/Submissions/{action}/{id}", Form(page, fields ?? new()));
        }
        var approvalId = await Project(student, "Milestone5 AI Based Academic Repository", fullPackage: true);
        var rejectionId = await Project(student, "Milestone5 Rejection case");
        var busId = await Project(busStudent, "BUS secret review title");
        var draftId = await Project(student, "CSC private review Draft", submit: false);
        var file = await db.ProjectFiles.AsNoTracking().FirstAsync(f => f.ProjectSubmissionId == approvalId);
        var busFile = await db.ProjectFiles.AsNoTracking().FirstAsync(f => f.ProjectSubmissionId == busId);
        var draftFile = await db.ProjectFiles.AsNoTracking().FirstAsync(f => f.ProjectSubmissionId == draftId);

        var dashboard = await csc.GetStringAsync("/Coordinator/Dashboard");
        foreach (var (status, label) in new[] { (SubmissionStatus.Submitted, "awaiting"), (SubmissionStatus.UnderReview, "underreview"), (SubmissionStatus.Approved, "approved"), (SubmissionStatus.Rejected, "rejected") })
        {
            var expected = await db.ProjectSubmissions.CountAsync(s => s.DepartmentId == cscDepartment.Id && s.Status == status);
            Check(dashboard.Contains($"data-review-count=\"{label}\">{expected}</strong>"), "Dashboard count is department-scoped: " + label);
        }
        Check(dashboard.Contains(cscDepartment.Name) && !dashboard.Contains("BUS secret") && !dashboard.Contains("CSC private review Draft"), "Coordinator dashboard hides other departments and Drafts");
        Check(dashboard.Contains("/Coordinator/ReviewQueue") && !dashboard.Contains("href=\"/Users\"") && !dashboard.Contains("New Submission"), "Coordinator navigation exposes review links only");
        var queue = await csc.GetStringAsync("/Coordinator/ReviewQueue");
        Check(queue.Contains("Milestone5 AI Based") && queue.Contains("CSC-20F-005") && !queue.Contains("BUS secret") && !queue.Contains("CSC private review Draft"), "Queue includes own Submitted projects and correct StudentNumber");
        var details = await csc.GetStringAsync($"/Coordinator/Submissions/Review/{approvalId}");
        Check(details.Contains("CSC-20F-005") && details.Contains("csc20f005@smiu.edu.pk") && details.Contains("FinalReport.pdf") && details.Contains("Start Review")
            && !details.Contains(file.StoredFileName), "Review Details exposes safe Student/project/resource information");
        Check(await db.ProjectSubmissions.AnyAsync(s => s.Id == approvalId && s.Status == SubmissionStatus.Submitted) && !await db.SubmissionReviews.AnyAsync(r => r.ProjectSubmissionId == approvalId), "Opening Details does not start a review");
        var downloaded = await csc.GetAsync($"/Coordinator/Files/Download/{file.Id}");
        Check(downloaded.StatusCode == HttpStatusCode.OK && (await downloaded.Content.ReadAsByteArrayAsync()).SequenceEqual(TestPdf)
            && downloaded.Content.Headers.ContentDisposition!.ToString().Contains(file.OriginalFileName), "Same-department Coordinator downloads original attachment");
        Check((await csc.GetAsync($"/Coordinator/Files/Download/{busFile.Id}")).StatusCode == HttpStatusCode.NotFound
            && (await bus.GetAsync($"/Coordinator/Files/Download/{file.Id}")).StatusCode == HttpStatusCode.NotFound, "Cross-department Coordinator file access blocked both directions");
        Check((await csc.GetAsync($"/Coordinator/Files/Download/{draftFile.Id}")).StatusCode == HttpStatusCode.NotFound
            && (await csc.GetAsync($"/Coordinator/Submissions/Review/{draftId}")).StatusCode == HttpStatusCode.NotFound, "Coordinator cannot discover Draft details or files");
        foreach (var action in new[] { "StartReview", "Approve", "Reject" })
        {
            Check((await Action(bus, action, approvalId, new() { ["ReviewId"] = "1", ["Comments"] = "Cross-department" })).StatusCode == HttpStatusCode.NotFound, "Cross-department POST blocked: " + action);
            Check((await Action(csc, action, draftId, new() { ["ReviewId"] = "1", ["Comments"] = "Draft" })).StatusCode == HttpStatusCode.NotFound, "Draft review mutation blocked: " + action);
            Check((await csc.GetAsync($"/Coordinator/Submissions/{action}/{approvalId}")).StatusCode == HttpStatusCode.MethodNotAllowed, action + " is POST-only");
            Check((await csc.PostAsync($"/Coordinator/Submissions/{action}/{approvalId}", new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode == HttpStatusCode.BadRequest, action + " requires antiforgery");
        }
        Check(!await db.SubmissionReviews.AnyAsync(r => r.ProjectSubmissionId == approvalId || r.ProjectSubmissionId == draftId || r.ProjectSubmissionId == busId), "Unauthorized actions create no review history");
        Check((await bus.GetAsync($"/Coordinator/Submissions/Review/{approvalId}")).StatusCode == HttpStatusCode.NotFound, "Direct cross-department review URL protected");
        Check(!(await bus.GetStringAsync("/Coordinator/Submissions?Search=Milestone5")).Contains("Milestone5 AI Based"), "Search never crosses department boundary");
        Check((await Action(csc, "Approve", approvalId, new() { ["ReviewId"] = "1" })).StatusCode == HttpStatusCode.Conflict, "Cannot approve directly from Submitted");
        var malicious = new Dictionary<string,string> { ["ReviewerId"] = busReviewer.Id, ["StudentId"] = busOwner.Id, ["DepartmentId"] = busDepartment.Id.ToString(),
            ["Status"] = "4", ["StartedAt"] = "2000-01-01", ["CompletedAt"] = "2000-01-01", ["Decision"] = "2", ["Id"] = busId.ToString() };
        Check((await Action(csc, "StartReview", approvalId, malicious)).StatusCode == HttpStatusCode.Redirect, "StartReview accepts authorized route and ignores forged metadata");
        var review = await db.SubmissionReviews.AsNoTracking().SingleAsync(r => r.ProjectSubmissionId == approvalId);
        Check(review.ReviewerId == reviewer.Id && review.Decision == ReviewDecision.Pending && review.ReviewRound == 1 && review.StartedAt == clock.GetUtcNow().UtcDateTime
            && review.CompletedAt is null && await db.ProjectSubmissions.AnyAsync(s => s.Id == approvalId && s.Status == SubmissionStatus.UnderReview && s.DepartmentId == cscDepartment.Id), "StartReview records server-derived reviewer/UTC/round and UnderReview atomically");
        Check((await Action(csc, "StartReview", approvalId)).StatusCode == HttpStatusCode.Conflict, "Repeated start cannot create another Pending review");
        var colleaguePage = await second.GetStringAsync($"/Coordinator/Submissions/Review/{approvalId}");
        Check(colleaguePage.Contains("Only that reviewer") && !colleaguePage.Contains("name=\"ReviewId\""), "Same-department colleague may view but not complete active review");
        foreach (var action in new[] { "Approve", "Reject" })
            Check((await Action(second, action, approvalId, new() { ["ReviewId"] = review.Id.ToString(), ["Comments"] = "Hijack" })).StatusCode == HttpStatusCode.Forbidden, "Second Coordinator cannot hijack active " + action);
        Check((await Action(csc, "Approve", approvalId, new() { ["ReviewId"] = "999999" })).StatusCode == HttpStatusCode.Conflict, "Forged active review ID rejected");
        Check((await Action(csc, "Approve", approvalId, new() { ["ReviewId"] = review.Id.ToString(), ["Comments"] = new string('x', 2001) })).StatusCode == HttpStatusCode.BadRequest, "Approval comments length enforced");
        const string approvedComment = "Submission reviewed and meets the required criteria.";
        malicious["ReviewId"] = review.Id.ToString(); malicious["Comments"] = approvedComment;
        Check((await Action(csc, "Approve", approvalId, malicious)).StatusCode == HttpStatusCode.Redirect, "Coordinator approves own active review");
        var approved = await db.SubmissionReviews.AsNoTracking().SingleAsync(r => r.Id == review.Id);
        Check(approved.Decision == ReviewDecision.Approved && approved.Comments == approvedComment && approved.CompletedAt >= approved.StartedAt && approved.ReviewerId == reviewer.Id
            && await db.ProjectSubmissions.AnyAsync(s => s.Id == approvalId && s.Status == SubmissionStatus.Approved), "Approval and completed history committed consistently");
        foreach (var action in new[] { "Approve", "Reject", "StartReview" })
            Check((await Action(csc, action, approvalId, new() { ["ReviewId"] = review.Id.ToString(), ["Comments"] = "Overwrite" })).StatusCode == HttpStatusCode.Conflict, "Completed decision cannot be overwritten: " + action);
        Check((await db.SubmissionReviews.AsNoTracking().SingleAsync(r => r.Id == review.Id)).Comments == approvedComment, "Completed comments remain unchanged");
        var studentDetails = await student.GetStringAsync($"/Student/Submissions/{approvalId}");
        Check(studentDetails.Contains("Review History") && studentDetails.Contains("Round 1") && studentDetails.Contains(approvedComment) && studentDetails.Contains(reviewer.FullName), "Student sees own approval history");
        Check((await otherStudent.GetAsync($"/Student/Submissions/{approvalId}")).StatusCode == HttpStatusCode.NotFound, "Another Student cannot see review information");

        Check((await Action(csc, "StartReview", rejectionId)).StatusCode == HttpStatusCode.Redirect, "Start rejection case");
        var rejectionReview = await db.SubmissionReviews.AsNoTracking().SingleAsync(r => r.ProjectSubmissionId == rejectionId);
        foreach (var comments in new[] { "", "   ", new string('x', 2001) })
            Check((await Action(csc, "Reject", rejectionId, new() { ["ReviewId"] = rejectionReview.Id.ToString(), ["Comments"] = comments })).StatusCode == HttpStatusCode.BadRequest, "Rejection requires bounded non-whitespace comments");
        Check(await db.ProjectSubmissions.AnyAsync(s => s.Id == rejectionId && s.Status == SubmissionStatus.UnderReview), "Invalid rejection leaves UnderReview unchanged");
        const string rejectionComment = "Please revise the methodology section and upload the missing supporting material. <script>alert(1)</script>";
        Check((await Action(csc, "Reject", rejectionId, new() { ["ReviewId"] = rejectionReview.Id.ToString(), ["Comments"] = rejectionComment })).StatusCode == HttpStatusCode.Redirect, "Coordinator rejects with required comments");
        Check(await db.SubmissionReviews.AnyAsync(r => r.Id == rejectionReview.Id && r.Decision == ReviewDecision.Rejected && r.Comments == rejectionComment && r.CompletedAt != null)
            && await db.ProjectSubmissions.AnyAsync(s => s.Id == rejectionId && s.Status == SubmissionStatus.Rejected), "Rejection history and status committed consistently");
        studentDetails = await student.GetStringAsync($"/Student/Submissions/{rejectionId}");
        Check(studentDetails.Contains("Please revise the methodology") && studentDetails.Contains("&lt;script&gt;") && !studentDetails.Contains("<script>alert(1)</script>"), "Student sees HTML-encoded rejection comments");
        foreach (var projectId in new[] { approvalId, rejectionId })
        {
            var resource = await db.ProjectFiles.AsNoTracking().FirstAsync(f => f.ProjectSubmissionId == projectId);
            Check((await student.GetAsync($"/ProjectFiles/Download/{resource.Id}")).StatusCode == HttpStatusCode.OK, "Student can download reviewed resources " + projectId);
            Check((await csc.GetAsync($"/Coordinator/Files/Download/{resource.Id}")).StatusCode == HttpStatusCode.OK, "Coordinator can download reviewed resources " + projectId);
            Check((await UploadTestFileAsync(student, projectId)).StatusCode == HttpStatusCode.Conflict, "Reviewed package rejects upload " + projectId);
            var page = await student.GetStringAsync("/");
            Check((await student.PostAsync($"/ProjectFiles/Delete/{resource.Id}", Form(page, new()))).StatusCode == HttpStatusCode.Conflict, "Reviewed package rejects deletion " + projectId);
            Check((await student.GetAsync($"/Student/Submissions/{projectId}/Edit")).StatusCode == HttpStatusCode.Conflict, "Reviewed project cannot be edited " + projectId);
            Check((await student.PostAsync($"/Student/Submissions/{projectId}/Edit", Form(page, new() { ["intent"] = "Submit", ["Title"] = "Resubmit", ["ProjectType"] = "2" }))).StatusCode == HttpStatusCode.Conflict, "Resubmission remains unavailable " + projectId);
        }
        await ReviewScopeAndConcurrencyChecks(factory, csc, second, student, reviewer, colleague, missingDepartment, owner, cscDepartment.Id, busDepartment.Id, password);
    }

    static async Task ReviewScopeAndConcurrencyChecks(WebApplicationFactory<Program> factory, HttpClient csc, HttpClient second, HttpClient student,
        ApplicationUser reviewer, ApplicationUser colleague, ApplicationUser noDepartment, ApplicationUser owner, int cscDepartment, int busDepartment, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var service = scope.ServiceProvider.GetRequiredService<IReviewService>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        async Task<int> Seed(string title, SubmissionStatus status = SubmissionStatus.Submitted, int? department = null)
        {
            var submission = new ProjectSubmission { Title = title, StudentId = owner.Id, DepartmentId = department ?? cscDepartment, Status = status,
                CreatedAt = DateTime.UtcNow, SubmittedAt = DateTime.UtcNow, ProjectType = ProjectType.ResearchProject, AcademicYear = "ReviewYear", Semester = "Spring" };
            db.ProjectSubmissions.Add(submission); await db.SaveChangesAsync(); return submission.Id;
        }
        async Task<HttpResponseMessage> Post(HttpClient client, string action, int id, int reviewId = 0)
        {
            var page = await client.GetStringAsync("/");
            return await client.PostAsync($"/Coordinator/Submissions/{action}/{id}", Form(page, new() { ["ReviewId"] = reviewId.ToString(), ["Comments"] = action == "Approve" ? "" : "Reviewed" }));
        }
        var raceId = await Seed("Concurrent review start");
        var race = await Task.WhenAll(Post(csc, "StartReview", raceId), Post(second, "StartReview", raceId));
        Check(race.Count(r => r.StatusCode == HttpStatusCode.Redirect) == 1 && race.Count(r => r.StatusCode == HttpStatusCode.Conflict) == 1,
            "Concurrent Coordinators cannot start duplicate active reviews");
        var active = await db.SubmissionReviews.AsNoTracking().SingleAsync(r => r.ProjectSubmissionId == raceId);
        var winner = active.ReviewerId == reviewer.Id ? csc : second;
        race = await Task.WhenAll(Post(winner, "Approve", raceId, active.Id), Post(winner, "Reject", raceId, active.Id));
        Check(race.Count(r => r.StatusCode == HttpStatusCode.Redirect) == 1 && race.Count(r => r.StatusCode == HttpStatusCode.Conflict) == 1, "Concurrent approve/reject yields one immutable decision");
        var completed = await db.SubmissionReviews.AsNoTracking().SingleAsync(r => r.Id == active.Id);
        var state = await db.ProjectSubmissions.AsNoTracking().SingleAsync(s => s.Id == raceId);
        Check(completed.CompletedAt.HasValue && ((completed.Decision == ReviewDecision.Approved && state.Status == SubmissionStatus.Approved)
            || (completed.Decision == ReviewDecision.Rejected && state.Status == SubmissionStatus.Rejected)), "Concurrent decision leaves matching status/history");
        var optionalId = await Seed("Optional approval comments");
        await Post(csc, "StartReview", optionalId);
        var optionalReview = await db.SubmissionReviews.AsNoTracking().SingleAsync(r => r.ProjectSubmissionId == optionalId);
        Check((await Post(csc, "Approve", optionalId, optionalReview.Id)).StatusCode == HttpStatusCode.Redirect, "Approval comments may be omitted");

        var legacyId = await Seed("Legacy UnderReview", SubmissionStatus.UnderReview);
        var legacyPage = await csc.GetStringAsync($"/Coordinator/Submissions/Review/{legacyId}");
        Check(legacyPage.Contains("no active review record") && (await Post(csc, "Approve", legacyId, 1)).StatusCode == HttpStatusCode.Conflict
            && !await db.SubmissionReviews.AnyAsync(r => r.ProjectSubmissionId == legacyId), "Legacy UnderReview without history is reported and cannot be completed");
        foreach (var status in new[] { SubmissionStatus.Approved, SubmissionStatus.Rejected })
        {
            var legacyCompletedId = await Seed("Legacy " + status, status);
            Check((await csc.GetStringAsync($"/Coordinator/Submissions/Review/{legacyCompletedId}")).Contains("no recorded review history")
                && !await db.SubmissionReviews.AnyAsync(r => r.ProjectSubmissionId == legacyCompletedId), "Legacy " + status + " has no invented review history");
        }
        for (var index = 0; index < 25; index++) await Seed("PagingCase " + index);
        await Seed("PagingCase secret", department: busDepartment);
        var filters = new ReviewFilterViewModel { Search = "PagingCase", Status = SubmissionStatus.Submitted, ProjectType = ProjectType.ResearchProject, AcademicYear = "ReviewYear", Semester = "Spring" };
        var first = await service.GetDepartmentSubmissionsAsync(reviewer.Id, filters, true);
        filters.Page = 2;
        var next = await service.GetDepartmentSubmissionsAsync(reviewer.Id, filters, true);
        Check(first.Total == 25 && first.Items.Count == 20 && next.Items.Count == 5 && !first.Items.Select(i => i.Id).Intersect(next.Items.Select(i => i.Id)).Any()
            && first.Items.Concat(next.Items).All(i => !i.Title.Contains("secret")), "Database filtering/pagination is bounded and department-scoped");
        filters.Page = int.MaxValue;
        Check((await service.GetDepartmentSubmissionsAsync(reviewer.Id, filters, false)).Filter.Page == 2, "Out-of-range page safely clamps without overflow");
        var filteredPage = await csc.GetStringAsync("/Coordinator/Submissions?Search=PagingCase&ProjectType=3&AcademicYear=ReviewYear&Semester=Spring&Status=1&Page=2");
        Check(filteredPage.Contains("25 matching submissions") && filteredPage.Contains("Page 2 of 2") && !filteredPage.Contains("PagingCase secret"), "HTTP filters bind and preserve scoped pagination");
        Check((await csc.GetAsync("/Coordinator/Submissions?Status=0")).StatusCode == HttpStatusCode.BadRequest
            && (await csc.GetAsync("/Coordinator/Submissions?Status=999")).StatusCode == HttpStatusCode.BadRequest, "Invalid/Draft filters rejected server-side");
        var searched = await service.GetDepartmentSubmissionsAsync(reviewer.Id, new() { Search = "CSC-20F-005" }, false);
        Check(searched.Total > 0 && searched.Items.All(i => i.StudentNumber == "CSC-20F-005"), "Search supports formatted StudentNumber");

        foreach (var userId in new[] { noDepartment.Id, owner.Id, (await users.FindByEmailAsync("admin@smiu.edu.pk"))!.Id })
        {
            try { await service.GetCoordinatorDashboardAsync(userId); Check(false, "Invalid review user must fail"); }
            catch (ReviewOperationException ex) { Check(ex.Status == 403 && (userId != noDepartment.Id || ex.Message.Contains("no department assigned")), "Service rejects non-Coordinator/unconfigured account directly"); }
        }
        try { await service.StartReviewAsync(noDepartment.Id, raceId); Check(false, "No department must fail"); }
        catch (ReviewOperationException ex) { Check(ex.Status == 403, "Missing department cannot mutate review through service"); }
        using (var unconfigured = Client(factory))
        {
            var page = await unconfigured.GetStringAsync("/Account/Login");
            var login = await unconfigured.PostAsync("/Account/Login", Form(page, new() { ["Email"] = noDepartment.Email!, ["Password"] = password }));
            Check(login.StatusCode == HttpStatusCode.OK && (await login.Content.ReadAsStringAsync()).Contains("Coordinator accounts require an assigned department"), "Unconfigured Coordinator cannot sign in and sees configuration guidance");
        }
        await db.Users.Where(u => u.Id == colleague.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DepartmentId, (int?)null));
        Check((await second.GetAsync("/Coordinator/ReviewQueue")).StatusCode == HttpStatusCode.Redirect, "Removing department invalidates Coordinator session");
        Check((await second.GetStringAsync("/Account/Login")).Contains("no department assigned"), "Revoked Coordinator session shows missing-department message");
        await db.Users.Where(u => u.Id == colleague.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DepartmentId, cscDepartment));
        await db.Users.Where(u => u.Id == colleague.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        try { await service.StartReviewAsync(colleague.Id, legacyId); Check(false, "Inactive reviewer must fail"); }
        catch (ReviewOperationException ex) { Check(ex.Status == 403, "Service rejects inactive Coordinator"); }
        try { await service.GetStudentHistoryAsync((await users.FindByEmailAsync("CSC20F006@smiu.edu.pk"))!.Id, raceId); Check(false, "Other Student history must fail"); }
        catch (ReviewOperationException ex) { Check(ex.Status == 404, "History service enforces Student ownership independently"); }

        using var anonymous = Client(factory);
        Check((await anonymous.GetAsync("/Coordinator/ReviewQueue")).StatusCode == HttpStatusCode.Redirect, "Anonymous review queue requires login");
        foreach (var role in new[] { "Admin", "DepartmentHead", "ORICQEC", "Student" })
        {
            using var staff = Client(factory);
            var client = role == "Student" ? student : staff;
            if (role != "Student") await LoginAsync(client, role + "@smiu.edu.pk", password);
            foreach (var path in new[] { "/Coordinator/ReviewQueue", $"/Coordinator/Submissions/Review/{raceId}", "/Coordinator/Files/Download/1" })
            {
                var response = await client.GetAsync(path);
                Check(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location!.OriginalString.Contains("AccessDenied"), role + " has no Coordinator GET powers");
            }
            foreach (var action in new[] { "StartReview", "Approve", "Reject" })
            {
                var response = await Post(client, action, raceId, active.Id);
                Check(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location!.OriginalString.Contains("AccessDenied"), role + " has no Coordinator " + action + " powers");
            }
        }

        var invariantId = await Seed("Pending uniqueness");
        await Post(csc, "StartReview", invariantId);
        db.SubmissionReviews.Add(new SubmissionReview { ProjectSubmissionId = invariantId, ReviewerId = reviewer.Id, ReviewRound = 2, StartedAt = clock.GetUtcNow().UtcDateTime });
        try { await db.SaveChangesAsync(); Check(false, "Duplicate Pending must fail"); }
        catch (DbUpdateException) { Check(true, "SQL unique index prevents two Pending reviews"); db.ChangeTracker.Clear(); }
        db.SubmissionReviews.Add(new SubmissionReview { ProjectSubmissionId = raceId, ReviewerId = reviewer.Id, ReviewRound = 1,
            Decision = ReviewDecision.Approved, StartedAt = clock.GetUtcNow().UtcDateTime, CompletedAt = clock.GetUtcNow().UtcDateTime });
        try { await db.SaveChangesAsync(); Check(false, "Duplicate round must fail"); }
        catch (DbUpdateException) { Check(true, "SQL unique index preserves one record per review round"); db.ChangeTracker.Clear(); }
        var failureId = await Seed("Review transaction rollback");
        await using var failingDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(db.Database.GetConnectionString())
            .AddInterceptors(new ReviewSaveFailure()).Options);
        var failingService = new ReviewService(failingDb, users, new SubmissionLock(failingDb), scope.ServiceProvider.GetRequiredService<IFileStorageService>(), clock, NullLogger<ReviewService>.Instance);
        try { await failingService.StartReviewAsync(reviewer.Id, failureId); Check(false, "Injected review save must fail"); }
        catch (ReviewOperationException) { Check(await db.ProjectSubmissions.AnyAsync(s => s.Id == failureId && s.Status == SubmissionStatus.Submitted)
            && !await db.SubmissionReviews.AnyAsync(r => r.ProjectSubmissionId == failureId), "Failed review start rolls back status and history together"); }
        await Post(csc, "StartReview", failureId);
        var failureReview = await db.SubmissionReviews.AsNoTracking().SingleAsync(r => r.ProjectSubmissionId == failureId);
        await using var decisionDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(db.Database.GetConnectionString())
            .AddInterceptors(new ReviewSaveFailure()).Options);
        var decisionService = new ReviewService(decisionDb, users, new SubmissionLock(decisionDb), scope.ServiceProvider.GetRequiredService<IFileStorageService>(), clock, NullLogger<ReviewService>.Instance);
        try { await decisionService.ApproveAsync(reviewer.Id, failureId, new() { ReviewId = failureReview.Id }); Check(false, "Injected decision save must fail"); }
        catch (ReviewOperationException) { Check(await db.ProjectSubmissions.AnyAsync(s => s.Id == failureId && s.Status == SubmissionStatus.UnderReview)
            && await db.SubmissionReviews.AnyAsync(r => r.Id == failureReview.Id && r.Decision == ReviewDecision.Pending && r.CompletedAt == null),
            "Failed decision rolls back submission and review together"); }
    }
}

internal sealed class ReviewSaveFailure : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<SubmissionReview>().Any(e => e.State is EntityState.Added or EntityState.Modified))
            throw new DbUpdateException("simulated review persistence failure");
        return ValueTask.FromResult(result);
    }
}
