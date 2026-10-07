using AcademicRepository.Data;
using AcademicRepository.Models;
using AcademicRepository.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcademicRepository.Controllers;

[Authorize(Roles = "Student")]
[Route("Student/Submissions")]
public class StudentSubmissionsController(ApplicationDbContext db, UserManager<ApplicationUser> users,
    StudentSubmissionService submissions, IDataProtectionProvider protection, ILogger<StudentSubmissionsController> logger,
    StudentDepartmentService departments, SubmissionLock submissionLock) : Controller
{
    private readonly IDataProtector editProtector = protection.CreateProtector("AcademicRepository.SubmissionEdits.v1");
    private string StudentId => users.GetUserId(User) ?? "";

    [HttpGet("")]
    public async Task<IActionResult> Index() => View(await submissions.ListAsync(StudentId));

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
        SetSubmitted(submission, intent, now);
        db.ProjectSubmissions.Add(submission);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException exception)
        {
            logger.LogWarning(exception, "Unable to create submission for student {StudentId}.", student.Id);
            ModelState.AddModelError("", "Unable to save your submission. Your profile may have changed. Refresh and try again.");
            return View("Create", model);
        }
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
        return model is null ? NotFound() : View(model);
    }

    [HttpGet("{id:int}/Edit")]
    public async Task<IActionResult> Edit(int id)
    {
        var submission = await FindOwnedAsync(id);
        if (submission is null) return NotFound();
        if (submission.Status != SubmissionStatus.Draft) return Locked();
        var student = await users.GetUserAsync(User);
        if (!await CanCreateAsync(student)) return Unavailable();
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
        if (submission is null) return NotFound();
        if (submission.Status != SubmissionStatus.Draft) return Locked();
        if (!ValidToken(model.EditToken, submission)) return Changed();
        var student = await users.GetUserAsync(User);
        if (!await CanCreateAsync(student)) return Unavailable();
        model.DepartmentName = await DepartmentNameAsync(student!);
        ValidateSubmission(model, intent);
        if (intent == "Submit" && !await db.ProjectFiles.AnyAsync(f => f.ProjectSubmissionId == id))
            ModelState.AddModelError("", "Please upload at least one resource file before submitting your project.");
        if (!ModelState.IsValid) return View(model);
        var now = DateTime.UtcNow;
        ApplyFields(submission, model);
        submission.DepartmentId = student!.DepartmentId!.Value;
        submission.UpdatedAt = now;
        SetSubmitted(submission, intent, now);
        try { await db.SaveChangesAsync(); await transaction.CommitAsync(); }
        catch (DbUpdateConcurrencyException) { return Changed(); }
        catch (DbUpdateException exception)
        {
            logger.LogWarning(exception, "Unable to update submission {SubmissionId}.", id);
            ModelState.AddModelError("", "Unable to save your submission. Refresh and try again.");
            return View(model);
        }
        return Saved(submission);
    }

    [HttpGet("{id:int}/Delete")]
    public async Task<IActionResult> Delete(int id)
    {
        var submission = await FindOwnedAsync(id);
        if (submission is null) return NotFound();
        if (submission.Status != SubmissionStatus.Draft) return Locked();
        return View(new DeleteSubmissionViewModel { Title = submission.Title, EditToken = Token(submission) });
    }

    [HttpPost("{id:int}/Delete")]
    public async Task<IActionResult> Delete(int id, DeleteSubmissionViewModel model)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var submission = await submissionLock.OwnedAsync(id, StudentId);
        if (submission is null) return NotFound();
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
    private async Task<bool> CanCreateAsync(ApplicationUser? student) => student is { IsActive: true, DepartmentId: not null }
        && await departments.MatchesAsync(student);
    private async Task<string> DepartmentNameAsync(ApplicationUser student) => await db.Departments.Where(d => d.Id == student.DepartmentId).Select(d => d.Name).SingleAsync();
    private IActionResult Unavailable()
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View("Message", "An active student account with an assigned department is required to create submissions. Contact your administrator.");
    }
    private IActionResult Locked() => ConflictView("This submission is no longer a draft. It cannot be edited or deleted.");
    private IActionResult Changed() => ConflictView("This draft changed or was deleted while the page was open. Return to My Submissions and reload before continuing.");
    private IActionResult ConflictView(string message)
    {
        Response.StatusCode = StatusCodes.Status409Conflict;
        return View("Message", message);
    }

    private IActionResult Saved(ProjectSubmission submission)
    {
        TempData["Status"] = submission.Status == SubmissionStatus.Submitted
            ? "Your submission has been submitted successfully." : "Draft saved.";
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
    private static void SetSubmitted(ProjectSubmission submission, string? intent, DateTime now)
    {
        if (intent != "Submit") return;
        submission.Status = SubmissionStatus.Submitted;
        submission.SubmittedAt = now;
        submission.UpdatedAt = now;
    }
    private static string TokenValue(ProjectSubmission submission) => $"{submission.StudentId}\n{submission.Id}\n{Convert.ToBase64String(submission.RowVersion)}";
    private string Token(ProjectSubmission submission) => editProtector.Protect(TokenValue(submission));
    private bool ValidToken(string? token, ProjectSubmission submission)
    {
        if (string.IsNullOrEmpty(token)) return false;
        try { return editProtector.Unprotect(token) == TokenValue(submission); }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }
}
