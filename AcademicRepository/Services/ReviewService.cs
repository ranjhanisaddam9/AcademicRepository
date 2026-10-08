using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AcademicRepository.Services;

public sealed class ReviewOperationException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public interface IReviewService
{
    Task<CoordinatorDashboardViewModel> GetCoordinatorDashboardAsync(string userId);
    Task<ReviewListViewModel> GetDepartmentSubmissionsAsync(string userId, ReviewFilterViewModel filter, bool queueOnly);
    Task<ReviewDetailsViewModel> GetSubmissionForReviewAsync(string userId, int id);
    Task StartReviewAsync(string userId, int id);
    Task ApproveAsync(string userId, int id, ReviewDecisionViewModel model);
    Task RejectAsync(string userId, int id, ReviewDecisionViewModel model);
    Task<IReadOnlyList<ReviewHistoryItem>> GetStudentHistoryAsync(string userId, int id);
    Task<(Stream Stream, string Name, string ContentType)> DownloadAsync(string userId, int fileId);
}

public sealed class ReviewService(ApplicationDbContext db, UserManager<ApplicationUser> users, SubmissionLock submissionLock,
    IFileStorageService storage, TimeProvider clock, ILogger<ReviewService> logger) : IReviewService
{
    private async Task<CoordinatorProfile> CoordinatorAsync(string userId)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Department).SingleOrDefaultAsync(u => u.Id == userId);
        if (user is null || !user.IsActive || !InstitutionalEmail.IsValid(user.Email) || !await users.IsInRoleAsync(user, "Coordinator"))
            throw new ReviewOperationException(403, "An active Coordinator account is required.");
        if (user.DepartmentId is null || user.Department is null)
            throw new ReviewOperationException(403, "Your Coordinator account has no department assigned. Contact your administrator before reviewing submissions.");
        return new(user.FullName, user.DepartmentId.Value, user.Department.Name);
    }
    private IQueryable<ProjectSubmission> DepartmentSubmissions(int departmentId) => db.ProjectSubmissions.AsNoTracking()
        .Where(s => s.DepartmentId == departmentId && (s.Status == SubmissionStatus.Submitted || s.Status == SubmissionStatus.UnderReview || s.Status == SubmissionStatus.Approved || s.Status == SubmissionStatus.Rejected));
    private IQueryable<ReviewQueueItem> Rows(IQueryable<ProjectSubmission> query, string sort = "SubmittedAt", string direction = "desc")
    {
        query = (sort, direction) switch
        {
            ("Title", "asc") => query.OrderBy(s => s.Title).ThenBy(s => s.Id),
            ("Title", _) => query.OrderByDescending(s => s.Title).ThenBy(s => s.Id),
            ("Student", "asc") => query.OrderBy(s => s.Student.FullName).ThenBy(s => s.Id),
            ("Student", _) => query.OrderByDescending(s => s.Student.FullName).ThenBy(s => s.Id),
            ("Status", "asc") => query.OrderBy(s => s.Status).ThenBy(s => s.Id),
            ("Status", _) => query.OrderByDescending(s => s.Status).ThenBy(s => s.Id),
            ("Type", "asc") => query.OrderBy(s => s.ProjectType).ThenBy(s => s.Id),
            ("Type", _) => query.OrderByDescending(s => s.ProjectType).ThenBy(s => s.Id),
            ("SubmittedAt", "asc") => query.OrderBy(s => s.SubmittedAt).ThenBy(s => s.Id),
            _ => query.OrderByDescending(s => s.SubmittedAt).ThenByDescending(s => s.Id)
        };
        return query
        .Select(s => new ReviewQueueItem(s.Id, s.Title, s.Student.FullName, s.Student.StudentNumber,
            s.ProjectType, s.AcademicYear, s.Semester, s.SubmittedAt, s.Status,
            db.SubmissionVersions.Where(v => v.ProjectSubmissionId == s.Id).Max(v => (int?)v.VersionNumber) ?? 0));
    }
    public async Task<CoordinatorDashboardViewModel> GetCoordinatorDashboardAsync(string userId)
    {
        var profile = await CoordinatorAsync(userId);
        var query = DepartmentSubmissions(profile.DepartmentId);
        var counts = await query.GroupBy(s => s.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync();
        int Count(SubmissionStatus status) => counts.SingleOrDefault(c => c.Status == status)?.Count ?? 0;
        return new(profile, Count(SubmissionStatus.Submitted), Count(SubmissionStatus.UnderReview), Count(SubmissionStatus.Approved),
            Count(SubmissionStatus.Rejected), await Rows(query).Take(10).ToListAsync());
    }
    public async Task<ReviewListViewModel> GetDepartmentSubmissionsAsync(string userId, ReviewFilterViewModel filter, bool queueOnly)
    {
        var profile = await CoordinatorAsync(userId);
        if ((filter.Status.HasValue && !SubmissionWorkflow.CanDiscover(filter.Status.Value))
            || (filter.ProjectType.HasValue && !Enum.IsDefined(filter.ProjectType.Value)) || filter.Search?.Length > 200
            || filter.AcademicYear?.Length > 30 || filter.Semester?.Length > 50)
            throw new ReviewOperationException(400, "Choose valid review filters. Drafts are private to Students.");
        filter.Sort = new[] { "Title", "Student", "Status", "Type", "SubmittedAt" }.FirstOrDefault(s => string.Equals(s, filter.Sort, StringComparison.OrdinalIgnoreCase)) ?? "SubmittedAt";
        filter.Direction = string.Equals(filter.Direction, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        var query = DepartmentSubmissions(profile.DepartmentId);
        if (queueOnly) query = query.Where(s => s.Status == SubmissionStatus.Submitted || s.Status == SubmissionStatus.UnderReview);
        if (filter.Status.HasValue) query = query.Where(s => s.Status == filter.Status);
        if (filter.ProjectType.HasValue) query = query.Where(s => s.ProjectType == filter.ProjectType);
        if (!string.IsNullOrWhiteSpace(filter.AcademicYear)) { var year = filter.AcademicYear.Trim(); query = query.Where(s => s.AcademicYear == year); }
        if (!string.IsNullOrWhiteSpace(filter.Semester)) { var semester = filter.Semester.Trim(); query = query.Where(s => s.Semester == semester); }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(s => s.Title.Contains(search) || s.Student.FullName.Contains(search) || (s.Student.StudentNumber != null && s.Student.StudentNumber.Contains(search)));
        }
        var results = await Rows(query, filter.Sort, filter.Direction).ToPagedResultAsync(filter.Page, filter.PageSize);
        filter.Page = results.PageNumber;
        filter.PageSize = results.PageSize;
        return new(profile, filter, queueOnly, results);
    }
    private Task<List<ReviewHistoryItem>> HistoryAsync(int id) => db.SubmissionReviews.AsNoTracking().Where(r => r.ProjectSubmissionId == id)
        .OrderBy(r => r.ReviewRound).Select(r => new ReviewHistoryItem(r.ReviewRound, r.Reviewer.FullName, r.Decision, r.Comments, r.StartedAt, r.CompletedAt, r.SubmissionVersionId, r.SubmissionVersion == null ? null : (DateTime?)r.SubmissionVersion.SubmittedAt)).ToListAsync();
    public async Task<ReviewDetailsViewModel> GetSubmissionForReviewAsync(string userId, int id)
    {
        var profile = await CoordinatorAsync(userId);
        var submission = await DepartmentSubmissions(profile.DepartmentId).Include(s => s.Student).Include(s => s.Department).SingleOrDefaultAsync(s => s.Id == id);
        if (submission is null)
        {
            logger.LogWarning("Coordinator submission access unavailable. Action={Action} UserId={UserId} DepartmentId={DepartmentId} SubmissionId={SubmissionId} Result={Result}",
                "OpenReview", userId, profile.DepartmentId, id, "NotFoundOrOutsideDepartment");
            throw new ReviewOperationException(404, "Submission not found.");
        }
        var active = await db.SubmissionReviews.AsNoTracking().SingleOrDefaultAsync(r => r.ProjectSubmissionId == id && r.Decision == ReviewDecision.Pending);
        var history = await HistoryAsync(id);
        string? message = submission.Status == SubmissionStatus.UnderReview && active is null
            ? "This legacy submission has no active review record. Contact your administrator; no decision can be recorded."
            : active is not null && active.ReviewerId != userId ? "Another Coordinator started this review. Only that reviewer can complete it." : null;
        if (history.Count == 0 && submission.Status is SubmissionStatus.Approved or SubmissionStatus.Rejected)
            message = "This legacy submission has no recorded review history.";
        var project = new SubmissionDetailsViewModel(submission.Id, submission.Title, submission.ProjectType, submission.Status,
            submission.Student.FullName, submission.Department.Name, submission.Abstract, submission.Keywords, submission.SupervisorName,
            submission.CourseName, submission.CourseCode, submission.AcademicYear, submission.Semester, submission.CreatedAt, submission.UpdatedAt, submission.SubmittedAt);
        var files = await db.ProjectFiles.AsNoTracking().Where(f => f.ProjectSubmissionId == id && f.IsActive)
            .OrderBy(f => f.UploadedAt).ThenBy(f => f.Id)
            .Select(f => new ProjectFileViewModel(f.Id, f.OriginalFileName, f.ResourceType, f.Description, f.FileSize, f.UploadedAt)).ToListAsync();
        return new(project, submission.Student.StudentNumber, submission.Student.Email ?? "", files, history,
            submission.Status == SubmissionStatus.Submitted && active is null,
            submission.Status == SubmissionStatus.UnderReview && active?.ReviewerId == userId, active?.Id, message);
    }
    public async Task StartReviewAsync(string userId, int id)
    {
        var profile = await CoordinatorAsync(userId);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var submission = await submissionLock.DepartmentAsync(id, profile.DepartmentId);
        if (submission is null)
        {
            logger.LogWarning("Coordinator submission access unavailable. Action={Action} UserId={UserId} DepartmentId={DepartmentId} SubmissionId={SubmissionId} Result={Result}",
                "StartReview", userId, profile.DepartmentId, id, "NotFoundOrOutsideDepartment");
            throw new ReviewOperationException(404, "Submission not found.");
        }
        if (submission.Status != SubmissionStatus.Submitted || await db.SubmissionReviews.AnyAsync(r => r.ProjectSubmissionId == id && r.Decision == ReviewDecision.Pending))
            throw new ReviewOperationException(409, "This submission is no longer awaiting review. Reload its current status.");
        var round = (await db.SubmissionReviews.Where(r => r.ProjectSubmissionId == id).MaxAsync(r => (int?)r.ReviewRound) ?? 0) + 1;
        var version = await db.SubmissionVersions.AsNoTracking().Where(v => v.ProjectSubmissionId == id).OrderByDescending(v => v.VersionNumber).FirstOrDefaultAsync();
        if (version is not null && version.VersionNumber != round)
            throw new ReviewOperationException(409, "Submission version and review round do not match. Contact your administrator.");
        var now = clock.GetUtcNow().UtcDateTime;
        db.SubmissionReviews.Add(new SubmissionReview { ProjectSubmissionId = id, ReviewerId = userId, ReviewRound = round, StartedAt = now, SubmissionVersionId = version?.Id });
        submission.Status = SubmissionStatus.UnderReview;
        submission.UpdatedAt = now;
        await SaveAsync();
        await transaction.CommitAsync();
        logger.LogInformation("Submission lifecycle event. Action={Action} UserId={UserId} Role={Role} DepartmentId={DepartmentId} SubmissionId={SubmissionId} Round={Round} Result={Result}",
            "ReviewStarted", userId, "Coordinator", profile.DepartmentId, id, round, "UnderReview");
    }
    public Task ApproveAsync(string userId, int id, ReviewDecisionViewModel model) => CompleteAsync(userId, id, model, ReviewDecision.Approved);
    public Task RejectAsync(string userId, int id, ReviewDecisionViewModel model) => CompleteAsync(userId, id, model, ReviewDecision.Rejected);
    private async Task CompleteAsync(string userId, int id, ReviewDecisionViewModel model, ReviewDecision decision)
    {
        var profile = await CoordinatorAsync(userId);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var submission = await submissionLock.DepartmentAsync(id, profile.DepartmentId);
        if (submission is null)
        {
            logger.LogWarning("Coordinator submission access unavailable. Action={Action} UserId={UserId} DepartmentId={DepartmentId} SubmissionId={SubmissionId} Result={Result}",
                decision == ReviewDecision.Approved ? "ApproveSubmission" : "RejectSubmission", userId, profile.DepartmentId, id, "NotFoundOrOutsideDepartment");
            throw new ReviewOperationException(404, "Submission not found.");
        }
        if (submission.Status != SubmissionStatus.UnderReview) throw new ReviewOperationException(409, "This submission is not under review. A completed decision cannot be changed.");
        var review = await db.SubmissionReviews.SingleOrDefaultAsync(r => r.ProjectSubmissionId == id && r.Decision == ReviewDecision.Pending);
        if (review is null) throw new ReviewOperationException(409, "No active review exists. Contact your administrator.");
        if (review.ReviewerId != userId) throw new ReviewOperationException(403, "Only the Coordinator who started this review can complete it.");
        if (model.ReviewId != review.Id) throw new ReviewOperationException(409, "The active review changed. Reload the page before deciding.");
        var comments = model.Comments?.Trim();
        if (model.Comments?.Length > 2000 || (decision == ReviewDecision.Rejected && string.IsNullOrWhiteSpace(comments)))
            throw new ReviewOperationException(400, "Rejection requires comments. Comments must be at most 2000 characters.");
        review.Decision = decision;
        review.Comments = string.IsNullOrWhiteSpace(comments) ? null : comments;
        review.CompletedAt = clock.GetUtcNow().UtcDateTime;
        submission.Status = decision == ReviewDecision.Approved ? SubmissionStatus.Approved : SubmissionStatus.Rejected;
        submission.UpdatedAt = review.CompletedAt;
        await SaveAsync();
        await transaction.CommitAsync();
        logger.LogInformation("Submission lifecycle event. Action={Action} UserId={UserId} Role={Role} DepartmentId={DepartmentId} SubmissionId={SubmissionId} Result={Result}",
            decision == ReviewDecision.Approved ? "ReviewApproved" : "ReviewRejected", userId, "Coordinator", profile.DepartmentId, id, decision.ToString());
    }
    private async Task SaveAsync()
    {
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Review persistence conflict. Result={Result}", "Conflict");
            throw new ReviewOperationException(409, "The review changed or could not be saved. Reload the page before trying again.");
        }
    }
    public async Task<IReadOnlyList<ReviewHistoryItem>> GetStudentHistoryAsync(string userId, int id)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && u.IsActive);
        if (user is null || !await users.IsInRoleAsync(user, "Student") || !await db.ProjectSubmissions.AnyAsync(s => s.Id == id && s.StudentId == userId))
            throw new ReviewOperationException(404, "Submission not found.");
        return await HistoryAsync(id);
    }
    public async Task<(Stream Stream, string Name, string ContentType)> DownloadAsync(string userId, int fileId)
    {
        var profile = await CoordinatorAsync(userId);
        var file = await db.ProjectFiles.AsNoTracking().Where(f => f.Id == fileId
            && f.ProjectSubmission.DepartmentId == profile.DepartmentId
            && (f.ProjectSubmission.Status == SubmissionStatus.Submitted || f.ProjectSubmission.Status == SubmissionStatus.UnderReview || f.ProjectSubmission.Status == SubmissionStatus.Approved || f.ProjectSubmission.Status == SubmissionStatus.Rejected)
            && (f.IsActive || db.SubmissionVersionFiles.Any(v => v.ProjectFileId == f.Id))).SingleOrDefaultAsync();
        if (file is null) throw new ReviewOperationException(404, "Resource not found.");
        try { return (await storage.OpenReadAsync(file.StoredFileName), file.OriginalFileName, file.ContentType); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Review resource unavailable. FileId={FileId} Result={Result}", fileId, "Unavailable");
            throw new ReviewOperationException(404, "Resource is currently unavailable. Contact your administrator.");
        }
    }
}
