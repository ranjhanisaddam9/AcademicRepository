using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AcademicRepository.Services;

public sealed class SubmissionVersionService(ApplicationDbContext db, UserManager<ApplicationUser> users, IFileStorageService storage)
{
    private async Task<ProjectSubmission> AuthorizeAsync(string userId, int submissionId)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && u.IsActive);
        if (user is null || user.DepartmentId is null || !InstitutionalEmail.IsValid(user.Email)) throw new ReviewOperationException(404, "Submission not found.");
        var query = db.ProjectSubmissions.AsNoTracking().Where(s => s.Id == submissionId && s.DepartmentId == user.DepartmentId);
        if (await users.IsInRoleAsync(user, "Coordinator")) query = query.Where(s => s.Status != SubmissionStatus.Draft);
        else if (await users.IsInRoleAsync(user, "Student")) query = query.Where(s => s.StudentId == userId);
        else throw new ReviewOperationException(403, "Student or Coordinator access is required.");
        return await query.SingleOrDefaultAsync() ?? throw new ReviewOperationException(404, "Submission not found.");
    }
    public async Task<VersionHistoryViewModel> ListAsync(string userId, int submissionId)
    {
        await AuthorizeAsync(userId, submissionId);
        return new(submissionId, await db.SubmissionVersions.AsNoTracking().Where(v => v.ProjectSubmissionId == submissionId)
            .OrderBy(v => v.VersionNumber).Select(v => new VersionSummary(v.Id, v.VersionNumber, v.SubmittedAt, v.TitleSnapshot)).ToListAsync());
    }
    private async Task<SubmissionVersion> VersionAsync(string userId, int id)
    {
        var parent = await db.SubmissionVersions.Where(v => v.Id == id).Select(v => (int?)v.ProjectSubmissionId).SingleOrDefaultAsync();
        if (parent is null) throw new ReviewOperationException(404, "Version not found.");
        await AuthorizeAsync(userId, parent.Value);
        return await db.SubmissionVersions.AsNoTracking().Include(v => v.Department).Include(v => v.ProjectSubmission.Student).SingleAsync(v => v.Id == id);
    }
    public async Task<VersionDetailsViewModel> DetailsAsync(string userId, int id)
    {
        var v = await VersionAsync(userId, id);
        var project = new SubmissionDetailsViewModel(v.ProjectSubmissionId, v.TitleSnapshot, v.ProjectTypeSnapshot, SubmissionStatus.Submitted,
            v.ProjectSubmission.Student.FullName, v.Department.Name, v.AbstractSnapshot, v.KeywordsSnapshot,
            v.SupervisorNameSnapshot, v.CourseNameSnapshot, v.CourseCodeSnapshot, v.AcademicYearSnapshot, v.SemesterSnapshot,
            v.CreatedAt, null, v.SubmittedAt);
        var files = await db.SubmissionVersionFiles.AsNoTracking().Where(f => f.SubmissionVersionId == id).OrderBy(f => f.ProjectFile.UploadedAt).ThenBy(f => f.ProjectFileId)
            .Select(f => new ProjectFileViewModel(f.ProjectFile.Id, f.ProjectFile.OriginalFileName, f.ProjectFile.ResourceType, f.ProjectFile.Description, f.ProjectFile.FileSize, f.ProjectFile.UploadedAt)).ToListAsync();
        var reviews = await db.SubmissionReviews.AsNoTracking().Where(r => r.SubmissionVersionId == id)
            .Select(r => new ReviewHistoryItem(r.ReviewRound, r.Reviewer.FullName, r.Decision, r.Comments, r.StartedAt, r.CompletedAt, r.SubmissionVersionId, v.SubmittedAt)).ToListAsync();
        return new(id, v.VersionNumber, project, files, reviews);
    }
    public async Task<(Stream Stream, string Name, string ContentType)> DownloadAsync(string userId, int versionId, int fileId)
    {
        await VersionAsync(userId, versionId);
        var file = await db.SubmissionVersionFiles.Where(f => f.SubmissionVersionId == versionId && f.ProjectFileId == fileId).Select(f => f.ProjectFile).SingleOrDefaultAsync();
        if (file is null) throw new ReviewOperationException(404, "Version resource not found.");
        try { return (await storage.OpenReadAsync(file.StoredFileName), file.OriginalFileName, file.ContentType); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ReviewOperationException(404, "Historical resource is unavailable. Contact your administrator."); }
    }
}
