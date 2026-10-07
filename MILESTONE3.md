# Milestone 3 — Student Project Submission

Implemented on the existing MVC, EF Core SQL Server and Identity application. Only Student draft/submission functionality was added. No files, ProjectFile entity, uploads, review, approval/rejection actions, search, email or audit logs were implemented.

## Verification

- Complete solution Debug build: zero warnings and zero errors.
- All existing Milestone 1/2 integration checks and new Milestone 3 checks passed: 419 assertions, including shared setup/antiforgery assertions.
- The harness first applies the Milestone 1 migration with an existing Identity account, upgrades to Milestone 2 and creates a department/profile, then applies Milestone 3. Department/profile/password data and login are preserved.
- Applied `20261006071514_AddProjectSubmissions` to the existing configured database. Counts/checksums of Departments and all seven Identity tables match before and after. The migration only creates ProjectSubmissions, its indexes and restrictive foreign keys. No application database or existing tables/data were deleted/reset.
- The application's submission table remains empty; test submissions/users were confined to uniquely named disposable integration databases, which were removed after the checks.
- The updated Debug application started in Development against the migrated database. Login and the local Bootstrap stylesheet returned HTTP 200 over HTTPS. The temporary verification server was stopped afterward.
- New tests cover incomplete drafts, complete direct submission, editing/submitting drafts, detail/list/dashboard content, deletion confirmation, forged StudentId/DepartmentId/Status/timestamps, foreign-owner GET/POST requests, all other roles and anonymous users, missing departments, inactive sessions/accounts, required/maximum-length/enum/intent validation, CSRF, stale/tampered edit tokens, stale deletion confirmation, repeated submission and SQL rowversion edit/delete races after submission.

The running Release app (process 4632 at implementation time) locked the Release build output. Debug output was available and used for the final build, migrations and checks. Stop your existing running app/debugging session and restart to load Milestone 3. No compilation errors remain in the verified configuration.

## Commands

Run from `H:\AI\AcademicRepostory` with the .NET 10 SDK:

```powershell
# On this machine, if PATH still selects the .NET 9 SDK:
$env:DOTNET_ROOT = 'H:\AI\.dotnet'
$env:PATH = 'H:\AI\.dotnet;' + $env:PATH

dotnet tool restore
dotnet build AcademicRepository.slnx --configuration Debug
dotnet ef database update --project AcademicRepository --configuration Debug --no-build
dotnet run --project AcademicRepository.Tests --configuration Debug --no-build
dotnet run --project AcademicRepository --configuration Debug
```

The migration is checked in and already applied here. The database update command is safe to repeat and is the required command for another existing installation. Do not regenerate the migration. The creation command used was:

```powershell
dotnet ef migrations add AddProjectSubmissions --project AcademicRepository --configuration Debug --no-build
```

Connection string, development Admin credentials, package references and launch settings are unchanged. Migration history and the earlier migrations remain intact.

## Architecture and business rules

- Added ProjectType and SubmissionStatus enums, a ProjectSubmission entity in the existing context, dedicated form/list/detail/dashboard/delete view models, a Student-only controller and a small scoped service for owner-filtered lists/counts. No replacement architecture or generic repository pattern.
- Routes: `/Student/Submissions` (list), `/Student/Submissions/Create`, `/Student/Submissions/{id}` (details), and `/{id}/Edit` / `/{id}/Delete` beneath the same prefix. The existing `/Student/MySubmissions` redirects to the new list. `/Student/Dashboard` now has counts and the five most recent submissions.
- New submissions use the Identity user ID and that user's current DepartmentId from the server. Form models contain no ownership, department, status or timestamp properties. Unknown extra fields cannot change those values. All private queries and mutations require the authenticated student's ownership; foreign and missing IDs return 404.
- Only `SaveDraft` and `Submit` button intents are accepted. Both require Title and a valid ProjectType; Draft can omit Abstract/Keywords and optional metadata. Submit additionally requires a nonblank Abstract and Keywords. Limits: Title 250, Abstract 10,000, Keywords 1,000, Supervisor/Course Name 150, Course Code/Academic Year 30 and Semester 50. Optional blank values become null; draft Abstract/Keywords become empty strings.
- All new records receive UTC CreatedAt. Editing a draft records UTC UpdatedAt. Submitting sets Status to Submitted, SubmittedAt and UpdatedAt to the same UTC instant, then redirects to read-only Details with the requested success alert. Direct create-and-submit is supported. No future state transitions exist.
- Editing/deleting requires an owned Draft on every request. Submitted records cannot be edited, deleted, resubmitted or reverted via forged forms. Owned locked or stale records return HTTP 409 with a useful message and a link to reload the list. Draft deletion uses a GET confirmation page followed by antiforgery-protected POST; GET never deletes.
- SQL Server rowversion prevents a request that previously read a Draft from overwriting/deleting it after another request submits it. Opaque Data Protection tokens bind edit/delete forms to the owner, record and version, also rejecting stale tabs and tampering. No raw Identity security fields are exposed.
- An active Student account with a department is required to create. Existing Identity login/cookie checks also block inactive sessions. A submission keeps the department captured at creation even if an Admin later transfers the student; editing does not reassign historical work. This prepares the relationship without introducing future department review rules.
- As in Milestone 2, department deactivation preserves user accounts. This milestone adds no rule blocking existing users from creating work for their assigned inactive department. Students with no current department cannot create new records but may still access their existing owned work. These are explicit assumptions; no transfer/review workflow was added.
- Razor encodes submitted text; Bootstrap status badges include the future labels, but only Draft and Submitted are actively used. Student navigation adds New Submission; Coordinator and DepartmentHead placeholders and Admin management are preserved.
- My Submissions displays all owned rows. Optional pagination was omitted to keep the milestone simple; the dashboard is limited to five recent rows. All dates are labeled UTC.

