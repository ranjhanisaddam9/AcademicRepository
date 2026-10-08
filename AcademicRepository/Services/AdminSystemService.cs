using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Data.Common;

namespace AcademicRepository.Services;

public interface IAdminSystemService
{
    Task<AdminDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<AdminSystemInformationViewModel> GetSystemInformationAsync(CancellationToken cancellationToken = default);
    Task<DataConsistencyViewModel> GetDiagnosticsAsync(CancellationToken cancellationToken = default);
}

public sealed class AdminSystemService(ApplicationDbContext db,
    IFileStorageService storage, IOptions<FileStorageOptions> storageOptions, IConfiguration configuration,
    IOptions<AuthenticationCodeOptions> authenticationOptions, IWebHostEnvironment environment, IOperationalSettingsService operationalSettings, TimeProvider clock,
    ILogger<AdminSystemService> logger) : IAdminSystemService
{
    public async Task<AdminDashboardViewModel> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        try
        {
        var settings = await operationalSettings.GetAsync(cancellationToken);
        var statusCounts = await db.ProjectSubmissions.AsNoTracking().GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        int Count(SubmissionStatus status) => statusCounts.SingleOrDefault(s => s.Status == status)?.Count ?? 0;
        var activeDepartmentCount = await db.Departments.CountAsync(d => d.IsActive, cancellationToken);
        var activeStudentCount = await (from u in db.Users.AsNoTracking()
                                        join ur in db.UserRoles on u.Id equals ur.UserId
                                        join r in db.Roles on ur.RoleId equals r.Id
                                        where u.IsActive && r.Name == "Student" select u.Id).Distinct().CountAsync(cancellationToken);
        var activeStaffCount = await (from u in db.Users.AsNoTracking()
                                      join ur in db.UserRoles on u.Id equals ur.UserId
                                      join r in db.Roles on ur.RoleId equals r.Id
                                      where u.IsActive && r.Name != "Student" select u.Id).Distinct().CountAsync(cancellationToken);
        var approved = statusCounts.SingleOrDefault(s => s.Status == SubmissionStatus.Approved)?.Count ?? 0;
        var overview = new List<AdminCount>
        {
            new("Active Departments", activeDepartmentCount), new("Active Students", activeStudentCount), new("Active Staff", activeStaffCount),
            new("Draft Submissions", Count(SubmissionStatus.Draft)), new("Submitted", Count(SubmissionStatus.Submitted)),
            new("Under Review", Count(SubmissionStatus.UnderReview)), new("Rejected", Count(SubmissionStatus.Rejected)),
            new("Approved", approved), new("Revision", Count(SubmissionStatus.Revision)), new("Repository Projects", approved)
        };
        var issues = await GetConsistencyIssuesAsync(cancellationToken);
        var warnings = new List<AdminCount>
        {
            new("Approved records missing valid review/version", await MissingApprovedHistoryAsync(cancellationToken)),
            new("Missing physical files", issues.Count(i => i.Category == "Storage")),
            new("Staff without required department", await MissingStaffDepartmentsAsync(cancellationToken)),
            new("Student identity/department mapping issues", issues.Count(i => i.Category == "Student identity")),
            new("Other data consistency issues", issues.Count(i => i.Category is not "Storage" and not "Student identity" and not "Review history"))
        };
        return new(overview, warnings, await DatabaseHealthyAsync(cancellationToken), await StorageHealthyAsync(), GraphConfigured(),
            environment.EnvironmentName, typeof(Program).Assembly.GetName().Version?.ToString() ?? "Unknown", clock.GetUtcNow().UtcDateTime);
        }
        catch (DbException ex)
        {
            logger.LogWarning(ex, "Admin system operation failed. Action={Action} Result={Result}", "LoadDashboard", "DatabaseUnavailable");
            return new([], [new("Database unavailable; operational counts could not be loaded", 1)], false,
                await StorageHealthyAsync(), GraphConfigured(), environment.EnvironmentName,
                typeof(Program).Assembly.GetName().Version?.ToString() ?? "Unknown", clock.GetUtcNow().UtcDateTime);
        }
    }

    public async Task<AdminSystemInformationViewModel> GetSystemInformationAsync(CancellationToken cancellationToken = default)
    {
        OperationalSettingsViewModel settings;
        try { settings = await operationalSettings.GetAsync(cancellationToken); }
        catch (DbException ex)
        {
            logger.LogWarning(ex, "Admin system operation failed. Action={Action} Result={Result}", "LoadOperationalSettings", "UsingDeploymentDefaults");
            settings = DeploymentDefaults();
        }
        var email = configuration.GetSection("Email").Get<EmailOptions>();
        return new(environment.EnvironmentName, typeof(Program).Assembly.GetName().Version?.ToString() ?? "Unknown",
            clock.GetUtcNow().UtcDateTime, await DatabaseHealthyAsync(cancellationToken), await StorageHealthyAsync(), GraphConfigured(),
            email is { DeliveryMode: "MicrosoftGraph" } ? email.From : null, settings.MaxFileSizeMB, settings.MaxFilesPerSubmission,
            settings.OtpExpiryMinutes, settings.OtpMaxAttempts, settings.OtpResendCooldownSeconds, settings.DefaultAcademicYear);
    }

    private OperationalSettingsViewModel DeploymentDefaults()
    {
        var now = clock.GetUtcNow();
        var yearStart = now.Month >= 7 ? now.Year : now.Year - 1;
        return new OperationalSettingsViewModel
        {
            MaxFileSizeMB = storageOptions.Value.MaxFileSizeMB,
            MaxFilesPerSubmission = storageOptions.Value.MaxFilesPerSubmission,
            OtpExpiryMinutes = authenticationOptions.Value.ExpiryMinutes,
            OtpMaxAttempts = authenticationOptions.Value.MaxAttempts,
            OtpResendCooldownSeconds = authenticationOptions.Value.ResendCooldownSeconds,
            DefaultAcademicYear = configuration["Repository:CurrentAcademicYear"] ?? $"{yearStart}-{yearStart + 1}"
        };
    }

    public async Task<DataConsistencyViewModel> GetDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        new(clock.GetUtcNow().UtcDateTime, await CountOrphansAsync(cancellationToken), await GetConsistencyIssuesAsync(cancellationToken));

    private async Task<IReadOnlyList<DataDiagnosticIssue>> GetConsistencyIssuesAsync(CancellationToken cancellationToken)
    {
        var issues = new List<DataDiagnosticIssue>();
        var existingRoles = await db.Roles.AsNoTracking().Select(r => r.Name).ToListAsync(cancellationToken);
        issues.AddRange(IdentitySeeder.Roles.Where(required => !existingRoles.Contains(required, StringComparer.OrdinalIgnoreCase))
            .Select(role => new DataDiagnosticIssue("Role configuration", role, "Expected application role is missing.")));
        var incompleteDepartments = await db.Departments.AsNoTracking().Where(d => d.Name == "" || d.Code == "")
            .Select(d => new { d.Id, d.Code, d.Name }).Take(200).ToListAsync(cancellationToken);
        issues.AddRange(incompleteDepartments.Select(d => new DataDiagnosticIssue("Department", d.Id.ToString(), "Department name or code is missing.")));
        var badStaff = await (from ur in db.UserRoles.AsNoTracking()
                              join role in db.Roles on ur.RoleId equals role.Id
                              join user in db.Users.AsNoTracking() on ur.UserId equals user.Id
                              where (role.Name == "Coordinator" || role.Name == "DepartmentHead") && user.DepartmentId == null
                              select new { user.Id, user.Email, role.Name }).Take(200).ToListAsync(cancellationToken);
        issues.AddRange(badStaff.Select(x => new DataDiagnosticIssue("Staff department", x.Id, $"{x.Name} account {x.Email} has no department.")));
        const int batchSize = 200;
        var duplicateNumbers = await (from user in db.Users.AsNoTracking()
                                      join assignment in db.UserRoles.AsNoTracking() on user.Id equals assignment.UserId
                                      join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                                      where role.Name == "Student" && user.StudentNumber != null && user.StudentNumber != ""
                                      group user by user.StudentNumber into groupOfStudents
                                      where groupOfStudents.Count() > 1
                                      orderby groupOfStudents.Key
                                      select groupOfStudents.Key!).Take(200).ToListAsync(cancellationToken);
        if (duplicateNumbers.Count > 0)
        {
            var duplicateStudents = await (from user in db.Users.AsNoTracking()
                                           join assignment in db.UserRoles.AsNoTracking() on user.Id equals assignment.UserId
                                           join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                                           where role.Name == "Student" && user.StudentNumber != null && duplicateNumbers.Contains(user.StudentNumber)
                                           orderby user.Id
                                           select new { user.Id, user.StudentNumber }).Take(200).ToListAsync(cancellationToken);
            issues.AddRange(duplicateStudents.Select(student => new DataDiagnosticIssue("Student identity", student.Id,
                "Duplicate Student ID " + student.StudentNumber + ".")));
        }

        // Resolve every Student against one database-backed mapping instead of issuing one query per account.
        var departmentIdsByKeyword = await db.Departments.AsNoTracking().Where(d => d.StudentEmailKeyword != null)
            .ToDictionaryAsync(d => d.StudentEmailKeyword!, d => d.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var studentQuery = from assignment in db.UserRoles.AsNoTracking()
                           join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                           join user in db.Users.AsNoTracking() on assignment.UserId equals user.Id
                           where role.Name == "Student"
                           select new { user.Id, user.Email, user.StudentNumber, user.DepartmentId };
        string lastStudentId = "";
        while (issues.Count < 500)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var students = await studentQuery.Where(student => string.Compare(student.Id, lastStudentId) > 0)
                .OrderBy(student => student.Id).Take(batchSize).ToListAsync(cancellationToken);
            if (students.Count == 0) break;
            lastStudentId = students[^1].Id;
            foreach (var student in students)
            {
                var keyword = StudentDepartmentService.KeywordFromEmail(student.Email);
                if (student.DepartmentId is null || string.IsNullOrWhiteSpace(student.StudentNumber))
                    issues.Add(new("Student identity", student.Id, "Student is missing a required department or Student ID."));
                else if (keyword is null || !departmentIdsByKeyword.TryGetValue(keyword, out var mappedDepartmentId)
                    || mappedDepartmentId != student.DepartmentId
                    || !string.Equals(student.StudentNumber, StudentDepartmentService.StudentNumberFromEmail(student.Email), StringComparison.OrdinalIgnoreCase))
                    issues.Add(new("Student identity", student.Id, "Student email keyword, Student ID and assigned department do not agree."));
                if (issues.Count >= 500) break;
            }
        }
        var missingReviews = await db.ProjectSubmissions.AsNoTracking().Where(s => s.Status == SubmissionStatus.Approved
                && !s.Reviews.Any(r => r.Decision == ReviewDecision.Approved && r.CompletedAt != null && r.SubmissionVersionId != null
                    && r.SubmissionVersion != null && r.SubmissionVersion.ProjectSubmissionId == s.Id && r.SubmissionVersion.VersionNumber == r.ReviewRound))
            .Select(s => new { s.Id, s.Title, s.Status }).Take(200).ToListAsync(cancellationToken);
        issues.AddRange(missingReviews.Select(s => new DataDiagnosticIssue("Review history", s.Id.ToString(), $"Approved submission '{s.Title}' has no linked completed approved version.")));
        var badApprovedReviews = await db.SubmissionReviews.AsNoTracking().Where(r => r.Decision == ReviewDecision.Approved
                && (r.CompletedAt == null || r.SubmissionVersionId == null || r.SubmissionVersion == null))
            .Select(r => new { r.Id, r.ProjectSubmissionId }).Take(200).ToListAsync(cancellationToken);
        issues.AddRange(badApprovedReviews.Select(r => new DataDiagnosticIssue("Review history", r.Id.ToString(), $"Approved review for submission {r.ProjectSubmissionId} has no linked version.")));
        var missingSnapshots = await db.SubmissionVersions.AsNoTracking().Where(v => !v.Files.Any())
            .Select(v => new { v.Id, v.ProjectSubmissionId, v.TitleSnapshot }).Take(200).ToListAsync(cancellationToken);
        issues.AddRange(missingSnapshots.Select(v => new DataDiagnosticIssue("Version snapshot", v.Id.ToString(), $"Version {v.Id} for submission {v.ProjectSubmissionId} ('{v.TitleSnapshot}') has no resource snapshot.")));
        var malformedFiles = await db.ProjectFiles.AsNoTracking().Where(f => f.FileSize <= 0 || f.StoredFileName == "" || f.OriginalFileName == "")
            .Select(f => new { f.Id, f.ProjectSubmissionId }).Take(200).ToListAsync(cancellationToken);
        issues.AddRange(malformedFiles.Select(f => new DataDiagnosticIssue("File metadata", f.Id.ToString(), $"Project file for submission {f.ProjectSubmissionId} has missing or invalid metadata.")));
        var lastFileId = 0;
        while (issues.Count < 500)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var activeFiles = await db.ProjectFiles.AsNoTracking().Where(f => f.IsActive && f.Id > lastFileId)
                .OrderBy(f => f.Id).Select(f => new { f.Id, f.ProjectSubmissionId, f.OriginalFileName, f.StoredFileName, f.ProjectSubmission.Title, f.ProjectSubmission.Status })
                .Take(batchSize).ToListAsync(cancellationToken);
            if (activeFiles.Count == 0) break;
            lastFileId = activeFiles[^1].Id;
            foreach (var file in activeFiles)
            {
                try { await using var stream = await storage.OpenReadAsync(file.StoredFileName, cancellationToken); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    issues.Add(new("Storage", file.Id.ToString(), $"Project '{file.Title}' (submission {file.ProjectSubmissionId}), file '{file.OriginalFileName}' is unavailable; submission status: {file.Status}."));
                    logger.LogWarning(ex, "Admin diagnostic found unavailable file. Action={Action} FileId={FileId} SubmissionId={SubmissionId} Result={Result}",
                        "CheckStoredFile", file.Id, file.ProjectSubmissionId, "Unavailable");
                }
                if (issues.Count >= 500) break;
            }
        }
        return issues;
    }

    private Task<int> MissingApprovedHistoryAsync(CancellationToken cancellationToken) => db.ProjectSubmissions.AsNoTracking()
        .Where(s => s.Status == SubmissionStatus.Approved && !s.Reviews.Any(r => r.Decision == ReviewDecision.Approved
            && r.CompletedAt != null && r.CompletedAt >= r.StartedAt && r.SubmissionVersionId != null && r.SubmissionVersion != null
            && r.SubmissionVersion.ProjectSubmissionId == s.Id && r.SubmissionVersion.VersionNumber == r.ReviewRound
            && r.SubmissionVersion.Files.Any() && r.SubmissionVersion.TitleSnapshot != "" && r.SubmissionVersion.AbstractSnapshot != "" && r.SubmissionVersion.KeywordsSnapshot != ""))
        .CountAsync(cancellationToken);

    private Task<int> MissingStaffDepartmentsAsync(CancellationToken cancellationToken) => (from ur in db.UserRoles.AsNoTracking()
        join role in db.Roles on ur.RoleId equals role.Id join user in db.Users.AsNoTracking() on ur.UserId equals user.Id
        where user.IsActive && (role.Name == "Coordinator" || role.Name == "DepartmentHead") && user.DepartmentId == null select user.Id).Distinct().CountAsync(cancellationToken);

    private async Task<int> CountOrphansAsync(CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(storageOptions.Value.RootPath, environment.ContentRootPath);
        if (!Directory.Exists(root)) return 0;
        var known = await db.ProjectFiles.AsNoTracking().Select(f => f.StoredFileName).ToHashSetAsync(cancellationToken);
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!known.Contains(Path.GetFileName(path))) count++;
        }
        return count;
    }

    private async Task<bool> DatabaseHealthyAsync(CancellationToken cancellationToken)
    {
        try { return await db.Database.CanConnectAsync(cancellationToken); }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        { logger.LogWarning(ex, "Admin system operation failed. Action={Action} Result={Result}", "CheckDatabaseHealth", "Unavailable"); return false; }
    }

    private async Task<bool> StorageHealthyAsync()
    {
        var root = Path.GetFullPath(storageOptions.Value.RootPath, environment.ContentRootPath);
        var probe = Path.Combine(root, ".health-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                await stream.WriteAsync(new byte[] { 1 });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        { logger.LogWarning(ex, "Admin system operation failed. Action={Action} Result={Result}", "CheckStorageHealth", "Unavailable"); return false; }
        finally { try { if (File.Exists(probe)) File.Delete(probe); } catch (IOException) { } }
    }

    private bool GraphConfigured()
    {
        var email = configuration.GetSection("Email").Get<EmailOptions>();
        return email is { DeliveryMode: "MicrosoftGraph" } && email.HasValidGraphConfiguration();
    }
}
