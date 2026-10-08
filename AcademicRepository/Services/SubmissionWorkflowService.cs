using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AcademicRepository.Services;

public static class SubmissionWorkflow
{
    public static bool CanEdit(SubmissionStatus status) => status is SubmissionStatus.Draft or SubmissionStatus.Revision;
    public static bool CanDiscover(SubmissionStatus status) => status is SubmissionStatus.Submitted or SubmissionStatus.UnderReview or SubmissionStatus.Approved or SubmissionStatus.Rejected;
}

public sealed class SubmissionWorkflowService(ApplicationDbContext db, UserManager<ApplicationUser> users,
    StudentDepartmentService departments, SubmissionLock locks, IFileStorageService storage, TimeProvider clock,
    ILogger<SubmissionWorkflowService> logger)
{
    public async Task<bool> CanStartRevisionAsync(string userId, int id)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && u.IsActive);
        if (user is null || !await users.IsInRoleAsync(user, "Student") || !await departments.MatchesAsync(user)) return false;
        if (!await db.ProjectSubmissions.AnyAsync(s => s.Id == id && s.StudentId == userId && s.DepartmentId == user.DepartmentId && s.Status == SubmissionStatus.Rejected)) return false;
        var review = await db.SubmissionReviews.AsNoTracking().Where(r => r.ProjectSubmissionId == id).OrderByDescending(r => r.ReviewRound)
            .Select(r => new { r.Decision, r.CompletedAt, r.ReviewRound, Version = r.SubmissionVersion == null ? 0 : r.SubmissionVersion.VersionNumber }).FirstOrDefaultAsync();
        return review is { Decision: ReviewDecision.Rejected, CompletedAt: not null } && review.Version == review.ReviewRound;
    }
    public async Task StartRevisionAsync(string userId, int id)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var submission = await locks.OwnedAsync(id, userId);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId);
        if (submission is null || user is null || !user.IsActive || !await users.IsInRoleAsync(user, "Student")
            || user.DepartmentId != submission.DepartmentId || !await departments.MatchesAsync(user))
            throw new ReviewOperationException(404, "Submission not found.");
        var latest = await db.SubmissionReviews.AsNoTracking().Where(r => r.ProjectSubmissionId == id)
            .OrderByDescending(r => r.ReviewRound).FirstOrDefaultAsync();
        if (submission.Status != SubmissionStatus.Rejected || latest is null || latest.Decision != ReviewDecision.Rejected
            || latest.CompletedAt is null || latest.SubmissionVersionId is null
            || await db.SubmissionReviews.AnyAsync(r => r.ProjectSubmissionId == id && r.Decision == ReviewDecision.Pending))
            throw new ReviewOperationException(409, "Only a rejected submission with a preserved submitted version can start revision. Contact your administrator for legacy records.");
        var version = await db.SubmissionVersions.AsNoTracking().SingleAsync(v => v.Id == latest.SubmissionVersionId);
        if (version.ProjectSubmissionId != id || version.VersionNumber != latest.ReviewRound)
            throw new ReviewOperationException(409, "The review version is inconsistent. Contact your administrator.");
        submission.Status = SubmissionStatus.Revision;
        submission.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        try { await db.SaveChangesAsync(); await transaction.CommitAsync(); }
        catch (DbUpdateException exception)
        {
            logger.LogWarning(exception, "Revision start conflicted. Action={Action} UserId={UserId} SubmissionId={SubmissionId} Result={Result}", "RevisionStarted", userId, id, "Conflict");
            throw new ReviewOperationException(409, "The submission changed. Reload before starting revision.");
        }
        logger.LogInformation("Submission lifecycle event. Action={Action} UserId={UserId} Role={Role} DepartmentId={DepartmentId} SubmissionId={SubmissionId} Result={Result}",
            "RevisionStarted", userId, "Student", user.DepartmentId, id, "Revision");
    }

    // Caller holds the parent lock and transaction, and saves this snapshot with the transition.
    public async Task SubmitAsync(string userId, ProjectSubmission submission)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && u.IsActive);
        if (user is null || submission.StudentId != userId || user.DepartmentId != submission.DepartmentId
            || !await users.IsInRoleAsync(user, "Student") || !await departments.MatchesAsync(user))
            throw new ReviewOperationException(404, "Submission not found.");
        if (!SubmissionWorkflow.CanEdit(submission.Status)) throw new ReviewOperationException(409, "This submission cannot be submitted in its current state.");
        if (string.IsNullOrWhiteSpace(submission.Title) || string.IsNullOrWhiteSpace(submission.Abstract) || string.IsNullOrWhiteSpace(submission.Keywords))
            throw new ReviewOperationException(400, "Title, abstract and keywords are required before submitting.");
        var files = await db.ProjectFiles.Where(f => f.ProjectSubmissionId == submission.Id && f.IsActive).ToListAsync();
        if (files.Count == 0) throw new ReviewOperationException(400, "Upload at least one active resource before submitting.");
        foreach (var file in files)
        {
            try { await using var stream = await storage.OpenReadAsync(file.StoredFileName); if (stream.Length != file.FileSize) throw new IOException(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new ReviewOperationException(409, "A resource is unavailable or invalid. Correct the resource set before submitting."); }
        }
        var latest = await db.SubmissionVersions.Where(v => v.ProjectSubmissionId == submission.Id).MaxAsync(v => (int?)v.VersionNumber) ?? 0;
        if (submission.Status == SubmissionStatus.Revision && latest == 0)
            throw new ReviewOperationException(409, "Historical version is missing. Contact your administrator.");
        if (submission.Status == SubmissionStatus.Revision)
        {
            var review = await db.SubmissionReviews.AsNoTracking().Where(r => r.ProjectSubmissionId == submission.Id).OrderByDescending(r => r.ReviewRound).FirstOrDefaultAsync();
            if (review is null || review.Decision != ReviewDecision.Rejected || review.CompletedAt is null || review.ReviewRound != latest || review.SubmissionVersionId is null)
                throw new ReviewOperationException(409, "The rejected review and latest submitted version are inconsistent.");
        }
        var now = clock.GetUtcNow().UtcDateTime;
        var snapshot = Snapshot(submission, latest + 1, now);
        snapshot.Files = files.Select(f => new SubmissionVersionFile { ProjectFileId = f.Id }).ToList();
        db.SubmissionVersions.Add(snapshot);
        submission.Status = SubmissionStatus.Submitted;
        submission.SubmittedAt = now;
        submission.UpdatedAt = now;
    }

    internal static SubmissionVersion Snapshot(ProjectSubmission s, int number, DateTime submittedAt) => new()
    {
        ProjectSubmissionId = s.Id, VersionNumber = number, CreatedAt = submittedAt, SubmittedAt = submittedAt,
        CreatedByUserId = s.StudentId, DepartmentIdSnapshot = s.DepartmentId, TitleSnapshot = s.Title,
        AbstractSnapshot = s.Abstract, KeywordsSnapshot = s.Keywords, ProjectTypeSnapshot = s.ProjectType,
        SupervisorNameSnapshot = s.SupervisorName, CourseNameSnapshot = s.CourseName, CourseCodeSnapshot = s.CourseCode,
        AcademicYearSnapshot = s.AcademicYear, SemesterSnapshot = s.Semester
    };
}