## Manual checklist

1. Stop the old running app, run the update command and launch Debug. Verify existing Admin login/logout, department/user management and all role dashboard restrictions still work.
2. Through Admin management, create two active Students assigned to departments. Log in as Student A and open Student Dashboard. Confirm profile, five summary cards, My Submissions and New Submission navigation.
3. Create a title/project-type-only draft. Confirm it saves with Draft status and no submitted date. View its details and edit it while leaving Abstract/Keywords incomplete.
4. Try Submit with blank Abstract/Keywords, invalid project type, blank/oversized title or oversized metadata; confirm clear errors and no status change. Enter valid fields and Submit. Confirm success message, Submitted status, timestamps and all detail metadata.
5. Confirm submitted work has View only in the list/details. Direct requests to its Edit/Delete URLs should show a conflict and leave the record intact. Test direct create-and-submit as well.
6. Create another draft; follow Delete, cancel and confirm it remains. Reopen Delete and confirm removal through POST. A draft changed after opening its delete confirmation should require reloading before deletion.
7. Log in as Student B in another browser session. Confirm A's work is absent from B's list/dashboard and A's detail/edit/delete URLs return 404. Run the harness for forged form/POST and concurrency tests.
8. Check Student A's totals/draft/submitted counts after creating, submitting and deleting records; Approved/Rejected should remain zero. The recent table must contain only A's five newest submissions.
9. Use Admin to remove a test Student's department or deactivate the account. Confirm no-department creation is blocked; inactive users cannot create through an existing session or log in again. Reactivate/assign a department to restore eligibility.
10. Confirm Admin, Coordinator, DepartmentHead and anonymous users cannot reach Student submission endpoints. Check that uploads, review actions and repository search are absent.

## Files created

Application paths are relative to `H:\AI\AcademicRepostory\AcademicRepository`:

- `Models/ProjectSubmission.cs` (entity and both enums)
- `Models/SubmissionViewModels.cs`
- `Services/StudentSubmissionService.cs`
- `Controllers/StudentSubmissionsController.cs`
- `Views/Student/Dashboard.cshtml`
- `Views/StudentSubmissions/_Fields.cshtml`
- `Views/StudentSubmissions/Create.cshtml`
- `Views/StudentSubmissions/Edit.cshtml`
- `Views/StudentSubmissions/Index.cshtml`
- `Views/StudentSubmissions/Details.cshtml`
- `Views/StudentSubmissions/Delete.cshtml`
- `Views/StudentSubmissions/Message.cshtml`
- `Views/Shared/_SubmissionStatus.cshtml`
- `Views/Shared/_SubmissionTable.cshtml`
- `Migrations/20261006071514_AddProjectSubmissions.cs`
- `Migrations/20261006071514_AddProjectSubmissions.Designer.cs`

Repository-level files:

- `AcademicRepository.Tests/Milestone3Checks.cs`
- `MILESTONE3.md`
- `milestone3-checks.log` (generated, ignored verification output)

## Files modified

- `AcademicRepository/Data/ApplicationDbContext.cs`
- `AcademicRepository/Program.cs`
- `AcademicRepository/Controllers/RoleControllers.cs`
- `AcademicRepository/Views/Shared/_Layout.cshtml`
- `AcademicRepository/Migrations/ApplicationDbContextModelSnapshot.cs`
- `AcademicRepository.Tests/Program.cs` (upgrade preservation check, suite invocation and nonmutating form helper)
- `README.md`

No files were deleted. These changes are uncommitted; no push was performed. Stopped before Milestone 4.
