# Milestone 2 — Departments and User Management

Implemented on the existing MVC/Identity foundation. No submission, upload, approval, search, audit log or email functionality was added.

## Results

- Full solution Release build: zero warnings and zero errors.
- Existing Milestone 1 checks and new Milestone 2 checks: all 268 integration assertions passed (includes shared antiforgery/helper assertions).
- A test database was created with the original migration and a pre-existing password-hashed Identity account, then upgraded. The account, password and login remained working; its profile was backfilled and remained active.
- Applied `20261006064402_AddDepartmentsAndUserProfiles` to the configured SQL Server database. Compared original users, roles and user-role assignments before and after the migration; their counts/checksums matched. No database reset/deletion was performed on the application database.
- The application started in Development from Release output against the migrated database. Login and the vendored Bootstrap stylesheet returned HTTP 200 over HTTPS. The verification server was stopped afterward.
- All temporary integration databases were removed.

The running Debug application (process 12084 at verification time) locked its build output. Windows denied permission to stop that process. Release output was therefore used for all final checks. Stop your existing debugging session/server before rebuilding Debug or launching the updated app; the old running process does not load new code automatically. The HTTPS development certificate is present but untrusted; trust it locally if your browser warns.

## Commands

Run from `H:\AI\AcademicRepostory`, using the .NET 10 SDK:

```powershell
# Needed on this machine if your PATH still selects the .NET 9 SDK:
$env:DOTNET_ROOT = 'H:\AI\.dotnet'
$env:PATH = 'H:\AI\.dotnet;' + $env:PATH

dotnet tool restore
dotnet build AcademicRepository.slnx --configuration Release
dotnet ef database update --project AcademicRepository --configuration Release --no-build
dotnet run --project AcademicRepository.Tests --configuration Release --no-build
dotnet run --project AcademicRepository --configuration Release
```

The migration is checked in and already applied here. The update command is safe to repeat; it is the only migration command required on an existing installation. Do not recreate the migration or reset the database. The command used to generate it was:

```powershell
dotnet ef migrations add AddDepartmentsAndUserProfiles --project AcademicRepository --configuration Release --no-build
```

`DefaultConnection` and your existing development Admin secrets remain unchanged. No existing credential values were printed, committed, or replaced. The previous README contains the setup commands for secrets.

## Implementation decisions

- `Department` belongs to the existing EF context, with case-insensitive SQL unique indexes on Name (120 characters) and Code (20). Input is trimmed; codes are uppercase. Created/updated timestamps use UTC. Activation is a POST; there is no delete endpoint. A restrictive foreign key also protects departments associated with users.
- Identity remains the sole authentication store. `ApplicationUser` now holds required FullName (150), nullable DepartmentId, Department navigation, and IsActive defaulting to true. The migration backfills old FullName values from username/email (truncated safely). Existing users receive no guessed department assignment; Admin should complete their profiles through Edit.
- Management endpoints are `/Departments` and `/Users` and require Admin on the whole controller. Razor forms use typed view models, global antiforgery validation, allowlisted fields and server-side validation. Invalid and missing activation values return HTTP 400. Department detail pages link to `/Users?departmentId=<id>`.
- New and edited accounts require a department for Student, Coordinator and DepartmentHead; Admin may remain unassigned. Only active departments can be newly assigned. An existing inactive assignment can be retained during editing. Deactivating a department does not deactivate its users or prevent their logins; departments and user status are separate. No future submission routing rules are implemented.
- All password creation/reset uses UserManager. The UI shows Identity validation messages and never displays stored hashes or passwords. Passwords are cleared from forms on validation failures while validation errors remain visible. Share temporary passwords outside the application through an appropriate secure channel. Forced first-login password changes and delivery notifications are not part of this milestone.
- User creation and profile/role changes use database transactions. Editing removes only the four application role memberships before assigning one primary role, preserving system claims and unrelated role memberships. Existing multiple-role accounts are normalized when edited. The configured development seed Admin is kept in exactly the Admin application role on Development startup, so it should remain your reserved development administrator.
- A custom SignInManager rejects inactive accounts. Cookie validation checks current activity and security stamp on every authenticated request. Deactivation, role/profile changes and password reset invalidate existing sessions immediately. This adds a database lookup per authenticated request, appropriate for this simple application. Users must log in again after edits, including an Admin editing their own profile.
- User edits check a Data Protection encrypted edit token bound to the user ID and Identity concurrency stamp to reject stale/tampered form submissions. Raw Identity security fields are not exposed in forms. Admins cannot deactivate themselves or remove their own Admin role. Existing data is otherwise preserved. No arbitrary-role creation UI, extra authentication tables, repository pattern, API or frontend framework was introduced.
- DashboardService supplies full name, role(s), department and optional Admin counts to Home and all four existing role dashboards. Admin navigation exposes Departments and Users. Other roles retain their Milestone 1 navigation and future-feature placeholders.
- Optional sample department seeding was omitted; create real departments through the Admin UI. Existing development Admin and role seeding remains idempotent. Startup seeding assumes a single application instance, as before.

