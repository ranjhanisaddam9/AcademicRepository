using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AcademicRepository.Services;

public sealed class ResourceOperationException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class ProjectFileService(ApplicationDbContext db, IFileStorageService storage, SubmissionLock submissionLock,
    IOperationalSettingsService operationalSettings, ILogger<ProjectFileService> logger)
{
    private async Task<ProjectSubmission> AuthorizedAsync(int submissionId, string studentId, bool locked = false)
    {
        var submission = locked ? await submissionLock.OwnedAsync(submissionId, studentId)
            : await db.ProjectSubmissions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == submissionId && s.StudentId == studentId);
        var department = await db.Users.Where(u => u.Id == studentId && u.IsActive).Select(u => u.DepartmentId).SingleOrDefaultAsync();
        if (submission is null || department is null || submission.DepartmentId != department)
        {
            logger.LogWarning("Cross-user or unavailable submission access. Action={Action} UserId={UserId} SubmissionId={SubmissionId} Result={Result}",
                "AccessSubmissionResource", studentId, submissionId, "NotFoundOrNotOwned");
            throw new ResourceOperationException(404, "Submission or resource not found.");
        }
        return submission;
    }
    private static void RequireDraft(ProjectSubmission submission)
    {
        if (!SubmissionWorkflow.CanEdit(submission.Status)) throw new ResourceOperationException(409, "Resources can only be changed in Draft or Revision.");
    }
    public async Task<ResourceListViewModel> ListAsync(int submissionId, string studentId)
    {
        var settings = await operationalSettings.GetAsync();
        var submission = await AuthorizedAsync(submissionId, studentId);
        var files = await db.ProjectFiles.Where(f => f.ProjectSubmissionId == submissionId && f.IsActive).OrderBy(f => f.UploadedAt).ThenBy(f => f.Id)
            .Select(f => new ProjectFileViewModel(f.Id, f.OriginalFileName, f.ResourceType, f.Description, f.FileSize, f.UploadedAt)).ToListAsync();
        return new(submissionId, SubmissionWorkflow.CanEdit(submission.Status), files, settings.MaxFileSizeMB, settings.MaxFilesPerSubmission);
    }
    public async Task UploadAsync(int submissionId, string studentId, UploadResourceViewModel model)
    {
        var settings = await operationalSettings.GetAsync();
        var file = model.File;
        if (file is null || file.Length <= 0) throw new ResourceOperationException(400, "Choose a non-empty file.");
        if (file.Length > settings.MaxFileSizeMB * 1024L * 1024) throw new ResourceOperationException(400, $"Files must be no larger than {settings.MaxFileSizeMB} MB.");
        if (model.ResourceType is null || !Enum.IsDefined(model.ResourceType.Value) || model.Description?.Length > 1000)
            throw new ResourceOperationException(400, "Select a valid resource type and a description of at most 1000 characters.");
        var name = ResourceFileValidator.SafeOriginalName(file.FileName);
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (name.Length is 0 or > 255 || name.Any(char.IsControl) || name.Contains(':') || !ResourceFileValidator.ContentTypes.TryGetValue(extension, out var contentType))
            throw new ResourceOperationException(400, "This filename or file type is not allowed.");

        await using var transaction = await db.Database.BeginTransactionAsync();
        var submission = await AuthorizedAsync(submissionId, studentId, locked: true);
        RequireDraft(submission);
        if (await db.ProjectFiles.CountAsync(f => f.ProjectSubmissionId == submissionId && f.IsActive) >= settings.MaxFilesPerSubmission)
            throw new ResourceOperationException(400, $"A submission can contain at most {settings.MaxFilesPerSubmission} files.");
        string? key = null;
        var commitStarted = false;
        try
        {
            await using var input = file.OpenReadStream();
            if (!await ResourceFileValidator.MatchesAsync(input, extension))
                throw new ResourceOperationException(400, "The file content does not match its supported format.");
            input.Position = 0;
            key = await storage.StoreAsync(input, extension);
            db.ProjectFiles.Add(new ProjectFile
            {
                ProjectSubmissionId = submissionId, OriginalFileName = name, StoredFileName = key,
                ContentType = contentType, FileExtension = extension, FileSize = file.Length,
                ResourceType = model.ResourceType.Value, Description = string.IsNullOrWhiteSpace(model.Description) ? null : model.Description.Trim(),
                UploadedAt = DateTime.UtcNow, UploadedByUserId = studentId
            });
            submission.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            commitStarted = true;
            await transaction.CommitAsync();
            logger.LogInformation("Secure resource lifecycle event. Action={Action} UserId={UserId} SubmissionId={SubmissionId} Result={Result}",
                "FileUploaded", studentId, submissionId, "Success");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbUpdateException or System.Data.Common.DbException)
        {
            try { await transaction.RollbackAsync(); }
            catch (System.Data.Common.DbException rollbackException) { logger.LogError(rollbackException, "Upload rollback could not be confirmed for submission {SubmissionId}.", submissionId); }
            if (key is not null && !commitStarted)
            {
                try { await storage.DeleteAsync(key); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                { logger.LogError(cleanup, "File operation failed. Action={Action} SubmissionId={SubmissionId} Result={Result}", "UploadCleanup", submissionId, "OrphanCleanupFailed"); }
            }
            if (commitStarted) logger.LogError("File operation outcome uncertain. Action={Action} SubmissionId={SubmissionId} Result={Result}", "UploadCommit", submissionId, "ReconciliationRequired");
            logger.LogError(ex, "File operation failed. Action={Action} SubmissionId={SubmissionId} Result={Result}", "Upload", submissionId, "Failed");
            throw new ResourceOperationException(503, "We couldn't complete your request. Please try again.");
        }
    }
    private async Task<ProjectFile> FileAsync(int id, string studentId, bool locked = false)
    {
        // Scope metadata before acquiring/opening any physical file.
        var file = await db.ProjectFiles.SingleOrDefaultAsync(f => f.Id == id && f.ProjectSubmission.StudentId == studentId);
        if (file is null)
        {
            logger.LogWarning("Cross-user or unavailable file access. Action={Action} UserId={UserId} FileId={FileId} Result={Result}",
                "AccessProjectFile", studentId, id, "NotFoundOrNotOwned");
            throw new ResourceOperationException(404, "Resource not found.");
        }
        await AuthorizedAsync(file.ProjectSubmissionId, studentId, locked);
        return file;
    }
    public async Task<(Stream Stream, string Name, string ContentType)> DownloadAsync(int id, string studentId)
    {
        var file = await FileAsync(id, studentId);
        if (!file.IsActive && !await db.SubmissionVersionFiles.AnyAsync(v => v.ProjectFileId == id)) throw new ResourceOperationException(404, "Resource not found.");
        try { return (await storage.OpenReadAsync(file.StoredFileName), file.OriginalFileName, file.ContentType); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "File operation failed. Action={Action} FileId={FileId} Result={Result}", "Download", id, "Unavailable");
            throw new ResourceOperationException(404, "The resource is currently unavailable. Contact your administrator.");
        }
    }
    public async Task<int> DeleteAsync(int id, string studentId)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        // Resolve parent without tracking; acquire the parent lock before tracking the child.
        var parentId = await db.ProjectFiles.Where(f => f.Id == id && f.ProjectSubmission.StudentId == studentId)
            .Select(f => (int?)f.ProjectSubmissionId).SingleOrDefaultAsync();
        if (parentId is null) throw new ResourceOperationException(404, "Resource not found.");
        var submission = await AuthorizedAsync(parentId.Value, studentId, true);
        RequireDraft(submission);
        var file = await FileAsync(id, studentId);
        if (!file.IsActive) throw new ResourceOperationException(409, "This resource has already been removed from the current submission.");
        var historical = await db.SubmissionVersionFiles.AnyAsync(v => v.ProjectFileId == id);
        try
        {
            if (historical) { file.IsActive = false; file.DeletedAt = DateTime.UtcNow; }
            else db.ProjectFiles.Remove(file);
            submission.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (Exception ex) when (ex is DbUpdateException or System.Data.Common.DbException)
        {
            logger.LogError(ex, "File operation failed. Action={Action} FileId={FileId} Result={Result}", "DeleteRecord", id, "Failed");
            throw new ResourceOperationException(503, "We couldn't complete your request. Please try again.");
        }
        if (historical) return parentId.Value;
        // DB-first removal never leaves a downloadable record pointing at deleted bytes.
        // A cleanup failure is an inaccessible orphan; key is logged for reconciliation.
        try { await storage.DeleteAsync(file.StoredFileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "File operation failed. Action={Action} FileId={FileId} Result={Result}", "DeleteStoredFile", id, "OrphanedFile");
            throw new ResourceOperationException(503, "We couldn't complete your request. Please try again.");
        }
        logger.LogInformation("Secure resource lifecycle event. Action={Action} UserId={UserId} SubmissionId={SubmissionId} FileId={FileId} Result={Result}",
            "FileDeleted", studentId, parentId, id, "Success");
        return parentId.Value;
    }
}
