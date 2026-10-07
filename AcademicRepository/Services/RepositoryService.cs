using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AcademicRepository.Services;

public interface IRepositoryService
{
    Task<RepositoryListViewModel> GetApprovedProjectsAsync(string userId, RepositoryFilterViewModel filter);
    Task<RepositoryDetailsViewModel> GetApprovedProjectDetailsAsync(string userId, int submissionId);
    Task<(Stream Stream, string Name, string ContentType)> DownloadAsync(string userId, int submissionId, int versionId, int fileId);
}

public sealed class RepositoryService(ApplicationDbContext db, UserManager<ApplicationUser> users,
    IFileStorageService storage, ILogger<RepositoryService> logger) : IRepositoryService
{
    private async Task<RepositoryScope> ScopeAsync(string userId)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Department).SingleOrDefaultAsync(u => u.Id == userId);
        if (user is null || !user.IsActive || !InstitutionalEmail.IsValid(user.Email))
            throw new ReviewOperationException(403, "An active institutional account is required.");
        if (user.DepartmentId is null || user.Department is null)
            throw new ReviewOperationException(403, "Your account requires an assigned department. Contact your administrator.");
        if (await users.IsInRoleAsync(user, "Coordinator")) return new("Approved Repository — " + user.Department.Name, userId, user.DepartmentId.Value, true);
        if (await users.IsInRoleAsync(user, "Student")) return new("My Approved Projects", userId, user.DepartmentId.Value, false);
        throw new ReviewOperationException(403, "Repository access is currently available to Students and Coordinators only.");
    }

    private IQueryable<ProjectSubmission> ApprovedScope(RepositoryScope scope) => db.ProjectSubmissions.AsNoTracking()
        .Where(s => s.Status == SubmissionStatus.Approved && s.DepartmentId == scope.DepartmentId && (scope.Coordinator || s.StudentId == scope.UserId));

    // Single source of truth for list, details and download. Resolve by the approved review's link, never MAX(version).
    private IQueryable<SubmissionReview> EligibleReviews(RepositoryScope scope)
    {
        var submissions = ApprovedScope(scope);
        return db.SubmissionReviews.AsNoTracking().Where(r => submissions.Any(s => s.Id == r.ProjectSubmissionId)
            && r.Decision == ReviewDecision.Approved && r.CompletedAt != null && r.CompletedAt >= r.StartedAt
            && r.ProjectSubmission.Reviews.Count(x => x.Decision == ReviewDecision.Approved) == 1
            && !r.ProjectSubmission.Reviews.Any(x => x.Decision == ReviewDecision.Pending)
            && r.SubmissionVersion != null && r.SubmissionVersion.ProjectSubmissionId == r.ProjectSubmissionId
            && r.SubmissionVersion.VersionNumber == r.ReviewRound
            && r.SubmissionVersion.DepartmentIdSnapshot == r.ProjectSubmission.DepartmentId
            && r.SubmissionVersion.CreatedByUserId == r.ProjectSubmission.StudentId
            && r.SubmissionVersion.SubmittedAt <= r.StartedAt
            && r.SubmissionVersion.TitleSnapshot != "" && r.SubmissionVersion.AbstractSnapshot != "" && r.SubmissionVersion.KeywordsSnapshot != ""
            && r.SubmissionVersion.Files.Any()
            && r.SubmissionVersion.Files.All(f => f.ProjectFile.ProjectSubmissionId == r.ProjectSubmissionId && f.ProjectFile.FileSize > 0));
    }

    private static IQueryable<RepositoryListItem> Items(IQueryable<SubmissionReview> query) => query
        .OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.ProjectSubmissionId)
        .Select(r => new RepositoryListItem(r.ProjectSubmissionId, r.SubmissionVersionId!.Value, r.SubmissionVersion!.VersionNumber,
            r.SubmissionVersion.TitleSnapshot, r.ProjectSubmission.Student.FullName, r.ProjectSubmission.Student.StudentNumber,
            r.SubmissionVersion.Department.Name, r.SubmissionVersion.ProjectTypeSnapshot, r.SubmissionVersion.AcademicYearSnapshot,
            r.SubmissionVersion.SemesterSnapshot, r.SubmissionVersion.SupervisorNameSnapshot, r.SubmissionVersion.KeywordsSnapshot, r.CompletedAt!.Value));

    private async Task<bool> AvailableAsync(IEnumerable<ProjectFile> files)
    {
        foreach (var file in files)
        {
            try
            {
                await using var stream = await storage.OpenReadAsync(file.StoredFileName);
                if (stream.Length != file.FileSize) throw new IOException("Invalid resource size.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Repository resource {FileId} unavailable ({ErrorType}).", file.Id, ex.GetType().Name);
                return false;
            }
        }
        return true;
    }

    public async Task<RepositoryListViewModel> GetApprovedProjectsAsync(string userId, RepositoryFilterViewModel filter)
    {
        var scope = await ScopeAsync(userId);
        if (filter.Search?.Length > 200 || filter.AcademicYear?.Length > 30 || filter.Semester?.Length > 50 || filter.Page < 1
            || (filter.ProjectType.HasValue && !Enum.IsDefined(filter.ProjectType.Value)))
            throw new ReviewOperationException(400, "Choose valid repository search criteria.");
        var query = EligibleReviews(scope);
        var inconsistent = await ApprovedScope(scope).AnyAsync(s => !query.Any(r => r.ProjectSubmissionId == s.Id));
        if (inconsistent) logger.LogWarning("Inconsistent approved repository records detected in authorized scope for user {UserId}.", userId);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            query = query.Where(r => r.SubmissionVersion!.TitleSnapshot.Contains(text) || r.SubmissionVersion.KeywordsSnapshot.Contains(text));
        }
        if (filter.ProjectType.HasValue) query = query.Where(r => r.SubmissionVersion!.ProjectTypeSnapshot == filter.ProjectType);
        if (!string.IsNullOrWhiteSpace(filter.AcademicYear)) { var year = filter.AcademicYear.Trim(); query = query.Where(r => r.SubmissionVersion!.AcademicYearSnapshot == year); }
        if (!string.IsNullOrWhiteSpace(filter.Semester)) { var semester = filter.Semester.Trim(); query = query.Where(r => r.SubmissionVersion!.SemesterSnapshot == semester); }
        const int pageSize = 20;
        var total = await query.CountAsync();
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        filter.Page = Math.Min(filter.Page, pages);
        var page = await Items(query).Skip((filter.Page - 1) * pageSize).Take(pageSize).ToListAsync();
        var versionIds = page.Select(i => i.VersionId).ToArray();
        var resources = await db.SubmissionVersionFiles.AsNoTracking().Where(f => versionIds.Contains(f.SubmissionVersionId))
            .Select(f => new { f.SubmissionVersionId, File = f.ProjectFile }).ToListAsync();
        var available = new List<RepositoryListItem>();
        foreach (var item in page)
        {
            if (await AvailableAsync(resources.Where(f => f.SubmissionVersionId == item.VersionId).Select(f => f.File))) available.Add(item);
            else inconsistent = true;
        }
        return new(scope, filter, available, total, pages, inconsistent);
    }

    private async Task<SubmissionReview> ResolveAsync(string userId, int submissionId)
    {
        var scope = await ScopeAsync(userId);
        var review = await EligibleReviews(scope).Include(r => r.ProjectSubmission.Student)
            .Include(r => r.Reviewer).Include(r => r.SubmissionVersion!.Department).SingleOrDefaultAsync(r => r.ProjectSubmissionId == submissionId);
        if (review is null)
        {
            if (await ApprovedScope(scope).AnyAsync(s => s.Id == submissionId))
                logger.LogWarning("Approved submission {SubmissionId} is not repository eligible.", submissionId);
            throw new ReviewOperationException(404, "Approved repository entry not found or unavailable.");
        }
        return review;
    }
    private Task<List<ProjectFile>> ResourcesAsync(int versionId) => db.SubmissionVersionFiles.AsNoTracking()
        .Where(f => f.SubmissionVersionId == versionId).OrderBy(f => f.ProjectFile.UploadedAt).ThenBy(f => f.ProjectFileId).Select(f => f.ProjectFile).ToListAsync();

    public async Task<RepositoryDetailsViewModel> GetApprovedProjectDetailsAsync(string userId, int submissionId)
    {
        var review = await ResolveAsync(userId, submissionId);
        var v = review.SubmissionVersion!;
        var files = await ResourcesAsync(v.Id);
        if (!await AvailableAsync(files)) throw new ReviewOperationException(404, "Approved repository entry is currently unavailable. Contact your administrator.");
        return new(submissionId, v.Id, v.VersionNumber, review.ReviewRound, review.ProjectSubmission.Student.FullName,
            review.ProjectSubmission.Student.StudentNumber, v.Department.Name, v.TitleSnapshot, v.AbstractSnapshot, v.KeywordsSnapshot,
            v.ProjectTypeSnapshot, v.SupervisorNameSnapshot, v.CourseNameSnapshot, v.CourseCodeSnapshot, v.AcademicYearSnapshot, v.SemesterSnapshot,
            v.SubmittedAt, review.CompletedAt!.Value, review.Reviewer.FullName, review.Comments,
            files.Select(f => new ProjectFileViewModel(f.Id, f.OriginalFileName, f.ResourceType, f.Description, f.FileSize, f.UploadedAt)).ToList());
    }
    public async Task<(Stream Stream, string Name, string ContentType)> DownloadAsync(string userId, int submissionId, int versionId, int fileId)
    {
        var review = await ResolveAsync(userId, submissionId);
        if (review.SubmissionVersionId != versionId) throw new ReviewOperationException(404, "Approved resource not found.");
        var files = await ResourcesAsync(versionId);
        var file = files.SingleOrDefault(f => f.Id == fileId);
        if (file is null) throw new ReviewOperationException(404, "Approved resource not found.");
        if (!await AvailableAsync(files)) throw new ReviewOperationException(404, "Approved repository resources are currently unavailable.");
        try { return (await storage.OpenReadAsync(file.StoredFileName), file.OriginalFileName, file.ContentType); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Repository resource {FileId} unavailable at download ({ErrorType}).", fileId, ex.GetType().Name);
            throw new ReviewOperationException(404, "Approved resource is currently unavailable.");
        }
    }
}