## Manual checklist

1. Stop the old app, run the database update and launch Release using the commands above. Login with your existing Admin credentials. Confirm welcome/full name, Admin role, Not Assigned department, counts and management links. Edit the Admin profile if its migrated name initially shows its email.
2. Open Departments. Create CS / Computer Science and SE / Software Engineering. Try duplicate names/codes with different casing and blank values; confirm useful errors. Edit, view details, deactivate and reactivate a department.
3. Open Users. Create a Student, Coordinator and DepartmentHead with an active department, plus an Admin with no department. Try omitting each required department, using an invalid email, duplicate email or weak/missing temporary password; confirm rejection without partial user records.
4. Login as each role in separate browser sessions. Verify full name, role, department and role-specific navigation. Confirm that non-Admins receive Access Denied for Departments/Users, including direct create/edit/reset/status endpoints, and for other roles' dashboards.
5. Edit a user to change department and role. Confirm the listing, single application role and new dashboard profile. The user's old session should be logged out on its next request. Login again and confirm the new role's access.
6. Deactivate a logged-in user. Their next protected request should require login and their new login attempt should fail. Reactivate and verify login works. Confirm you cannot deactivate yourself or remove your own Admin role.
7. Reset a user's password. Weak/missing values should fail; a valid password should succeed. Confirm existing sessions are revoked, the old password fails, and the new password works. No password/hash should appear in user listings.
8. Deactivate a department with users. Confirm users remain, existing account access continues, and new assignments to that department are rejected. Open its detail page and follow the assigned-users link.
9. Verify logout, anonymous redirects, disabled registration and all Milestone 1 role boundaries still work. Restart to confirm role/Admin seed idempotency. Run the automated harness for repeatable isolated SQL/HTTP checks.

## Files created

Application paths below are relative to `H:\AI\AcademicRepostory\AcademicRepository`:

- `Models/Department.cs`
- `Models/ManagementViewModels.cs`
- `Services/ActiveUserSignInManager.cs`
- `Services/DashboardService.cs`
- `Controllers/DepartmentsController.cs`
- `Controllers/UsersController.cs`
- `Views/Departments/Index.cshtml`
- `Views/Departments/Form.cshtml`
- `Views/Departments/Details.cshtml`
- `Views/Users/Index.cshtml`
- `Views/Users/Form.cshtml`
- `Views/Users/ResetPassword.cshtml`
- `Migrations/20261006064402_AddDepartmentsAndUserProfiles.cs`
- `Migrations/20261006064402_AddDepartmentsAndUserProfiles.Designer.cs`

Repository-level files:

- `AcademicRepository.Tests/Milestone2Checks.cs`
- `MILESTONE2.md`
- `milestone2-checks.log` (generated ignored verification output)

## Files modified

Application paths:

- `Models/ApplicationUser.cs`
- `Models/LoginViewModel.cs` (dashboard model)
- `Data/ApplicationDbContext.cs`
- `Data/IdentitySeeder.cs`
- `Program.cs`
- `Controllers/HomeController.cs`
- `Controllers/RoleControllers.cs`
- `Views/Home/Index.cshtml`
- `Views/Shared/_Layout.cshtml`
- `Migrations/ApplicationDbContextModelSnapshot.cs`

Repository-level files:

- `AcademicRepository.Tests/Program.cs`
- `README.md`

Project/package files, appsettings/connection string, launch settings, original migration and user secrets were not changed in Milestone 2. Git was already initialized but the original files are still uncommitted; no commit was created. No Milestone 3 work was begun.
