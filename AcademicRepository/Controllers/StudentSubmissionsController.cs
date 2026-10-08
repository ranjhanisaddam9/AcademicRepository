using AcademicRepository.Data;
using AcademicRepository.Models;
using AcademicRepository.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AcademicRepository.Controllers;

[Authorize(Roles = "Student")]
[Route("Student/Submissions")]
public class StudentSubmissionsController(ApplicationDbContext db, UserManager<ApplicationUser> users,
    StudentSubmissionService submissions, IDataProtectionProvider protection, ILogger<StudentSubmissionsController> logger,
    StudentDepartmentService departments, SubmissionLock submissionLock, SubmissionWorkflowService workflow) : Controller
{
    private readonly IDataProtector editProtector = protection.CreateProtector("AcademicRepository.SubmissionEdits.v1");
    private string StudentId => users.GetUserId(User) ?? "";

    [HttpGet("")]
    [EnableRateLimiting("search")]
    public async Task<IActionResult> Index([FromQuery] StudentSubmissionFilterViewModel filter)
    {
        if (filter.Search?.Length > 200 || (filter.Status.HasValue && !Enum.IsDefined(filter.Status.Value))) return BadRequest("Choose valid submission filters.");
        filter.Search = filter.Search?.Trim();
        filter.Sort = new[] { "Title", "Status", "Type", "CreatedAt" }.FirstOrDefault(s => string.Equals(s, filter.Sort, StringComparison.OrdinalIgnoreCase)) ?? "CreatedAt";
        filter.Direction = string.Equals(filter.Direction, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        var results = await submissions.ListPageAsync(StudentId, filter);
        filter.Page = results.PageNumber; filter.PageSize = results.PageSize;
        return View(new StudentSubmissionListViewModel(results, filter));
    }

    [HttpGet("Create")]
    public async Task<IActionResult> Create()
    {
        var student = await users.GetUserAsync(User);
        return await CanCreateAsync(student) ? View("Create", new CreateSubmissionViewModel { DepartmentName = await DepartmentNameAsync(student!) }) : Unavailable();
    }

    [HttpPost("Create")]
    public async Task<IActionResult> Create(CreateSubmissionViewModel model, string? intent)
    {
        var student = await users.GetUserAsync(User);
        if (!await CanCreateAsync(student)) return Unavailable();
        model.DepartmentName = await DepartmentNameAsync(student!);
        ValidateSubmission(model, intent);
        if (intent == "Submit") ModelState.AddModelError("", "Save a draft and upload at least one resource file before submitting your project.");
        if (!ModelState.IsValid) return View("Create", model);
        var now = DateTime.UtcNow;
        var submission = new ProjectSubmission
        {
            StudentId = student!.Id,
            DepartmentId = student.DepartmentId!.Value,
            CreatedAt = now
        };
        ApplyFields(submission, model);
        db.ProjectSubmissions.Add(submission);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException exception)
        {
            logger.LogWarning(exception, "Unable to create submission for student {StudentId}.", student.Id);
            ModelState.AddModelError("", "Unable to save your submission. Your profile may have changed. Refresh and try again.");
            return View("Create", model);
        }
        logger.LogInformation("Submission lifecycle event. Action={Action} UserId={UserId} DepartmentId={DepartmentId} SubmissionId={SubmissionId} Result={Result}",
            "SubmissionCreated", student.Id, student.DepartmentId, submission.Id, "DraftCreated");
        return Saved(submission);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Details(int id)
    {
        var model = await submissions.OwnedBy(StudentId).AsNoTracking().Where(s => s.Id == id)
            .Select(s => new SubmissionDetailsViewModel(s.Id, s.Title, s.ProjectType, s.Status,
                s.Student.FullName == "" ? s.Student.UserName ?? "Student" : s.Student.FullName, s.Department.Name,
                s.Abstract, s.Keywords, s.SupervisorName, s.CourseName, s.CourseCode, s.AcademicYear, s.Semester,
                s.CreatedAt, s.UpdatedAt, s.SubmittedAt)).SingleOrDefaultAsync();
        if (model is null) return NotFoundAccess(id, "SubmissionDetails");
        if (model.Status == SubmissionStatus.Rejected) ViewData["CanStartRevision"] = await workflow.CanStartRevisionAsync(StudentId, id);
        return View(model);
    }

    [HttpGet("{id:int}/Edit")]
    public async Task<IActionResult> Edit(int id)
    {
        var submission = await FindOwnedAsync(id);
        if (submission is null) return NotFoundAccess(id, "EditSubmission");
        if (!SubmissionWorkflow.CanEdit(submission.Status)) return Locked();
        var student = await users.GetUserAsync(User);
        if (!await CanCreateAsync(student)) return Unavailable();
        if (submission.Status == SubmissionStatus.Revision && submission.DepartmentId != student!.DepartmentId) return NotFoundAccess(id, "EditRevision");
        ViewData["Revision"] = submission.Status == SubmissionStatus.Revision;
        return View(new EditSubmissionViewModel
        {
            DepartmentName = await DepartmentNameAsync(student!), Title = submission.Title, Abstract = submission.Abstract, Keywords = submission.Keywords,
            ProjectType = submission.ProjectType, SupervisorName = submission.SupervisorName,
            CourseName = submission.CourseName, CourseCode = submission.CourseCode,
            AcademicYear = submission.AcademicYear, Semester = submission.Semester, EditToken = Token(submission)
        });
    }

    [HttpPost("{id:int}/Edit")]
    public async Task<IActionResult> Edit(int id, EditSubmissionViewModel model, string? intent)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var submission = await submissionLock.OwnedAsync(id, StudentId);
        if (submission is null) return NotFoundAccess(id, "UpdateSubmission");
        if (!SubmissionWorkflow.CanEdit(submission.Status)) return Locked();
        if (!ValidToken(model.EditToken, submission)) return Changed();
        var student = await users.GetUserAsync(User);
        if (!await CanCreateAsync(student)) return Unavailable();
        if (submission.Status == SubmissionStatus.Revision && submission.DepartmentId != student!.DepartmentId) return NotFoundAccess(id, "UpdateRevision");
        var revision = submission.Status == SubmissionStatus.Revision;
        ViewData["Revision"] = revision;
        if (revision)
        {
            if (model.ProjectType != submission.ProjectType) ModelState.AddModelError(nameof(model.ProjectType), "Project type cannot change after the first submission.");
            model.ProjectType = submission.ProjectType;
        }
        model.DepartmentName = await DepartmentNameAsync(student!);
        ValidateSubmission(model, intent);
        if (intent == "Submit" && !await db.ProjectFiles.AnyAsync(f => f.ProjectSubmissionId == id && f.IsActive))
            ModelState.AddModelError("", "Please upload at least one resource file before submitting your project.");
        if (!ModelState.IsValid) return View(model);
        var priorStatus = submission.Status;
        var now = DateTime.UtcNow;
        ApplyFields(submission, model);
        submission.DepartmentId = student!.DepartmentId!.Value;
        submission.UpdatedAt = now;
        if (intent == "Submit")
        {
            try { await workflow.SubmitAsync(StudentId, submission); }
            catch (ReviewOperationException ex) { ModelState.AddModelError("", ex.Message); Response.StatusCode = ex.Status; return View(model); }
        }
        try { await db.SaveChangesAsync(); await transaction.CommitAsync(); }
        catch (DbUpdateConcurrencyException) { return Changed(); }
        catch (DbUpdateException exception)
        {
            logger.LogWarning(exception, "Unable to update submission {SubmissionId}.", id);
            ModelState.AddModelError("", "We couldn't complete your request. Please try again.");
            return View(model);
        }
        if (intent == "Submit")
            logger.LogInformation("Submission lifecycle event. Action={Action} UserId={UserId} SubmissionId={SubmissionId} Result={Result}",
                priorStatus == SubmissionStatus.Revision ? "ResubmissionCompleted" : "SubmissionSubmitted", StudentId, id, "Submitted");
        return Saved(submission);
    }

    [HttpGet("{id:int}/Delete")]
    public async Task<IActionResult> Delete(int id)
    {
        var submission = await FindOwnedAsync(id);
        if (submission is null) return NotFoundAccess(id, "DeleteSubmissionForm");
        if (submission.Status != SubmissionStatus.Draft) return Locked();
        return View(new DeleteSubmissionViewModel { Title = submission.Title, EditToken = Token(submission) });
    }

    [HttpPost("{id:int}/Delete")]
    public async Task<IActionResult> Delete(int id, DeleteSubmissionViewModel model)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var submission = await submissionLock.OwnedAsync(id, StudentId);
        if (submission is null) return NotFoundAccess(id, "DeleteSubmission");
        if (submission.Status != SubmissionStatus.Draft) return Locked();
        if (!ValidToken(model.EditToken, submission)) return Changed();
        if (await db.ProjectFiles.AnyAsync(f => f.ProjectSubmissionId == id))
            return ConflictView("Delete this draft's resources before deleting the draft.");
        db.ProjectSubmissions.Remove(submission);
        try { await db.SaveChangesAsync(); await transaction.CommitAsync(); }
        catch (DbUpdateConcurrencyException) { return Changed(); }
        TempData["Status"] = "Draft deleted.";
        return RedirectToAction(nameof(Index));
    }

    private Task<ProjectSubmission?> FindOwnedAsync(int id) => submissions.OwnedBy(StudentId).SingleOrDefaultAsync(s => s.Id == id);
    [HttpPost("{id:int}/StartRevision")]
    public async Task<IActionResult> StartRevision([FromRoute] int id)
    {
        try { await workflow.StartRevisionAsync(StudentId, id); }
        catch (ReviewOperationException ex) { Response.StatusCode = ex.Status; return View("Message", ex.Message); }
        catch (System.Data.Common.DbException exception) { logger.LogError(exception, "Student revision request failed for submission {SubmissionId}.", id); Response.StatusCode = 503; return View("Message", "We couldn't complete your request. Please try again."); }
        TempData["Status"] = "Revision started. Previous submitted versions and reviews are preserved.";
        return RedirectToAction(nameof(Edit), new { id });
    }
    private async Task<bool> CanCreateAsync(ApplicationUser? student) => student is { IsActive: true, DepartmentId: not null }
        && await departments.MatchesAsync(student);
    private async Task<string> DepartmentNameAsync(ApplicationUser student) => await db.Departments.Where(d => d.Id == student.DepartmentId).Select(d => d.Name).SingleAsync();
    private IActionResult Unavailable()
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View("Message", "An active student account with an assigned department is required to create submissions. Contact your administrator.");
    }
    private IActionResult Locked() => ConflictView("Only Draft or Revision submissions can be edited; only initial drafts can be deleted.");
    private IActionResult Changed() => ConflictView("This draft changed or was deleted while the page was open. Return to My Submissions and reload before continuing.");
    private IActionResult NotFoundAccess(int id, string action)
    {
        logger.LogWarning("Student resource access unavailable. Action={Action} UserId={UserId} SubmissionId={SubmissionId} Result={Result}", action, StudentId, id, "NotFound");
        return NotFound();
    }
    private IActionResult ConflictView(string message)
    {
        Response.StatusCode = StatusCodes.Status409Conflict;
        return View("Message", message);
    }

    private IActionResult Saved(ProjectSubmission submission)
    {
        TempData["Status"] = submission.Status == SubmissionStatus.Submitted
            ? "Your submission has been submitted successfully." : submission.Status == SubmissionStatus.Revision ? "Revision saved." : "Draft saved.";
        return RedirectToAction(nameof(Details), new { id = submission.Id });
    }

    private void ValidateSubmission(CreateSubmissionViewModel model, string? intent)
    {
        model.Title = (model.Title ?? "").Trim();
        if (model.Title.Length == 0) ModelState.AddModelError(nameof(model.Title), "Title is required, including for drafts.");
        if (model.ProjectType is null || !Enum.IsDefined(model.ProjectType.Value))
            ModelState.AddModelError(nameof(model.ProjectType), "Select a valid project type.");
        if (intent is not ("SaveDraft" or "Submit")) ModelState.AddModelError("", "Choose Save Draft or Submit.");
        if (intent == "Submit")
        {
            if (string.IsNullOrWhiteSpace(model.Abstract)) ModelState.AddModelError(nameof(model.Abstract), "Abstract is required before submitting.");
            if (string.IsNullOrWhiteSpace(model.Keywords)) ModelState.AddModelError(nameof(model.Keywords), "Keywords are required before submitting.");
        }
    }

    private static void ApplyFields(ProjectSubmission submission, CreateSubmissionViewModel model)
    {
        submission.Title = model.Title;
        submission.ProjectType = model.ProjectType!.Value;
        submission.Abstract = model.Abstract?.Trim() ?? "";
        submission.Keywords = model.Keywords?.Trim() ?? "";
        submission.SupervisorName = Optional(model.SupervisorName);
        submission.CourseName = Optional(model.CourseName);
        submission.CourseCode = Optional(model.CourseCode);
        submission.AcademicYear = Optional(model.AcademicYear);
        submission.Semester = Optional(model.Semester);
    }
    private static string? Optional(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    private static string TokenValue(ProjectSubmission submission) => $"{submission.StudentId}\n{submission.Id}\n{Convert.ToBase64String(submission.RowVersion)}";
    private string Token(ProjectSubmission submission) => editProtector.Protect(TokenValue(submission));
    private bool ValidToken(string? token, ProjectSubmission submission)
    {
        if (string.IsNullOrEmpty(token)) return false;
        try { return editProtector.Unprotect(token) == TokenValue(submission); }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }
}
