using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AcademicRepository.Services;

public interface IRepositoryService
{
    Task<RepositoryListViewModel> GetApprovedProjectsAsync(string userId, RepositoryFilterViewModel filter, bool institutionWide = false);
    Task<RepositoryDetailsViewModel> GetApprovedProjectDetailsAsync(string userId, int submissionId, bool institutionWide = false);
    Task<(Stream Stream, string Name, string ContentType)> DownloadAsync(string userId, int submissionId, int versionId, int fileId, bool institutionWide = false);
    Task<DepartmentRepositoryStats> GetDepartmentRepositoryStatsAsync(string userId);
}

public sealed class RepositoryService(ApplicationDbContext db, UserManager<ApplicationUser> users,
    IFileStorageService storage, ILogger<RepositoryService> logger, IOperationalSettingsService operationalSettings) : IRepositoryService
{
    private async Task<RepositoryScope> ScopeAsync(string userId, bool institutionWide = false)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Department).SingleOrDefaultAsync(u => u.Id == userId);
        if (user is null || !user.IsActive || !InstitutionalEmail.IsValid(user.Email))
            throw new ReviewOperationException(403, "An active institutional account is required.");
        var oric = await users.IsInRoleAsync(user, "ORICQEC");
        var head = await users.IsInRoleAsync(user, "DepartmentHead");
        var coordinator = await users.IsInRoleAsync(user, "Coordinator");
        var student = await users.IsInRoleAsync(user, "Student");
        if ((head || coordinator || student) && (user.DepartmentId is null || user.Department is null))
            throw new ReviewOperationException(403, "Your account requires an assigned department. Contact your administrator.");
        if (institutionWide)
        {
            if (!oric) throw new ReviewOperationException(403, "ORICQEC access is required for institution-wide reporting and export.");
            return new("Institution-Wide Academic Repository", userId, 0, true, InstitutionWide: true);
        }
        if (oric && !head && !coordinator && !student) return new("Institution-Wide Academic Repository", userId, 0, true, InstitutionWide: true);
        if (user.DepartmentId is null || user.Department is null)
            throw new ReviewOperationException(403, "Your account requires an assigned department. Contact your administrator.");
        if (head) return new(user.Department.Name + " Academic Repository", userId, user.DepartmentId.Value, true, true, user.Department.Name);
        if (coordinator) return new("Approved Repository — " + user.Department.Name, userId, user.DepartmentId.Value, true);
        if (student) return new("My Approved Projects", userId, user.DepartmentId.Value, false);
        throw new ReviewOperationException(403, "Repository access requires a Student, Coordinator, DepartmentHead or ORICQEC role.");
    }

    private IQueryable<ProjectSubmission> ApprovedScope(RepositoryScope scope) => db.ProjectSubmissions.AsNoTracking()
        .Where(s => s.Status == SubmissionStatus.Approved && (scope.InstitutionWide || (s.DepartmentId == scope.DepartmentId && (scope.DepartmentWide || s.StudentId == scope.UserId))));

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

    internal static IOrderedQueryable<SubmissionReview> OrderedReviews(IQueryable<SubmissionReview> query, RepositorySort sort) => sort switch
    {
            RepositorySort.OldestApproved => query.OrderBy(r => r.CompletedAt).ThenBy(r => r.ProjectSubmissionId),
            RepositorySort.TitleAZ => query.OrderBy(r => r.SubmissionVersion!.TitleSnapshot).ThenBy(r => r.ProjectSubmissionId),
            RepositorySort.TitleZA => query.OrderByDescending(r => r.SubmissionVersion!.TitleSnapshot).ThenByDescending(r => r.ProjectSubmissionId),
            _ => query.OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.ProjectSubmissionId)
    };
    private static IQueryable<RepositoryListItem> Items(IQueryable<SubmissionReview> query, RepositorySort sort = RepositorySort.NewestApproved)
    {
        var ordered = OrderedReviews(query, sort);
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
                logger.LogWarning(ex, "Repository resource unavailable. FileId={FileId} Result={Result}", file.Id, "Unavailable");
                return false;
            }
        }
        return true;
    }

    private IQueryable<SubmissionReview> ApplyFilters(IQueryable<SubmissionReview> query, RepositoryScope scope, RepositoryFilterViewModel filter)
    {
        if (filter.Search?.Length > 200 || filter.AcademicYear?.Length > 30 || filter.Semester?.Length > 50
            || filter.Supervisor?.Length > 150 || filter.DepartmentId < 1 || !Enum.IsDefined(filter.Sort)
            || (filter.ProjectType.HasValue && !Enum.IsDefined(filter.ProjectType.Value)))
            throw new ReviewOperationException(400, "Choose valid repository search criteria.");
        // A department parameter never overrides Student/Coordinator/Head scope.
        if (scope.InstitutionWide && filter.DepartmentId.HasValue) query = query.Where(r => r.ProjectSubmission.DepartmentId == filter.DepartmentId.Value);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var text = filter.Search.Trim();
            if (scope.ExtendedSearch)
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
            if (!scope.ExtendedSearch) throw new ReviewOperationException(400, "Supervisor filtering requires DepartmentHead or ORICQEC repository access.");
            var supervisor = filter.Supervisor.Trim();
            query = query.Where(r => r.SubmissionVersion!.SupervisorNameSnapshot != null && EF.Functions.Collate(r.SubmissionVersion.SupervisorNameSnapshot, "Latin1_General_100_CI_AS").Contains(supervisor));
        }
        if (filter.ProjectType.HasValue) query = query.Where(r => r.SubmissionVersion!.ProjectTypeSnapshot == filter.ProjectType);
        if (!string.IsNullOrWhiteSpace(filter.AcademicYear)) { var year = filter.AcademicYear.Trim(); query = query.Where(r => r.SubmissionVersion!.AcademicYearSnapshot == year); }
        if (!string.IsNullOrWhiteSpace(filter.Semester)) { var semester = filter.Semester.Trim(); query = query.Where(r => r.SubmissionVersion!.SemesterSnapshot == semester); }
        return query;
    }

    internal async Task<IQueryable<SubmissionReview>> InstitutionQueryAsync(string userId, RepositoryFilterViewModel filter)
    {
        var scope = await ScopeAsync(userId, true);
        return ApplyFilters(EligibleReviews(scope), scope, filter);
    }

    internal async Task<bool> HasInconsistentInstitutionRecordsAsync(string userId)
    {
        var scope = await ScopeAsync(userId, true);
        var eligible = EligibleReviews(scope);
        var inconsistent = await ApprovedScope(scope).AnyAsync(s => !eligible.Any(r => r.ProjectSubmissionId == s.Id));
        if (inconsistent) logger.LogWarning("Inconsistent approved institution repository records detected for authorized user {UserId}.", userId);
        return inconsistent;
    }

    internal async Task<HashSet<int>> AvailableVersionIdsAsync(int[] ids, CancellationToken cancellationToken = default)
    {
        var resources = await db.SubmissionVersionFiles.AsNoTracking().Where(f => ids.Contains(f.SubmissionVersionId))
            .Select(f => new { f.SubmissionVersionId, File = f.ProjectFile }).ToListAsync(cancellationToken);
        var available = new HashSet<int>();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = resources.Where(f => f.SubmissionVersionId == id).Select(f => f.File).ToList();
            if (files.Count > 0 && await AvailableAsync(files)) available.Add(id);
        }
        return available;
    }

    public async Task<RepositoryListViewModel> GetApprovedProjectsAsync(string userId, RepositoryFilterViewModel filter, bool institutionWide = false)
    {
        var scope = await ScopeAsync(userId, institutionWide);
        var eligible = EligibleReviews(scope);
        var query = ApplyFilters(eligible, scope, filter);
        var years = scope.ExtendedSearch ? await eligible.Where(r => r.SubmissionVersion!.AcademicYearSnapshot != null && r.SubmissionVersion.AcademicYearSnapshot != "")
            .Select(r => r.SubmissionVersion!.AcademicYearSnapshot!).Distinct().OrderByDescending(y => y).Take(100).ToListAsync() : null;
        var departments = scope.InstitutionWide ? await db.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Name)
            .Select(d => new RepositoryDepartmentOption(d.Id, d.Name)).ToListAsync() : null;
        var inconsistent = await ApprovedScope(scope).AnyAsync(s => !eligible.Any(r => r.ProjectSubmissionId == s.Id));
        if (inconsistent) logger.LogWarning("Inconsistent approved repository records detected in authorized scope for user {UserId}.", userId);
        var configuredPageSize = PageRequest.NormalizeSize((await operationalSettings.GetAsync()).RepositoryPageSize);
        var results = await Items(query, filter.Sort).ToPagedResultAsync(filter.Page, filter.PageSize, configuredPageSize);
        filter.Page = results.PageNumber;
        filter.PageSize = results.PageSize;
        var role = scope.InstitutionWide ? "ORICQEC" : scope.DepartmentHead ? "DepartmentHead" : scope.DepartmentWide ? "Coordinator" : "Student";
        logger.LogInformation("Repository access. Action={Action} UserId={UserId} Role={Role} DepartmentId={DepartmentId} Result={Result} MatchingRecords={MatchingRecords}",
            "RepositoryList", userId, role, scope.InstitutionWide ? null : scope.DepartmentId, "Success", results.TotalCount);
        // List pages only query approved-version metadata; file metadata and storage are loaded on details/download.
        return new(scope, filter, results, inconsistent, years, departments);
    }

    private async Task<SubmissionReview> ResolveAsync(string userId, int submissionId, bool institutionWide = false)
    {
        var scope = await ScopeAsync(userId, institutionWide);
        var review = await EligibleReviews(scope).Include(r => r.ProjectSubmission.Student)
            .Include(r => r.Reviewer).Include(r => r.SubmissionVersion!.Department).SingleOrDefaultAsync(r => r.ProjectSubmissionId == submissionId);
        if (review is null)
        {
            logger.LogWarning("Repository resource access unavailable. Action={Action} UserId={UserId} Role={Role} SubmissionId={SubmissionId} Result={Result}",
                "OpenRepositoryEntry", userId, scope.InstitutionWide ? "ORICQEC" : scope.DepartmentHead ? "DepartmentHead" : scope.DepartmentWide ? "Coordinator" : "Student",
                submissionId, "NotFoundOrOutsideScope");
            throw new ReviewOperationException(404, "Approved repository entry not found or unavailable.");
        }
        return review;
    }
    private Task<List<ProjectFile>> ResourcesAsync(int versionId) => db.SubmissionVersionFiles.AsNoTracking()
        .Where(f => f.SubmissionVersionId == versionId).OrderBy(f => f.ProjectFile.UploadedAt).ThenBy(f => f.ProjectFileId).Select(f => f.ProjectFile).ToListAsync();

    public async Task<RepositoryDetailsViewModel> GetApprovedProjectDetailsAsync(string userId, int submissionId, bool institutionWide = false)
    {
        var review = await ResolveAsync(userId, submissionId, institutionWide);
        var v = review.SubmissionVersion!;
        var files = await ResourcesAsync(v.Id);
        if (!await AvailableAsync(files)) throw new ReviewOperationException(404, "Approved repository entry is currently unavailable. Contact your administrator.");
        return new(submissionId, v.Id, v.VersionNumber, review.ReviewRound, review.ProjectSubmission.Student.FullName,
            review.ProjectSubmission.Student.StudentNumber, v.Department.Name, v.TitleSnapshot, v.AbstractSnapshot, v.KeywordsSnapshot,
            v.ProjectTypeSnapshot, v.SupervisorNameSnapshot, v.CourseNameSnapshot, v.CourseCodeSnapshot, v.AcademicYearSnapshot, v.SemesterSnapshot,
            v.SubmittedAt, review.CompletedAt!.Value, review.Reviewer.FullName, review.Comments,
            files.Select(f => new ProjectFileViewModel(f.Id, f.OriginalFileName, f.ResourceType, f.Description, f.FileSize, f.UploadedAt)).ToList(), review.ProjectSubmission.Student.Email, institutionWide);
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
        var currentYear = (await operationalSettings.GetAsync()).DefaultAcademicYear;
        var currentCount = await query.CountAsync(r => r.SubmissionVersion!.AcademicYearSnapshot == currentYear);
        var recent = await GetApprovedProjectsAsync(userId, new());
        return new(scope.DepartmentName, total, types, years, semesters, currentYear, currentCount, recent.Items.Take(5).ToList(), recent.HasUnavailableRecords);
    }
    public async Task<(Stream Stream, string Name, string ContentType)> DownloadAsync(string userId, int submissionId, int versionId, int fileId, bool institutionWide = false)
    {
        var scope = await ScopeAsync(userId, institutionWide);
        var review = await ResolveAsync(userId, submissionId, institutionWide);
        if (review.SubmissionVersionId != versionId) throw new ReviewOperationException(404, "Approved resource not found.");
        var files = await ResourcesAsync(versionId);
        var file = files.SingleOrDefault(f => f.Id == fileId);
        if (file is null) throw new ReviewOperationException(404, "Approved resource not found.");
        if (!await AvailableAsync(files)) throw new ReviewOperationException(404, "Approved repository resources are currently unavailable.");
        try
        {
            var stream = await storage.OpenReadAsync(file.StoredFileName);
            var role = scope.InstitutionWide ? "ORICQEC" : scope.DepartmentHead ? "DepartmentHead" : scope.DepartmentWide ? "Coordinator" : "Student";
            logger.LogInformation("Secure file access. Action={Action} UserId={UserId} Role={Role} DepartmentId={DepartmentId} SubmissionId={SubmissionId} VersionId={VersionId} FileId={FileId} Result={Result}",
                "RepositoryFileDownload", userId, role, scope.InstitutionWide ? null : scope.DepartmentId, submissionId, versionId, fileId, "Success");
            return (stream, file.OriginalFileName, file.ContentType);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Repository resource unavailable at download. FileId={FileId} Result={Result}", fileId, "Unavailable");
            throw new ReviewOperationException(404, "Approved resource is currently unavailable.");
        }
    }
}
