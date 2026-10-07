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
    Task<DepartmentRepositoryStats> GetDepartmentRepositoryStatsAsync(string userId);
}

public sealed class RepositoryService(ApplicationDbContext db, UserManager<ApplicationUser> users,
    IFileStorageService storage, ILogger<RepositoryService> logger, TimeProvider clock, IConfiguration configuration) : IRepositoryService
{
    private async Task<RepositoryScope> ScopeAsync(string userId)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Department).SingleOrDefaultAsync(u => u.Id == userId);
        if (user is null || !user.IsActive || !InstitutionalEmail.IsValid(user.Email))
            throw new ReviewOperationException(403, "An active institutional account is required.");
        if (user.DepartmentId is null || user.Department is null)
            throw new ReviewOperationException(403, "Your account requires an assigned department. Contact your administrator.");
        if (await users.IsInRoleAsync(user, "DepartmentHead")) return new(user.Department.Name + " Academic Repository", userId, user.DepartmentId.Value, true, true, user.Department.Name);
        if (await users.IsInRoleAsync(user, "Coordinator")) return new("Approved Repository — " + user.Department.Name, userId, user.DepartmentId.Value, true);
        if (await users.IsInRoleAsync(user, "Student")) return new("My Approved Projects", userId, user.DepartmentId.Value, false);
        throw new ReviewOperationException(403, "Repository access requires a Student, Coordinator or DepartmentHead role.");
    }

    private IQueryable<ProjectSubmission> ApprovedScope(RepositoryScope scope) => db.ProjectSubmissions.AsNoTracking()
        .Where(s => s.Status == SubmissionStatus.Approved && s.DepartmentId == scope.DepartmentId && (scope.DepartmentWide || s.StudentId == scope.UserId));

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

    private static IQueryable<RepositoryListItem> Items(IQueryable<SubmissionReview> query, RepositorySort sort = RepositorySort.NewestApproved)
    {
        var ordered = sort switch
        {
            RepositorySort.OldestApproved => query.OrderBy(r => r.CompletedAt).ThenBy(r => r.ProjectSubmissionId),
            RepositorySort.TitleAZ => query.OrderBy(r => r.SubmissionVersion!.TitleSnapshot).ThenBy(r => r.ProjectSubmissionId),
            RepositorySort.TitleZA => query.OrderByDescending(r => r.SubmissionVersion!.TitleSnapshot).ThenByDescending(r => r.ProjectSubmissionId),
            _ => query.OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.ProjectSubmissionId)
        };
        return ordered.Select(r => new RepositoryListItem(r.ProjectSubmissionId, r.SubmissionVersionId!.Value, r.SubmissionVersion!.VersionNumber,
            r.SubmissionVersion.TitleSnapshot, r.ProjectSubmission.Student.FullName, r.ProjectSubmission.Student.StudentNumber,
            r.SubmissionVersion.Department.Name, r.SubmissionVersion.ProjectTypeSnapshot, r.SubmissionVersion.AcademicYearSnapshot,
            r.SubmissionVersion.SemesterSnapshot, r.SubmissionVersion.SupervisorNameSnapshot, r.SubmissionVersion.KeywordsSnapshot, r.CompletedAt!.Value));
    }

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
            || filter.Supervisor?.Length > 150 || !Enum.IsDefined(filter.Sort)
            || (filter.ProjectType.HasValue && !Enum.IsDefined(filter.ProjectType.Value)))
            throw new ReviewOperationException(400, "Choose valid repository search criteria.");
        var query = EligibleReviews(scope);
        var years = scope.DepartmentHead ? await query.Where(r => r.SubmissionVersion!.AcademicYearSnapshot != null && r.SubmissionVersion.AcademicYearSnapshot != "")
            .Select(r => r.SubmissionVersion!.AcademicYearSnapshot!).Distinct().OrderByDescending(y => y).Take(100).ToListAsync() : null;
        var inconsistent = await ApprovedScope(scope).AnyAsync(s => !query.Any(r => r.ProjectSubmissionId == s.Id));
        if (inconsistent) logger.LogWarning("Inconsistent approved repository records detected in authorized scope for user {UserId}.", userId);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            if (scope.DepartmentHead)
                query = query.Where(r => EF.Functions.Collate(r.SubmissionVersion!.TitleSnapshot, "Latin1_General_100_CI_AS").Contains(text)
                    || EF.Functions.Collate(r.SubmissionVersion.AbstractSnapshot, "Latin1_General_100_CI_AS").Contains(text)
                    || EF.Functions.Collate(r.SubmissionVersion.KeywordsSnapshot, "Latin1_General_100_CI_AS").Contains(text)
                    || EF.Functions.Collate(r.ProjectSubmission.Student.FullName, "Latin1_General_100_CI_AS").Contains(text)
                    || (r.ProjectSubmission.Student.StudentNumber != null && EF.Functions.Collate(r.ProjectSubmission.Student.StudentNumber, "Latin1_General_100_CI_AS").Contains(text))
                    || (r.SubmissionVersion.SupervisorNameSnapshot != null && EF.Functions.Collate(r.SubmissionVersion.SupervisorNameSnapshot, "Latin1_General_100_CI_AS").Contains(text)));
            else query = query.Where(r => r.SubmissionVersion!.TitleSnapshot.Contains(text) || r.SubmissionVersion.KeywordsSnapshot.Contains(text));
        }
        if (!string.IsNullOrWhiteSpace(filter.Supervisor))
        {
            if (!scope.DepartmentHead) throw new ReviewOperationException(400, "Supervisor filtering is available on the DepartmentHead repository.");
            var supervisor = filter.Supervisor.Trim();
            query = query.Where(r => r.SubmissionVersion!.SupervisorNameSnapshot != null && EF.Functions.Collate(r.SubmissionVersion.SupervisorNameSnapshot, "Latin1_General_100_CI_AS").Contains(supervisor));
        }
        if (filter.ProjectType.HasValue) query = query.Where(r => r.SubmissionVersion!.ProjectTypeSnapshot == filter.ProjectType);
        if (!string.IsNullOrWhiteSpace(filter.AcademicYear)) { var year = filter.AcademicYear.Trim(); query = query.Where(r => r.SubmissionVersion!.AcademicYearSnapshot == year); }
        if (!string.IsNullOrWhiteSpace(filter.Semester)) { var semester = filter.Semester.Trim(); query = query.Where(r => r.SubmissionVersion!.SemesterSnapshot == semester); }
        const int pageSize = 20;
        var total = await query.CountAsync();
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        filter.Page = Math.Min(filter.Page, pages);
        var page = await Items(query, filter.Sort).Skip((filter.Page - 1) * pageSize).Take(pageSize).ToListAsync();
        var versionIds = page.Select(i => i.VersionId).ToArray();
        var resources = await db.SubmissionVersionFiles.AsNoTracking().Where(f => versionIds.Contains(f.SubmissionVersionId))
            .Select(f => new { f.SubmissionVersionId, File = f.ProjectFile }).ToListAsync();
        var available = new List<RepositoryListItem>();
        foreach (var item in page)
        {
            if (await AvailableAsync(resources.Where(f => f.SubmissionVersionId == item.VersionId).Select(f => f.File))) available.Add(item);
            else inconsistent = true;
        }
        return new(scope, filter, available, total, pages, inconsistent, years);
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
            files.Select(f => new ProjectFileViewModel(f.Id, f.OriginalFileName, f.ResourceType, f.Description, f.FileSize, f.UploadedAt)).ToList(), review.ProjectSubmission.Student.Email);
    }
    public async Task<DepartmentRepositoryStats> GetDepartmentRepositoryStatsAsync(string userId)
    {
        var scope = await ScopeAsync(userId);
        if (!scope.DepartmentHead) throw new ReviewOperationException(403, "DepartmentHead access is required for department insights.");
        var query = EligibleReviews(scope);
        var total = await query.CountAsync();
        var types = await query.GroupBy(r => r.SubmissionVersion!.ProjectTypeSnapshot).Select(g => new RepositoryTypeCount(g.Key, g.Count())).ToListAsync();
        var years = await query.GroupBy(r => r.SubmissionVersion!.AcademicYearSnapshot).OrderByDescending(g => g.Key).Select(g => new RepositoryGroupCount(g.Key ?? "Not recorded", g.Count())).ToListAsync();
        var semesters = await query.GroupBy(r => r.SubmissionVersion!.SemesterSnapshot).OrderBy(g => g.Key).Select(g => new RepositoryGroupCount(g.Key ?? "Not recorded", g.Count())).ToListAsync();
        var year = clock.GetUtcNow().Month >= 7 ? clock.GetUtcNow().Year : clock.GetUtcNow().Year - 1;
        var currentYear = configuration["Repository:CurrentAcademicYear"] ?? $"{year}-{year + 1}";
        var currentCount = await query.CountAsync(r => r.SubmissionVersion!.AcademicYearSnapshot == currentYear);
        var recent = await GetApprovedProjectsAsync(userId, new());
        return new(scope.DepartmentName, total, types, years, semesters, currentYear, currentCount, recent.Items.Take(5).ToList(), recent.HasUnavailableRecords);
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
