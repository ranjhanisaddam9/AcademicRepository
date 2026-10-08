# Milestone 10 Verification Report

## A–S verification

| Item | Result | Evidence |
|---|---|---|
| A. Build | PASS | Clean Release build succeeded with 0 warnings and 0 errors. |
| B. Database and migrations | **FAIL** | The additive migration applied and is recorded. The read-only baseline had 3 submissions, 3 project-file rows and 4 review rows; the final audit found 0 in each table. The migration only adds nullable `AspNetUsers.CreatedAt` and `SystemSettings`, with no destructive statements. Seven users, eight departments and seven role assignments retained identical audit hashes. Three physical files remain without database file rows. The source of the submission-row discrepancy is unknown; no restoration or repair was attempted. |
| C. SMIU email validation | PASS | Existing regression checks validate the institutional domain and Student email parsing. |
| D. Microsoft Graph email | PASS (code/config) | Graph configuration is present; Graph token/send behavior and delivery failures pass integration checks. No live message was sent for this review. |
| E. Student self-onboarding | PASS | OTP verification creates the account, assigns Student and mapped department, and signs in; admin Student creation/edit are blocked. |
| F. Student passwordless authentication | PASS | Student OTP sign-in works; staff password login/recovery excludes Students. |
| G. OTP security | PASS | Existing regression checks cover hashing, expiry, one-time use, attempts, cooldown, throttling and delivery-failure invalidation. |
| H. Student number | PASS | Canonical Student ID is distinct from Identity ID; duplicate protection is database-backed. |
| I. Department mappings | PASS | All eight configured mappings remain present without duplicates; the department audit hash is unchanged. |
| J. Submission department security | PASS | Regression checks cover server-derived department assignment and tampering. |
| K. Student ownership security | PASS | Existing submission ownership tests pass. |
| L. Staff authentication | PASS | Staff email/password authentication and role checks pass. |
| M. Staff password recovery | PASS | OTP, limits, expiry, one-time grants, Identity reset and staff-role coverage pass. |
| N. ORIC/QEC | PASS | Role seeding, access boundaries and institution-wide read-only repository behavior pass. |
| O. Authorization | PASS | Role-protected actions and navigation regression checks pass. |
| P. Milestone 1 regression | PASS | Identity, roles, login/logout and dashboard integration checks pass. |
| Q. Milestone 2 regression | PASS | Department and staff management checks pass; Student editing is now intentionally prohibited. |
| R. Milestone 3 regression | PASS | Draft, submit, details, review and resource regression checks pass. |
| S. Automated tests | PASS | The isolated SQL Server/MVC harness passed **1,693 assertions**, with zero failed assertions. It verified its application host uses the generated test database. |

## Milestone 10 implementation

The Admin dashboard now presents operational counts and consistency warnings without review actions. Admin pages provide department mapping management, database-backed user search/filter/pagination, safe system information, read-only diagnostics and validated operational settings. Admin can activate/deactivate Students but cannot create, edit, set passwords for, or change Student identity data. Staff role/department rules and last-active-Admin/self-deactivation guards are enforced server-side.

Settings stored in `SystemSettings` include upload limits, OTP expiry/attempts/cooldown, repository page size, academic year, display name and institutional support email. Changes apply at runtime; Graph credentials, connection strings and other secrets are excluded. The migration is `20261008074128_AddAdministrativeConfiguration`.

## Database finding requiring a stop

The configured database is `AcademicRepository`. Before migration, the audit reported 7 users, 8 departments, 7 role assignments, 3 submissions, 3 project-file rows and 4 review rows. After applying the migration and completing the isolated test run, the final audit reported 7 users, 8 departments and 7 role assignments with unchanged hashes, but 0 submissions, 0 project-file rows and 0 review rows. Three physical resource files remain in storage without corresponding database metadata.

The migration contains only an additive nullable column and a new settings table. The test harness was verified to target a generated isolated database, and all tests passed. These checks do not explain the changed submission records. The discrepancy is therefore unresolved and Milestone 10 cannot be marked verified until the repository data is reconciled from an authoritative backup or other source. The application did not attempt to reconstruct records or delete the remaining physical files.

## Migrations and configuration

Already applied to the configured database. For another environment, run:

```powershell
dotnet ef database update --project AcademicRepository/AcademicRepository.csproj --configuration Release
```

To generate a future migration:

```powershell
dotnet ef migrations add <MigrationName> --project AcademicRepository/AcademicRepository.csproj --configuration Release
```

`DefaultConnection` remains in `appsettings.json`. Graph credentials and Admin development credentials remain deployment/user-secret settings and are not editable in the application’s operational settings page.

## Files changed

- Admin management and operations: `Controllers/DepartmentsController.cs`, `Controllers/UsersController.cs`, `Controllers/RoleControllers.cs`, `Services/AdminSystemService.cs`, `Services/OperationalSettingsService.cs`.
- Settings schema and models: `Data/ApplicationDbContext.cs`, `Models/ApplicationUser.cs`, `Models/ManagementViewModels.cs`, `Models/AdminViewModels.cs`, `Models/SystemSetting.cs`, the model snapshot, and the new administrative migration/designer.
- Runtime configuration: `Program.cs`, `Services/AuthenticationCodeService.cs`, `Services/LocalFileStorageService.cs`, `Services/ProjectFileService.cs`, `Services/RepositoryService.cs`, `Services/RepositoryReportService.cs`.
- Razor UI: `Views/Departments/Form.cshtml`, `Views/Departments/Index.cshtml`, `Views/Users/Index.cshtml`, `Views/Shared/_Layout.cshtml`, and new Admin overview, settings, system-information and diagnostics views under `Views/Home/`.
- Automated checks: `AcademicRepository.Tests/Milestone10Checks.cs`, `Program.cs`, `DatabaseAudit.cs`, plus updated Milestone 2 and 4 test helpers.

## Validation totals and known issue

- Clean, restore and Release build: PASS, 0 warnings, 0 errors.
- Full Milestones 1–10 SQL Server/MVC integration harness: PASS, 1,693 assertions, 0 failed assertions.
- Migration: applied; additive schema change verified.
- Microsoft Graph live delivery: not repeated here; configuration and mocked Graph delivery behavior passed, and the user previously verified live delivery.
- Existing project data: unresolved post-migration audit discrepancy described above; 3 physical orphan files remain. No file paths or automatic repair are exposed by diagnostics.
- **Safe to proceed to Milestone 11: NO.**

MILESTONE 10 NOT VERIFIED — DO NOT PROCEED
