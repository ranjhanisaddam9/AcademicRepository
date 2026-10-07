# AcademicRepository — Milestones 1, 2, 3, 3.5, 4, 5, 6, 7 and 8

Milestone 8 adds the Department Head's read-only Academic Repository and department dashboard. Search approved snapshots by title, abstract, keywords, Student name/number and supervisor, with department-scoped filters, sorting and pagination. Counts and summaries use the same approved eligibility rules as the repository. See [MILESTONE8.md](MILESTONE8.md) for verification and preservation results. No new migration is required. The integration harness covers Milestones 1–8.

The current academic year defaults to a July-start year (for example, `2026-2027`). Override it with `Repository:CurrentAcademicYear` in configuration or the `Repository__CurrentAcademicYear` environment variable to match the institution's stored academic-year labels.

Milestone 7 adds the authenticated Approved Academic Repository. Students use **My Approved Projects** for their own projects; Coordinators use **Approved Repository** for their department. Metadata and downloads come from the version linked to the completed approval, with simple title/keyword search, filters and pagination. Incomplete approvals or unavailable resources are withheld. See [MILESTONE7.md](MILESTONE7.md) for verification and the known legacy approved record without files. No new migration is required. The integration harness covers Milestones 1–7.

Milestone 6 adds rejected-submission revision, resubmission and immutable metadata/resource versions. Read feedback on Details, Start Revision, edit metadata/manage resources, then Resubmit from Edit Revision. Department and project type remain locked. Historical files remain available through authorized version pages, and new reviews link to new versions. See [MILESTONE6.md](MILESTONE6.md) for migration, tests, preservation evidence and legacy notes. The integration harness now covers Milestones 1–6.

Milestone 5 adds department-scoped Coordinator dashboards, review queues, filters, pagination and secure resource downloads. Explicit Start Review creates a pending history record; its initiating Coordinator can approve or reject it. Rejection requires comments. Students can see their own review history; reviewed submissions remain frozen. See [MILESTONE5.md](MILESTONE5.md) for schema, migration, verification and known legacy data. The integration harness now covers Milestones 1–5.

Milestone 4 adds private academic resource uploads to existing Student submissions. See [MILESTONE4.md](MILESTONE4.md) for verification, storage settings, migration, tests and deployment notes. Save a draft, add resources on Details/Edit, then submit. Newly submitted drafts require at least one resource; submitted packages are read-only. Existing submitted records without files are preserved.

ASP.NET Core MVC, .NET 10, EF Core SQL Server, Identity, Razor and local Bootstrap 5.3.8. Foundation/authentication, Admin department/user management, Student draft/submission workflows, institutional authentication and ORIC/QEC role expansion are implemented. See the milestone reports for the current working-tree changes.

See [MILESTONE3.5-VERIFICATION.md](MILESTONE3.5-VERIFICATION.md) for the current verification results, migrations, changed files and remaining legacy-account correction. [MILESTONE3.5.md](MILESTONE3.5.md), [MILESTONE3.md](MILESTONE3.md) and [MILESTONE2.md](MILESTONE2.md) document the milestone implementations. The sections below retain the original Milestone 1 setup details.

## Setup

For local Student email testing, `Email:DevelopmentStudentRecipient` can redirect Student OTPs to a test mailbox without changing the Student account email or department. It is currently configured in local user secrets as `ranjhanisaddam@smiu.edu.pk`. Staff recovery is unaffected; the override is forbidden outside Development. See [OFFICE365.md](OFFICE365.md) for setup and removal commands.

Student email format: `CSC20F005@smiu.edu.pk`. The leading letters map to the department; `20` is the two-digit year, `F` is Fall (`S` is Spring), and `005` is the three-digit sequence. Validation is case-insensitive. The stored StudentNumber inserts separators: `CSC20F005` becomes `CSC-20F-005`; email addresses stay compact. Existing mappings remain BUS, CSC, BSE, ENG, ENV, BDS, BAI and BIT. Apply `BackfillPrefixStudentNumbers` and `FormatStudentNumbers` along with all prior migrations. Admins do not register Students; accounts are created only after email OTP verification.

Open `AcademicRepository.slnx` in a .NET 10 compatible Visual Studio, or this directory in VS Code. Install the .NET 10 SDK. A workspace-local SDK was installed at `H:\AI\.dotnet` for verification; it is outside this repository. For this machine's PowerShell session:

```powershell
$env:DOTNET_ROOT = 'H:\AI\.dotnet'
$env:PATH = 'H:\AI\.dotnet;' + $env:PATH
dotnet tool restore
dotnet restore
```

`AcademicRepository/appsettings.json` contains `ConnectionStrings:DefaultConnection`. The default uses Windows authentication against `.\SQLEXPRESS` and database `AcademicRepository`. Change the server/database to your SQL Server instance. The Windows account running the app needs database access; the account applying migrations needs schema creation rights. `TrustServerCertificate=True` is for local development; use a valid trusted server certificate and `TrustServerCertificate=False` in production. Store SQL authentication passwords in secrets/environment configuration rather than source control.

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=.\SQLEXPRESS;Database=AcademicRepository;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True" --project AcademicRepository
dotnet user-secrets set "DevelopmentAdmin:Email" "your-admin@smiu.edu.pk" --project AcademicRepository
dotnet user-secrets set "DevelopmentAdmin:Password" "<your unique development password>" --project AcademicRepository
```

Replace the password placeholder with a unique password of at least six characters including uppercase, lowercase, a digit and a nonalphanumeric character. No development password is committed or configured by this implementation. User secrets are a local development facility, not encrypted production credential storage. The launch profile sets Development. Both seed values are required when either is supplied. Startup creates the account only if absent and assigns Admin only if missing; it never resets an existing password. Admin credential seeding is ignored outside Development. Roles are seeded in every environment. Single-instance startup is assumed; coordinate startup/seeding when deploying multiple instances.

## Database and run commands

The initial migration is already included; do not recreate it during normal setup.

```powershell
dotnet tool restore
dotnet ef database update --project AcademicRepository
dotnet build AcademicRepository.slnx
dotnet run --project AcademicRepository
```

The additive `AddOtpAuthenticationAndOricQec` migration adds only the `AuthenticationCodes` table. To update an existing database, run `dotnet ef database update --project AcademicRepository`. To inspect deployable SQL: `dotnet ef migrations script --idempotent --project AcademicRepository`. Migrations are applied explicitly, not automatically at application startup. Missing schema/connection failures are logged and stop startup. Production runtime database permissions can therefore be narrower than migration permissions.

Trust the local HTTPS development certificate if necessary with `dotnet dev-certs https --trust`. Browse `https://localhost:7145`. Login is `/Account/Login`; logout is an antiforgery-protected POST. Public registration has no endpoint. Home `/` and all role dashboards require authentication. An unauthorized role redirects to `/Account/AccessDenied`, which returns HTTP 403. Admin has no implicit access to other roles. Users with multiple assigned roles can access each assigned role's pages, following standard Identity role behavior.

## Checks and results

```powershell
dotnet run --project AcademicRepository.Tests
```

This executable integration harness uses real SQL Server Express and a uniquely named `AcademicRepository_Test_<guid>` database. It applies all migrations, creates ephemeral test identities, exercises MVC flows and antiforgery, and deletes only that uniquely named test database in `finally`. It covers Milestone 1–3.5 preservation/regression, roles/navigation, submissions/ownership/department isolation, OTP and recovery, institutional email and passwordless Students. It requires permission to create/delete test databases.

Verified on this machine: solution build with zero warnings/errors; all SQL Server integration checks passed; and the additive migration was applied to the configured local database. Configure a stable development Admin account through user secrets as described above. Microsoft 365 email setup is documented in [OFFICE365.md](OFFICE365.md); OTP signing-key setup is in [MILESTONE3.5.md](MILESTONE3.5.md).

## Manual acceptance checklist

1. Configure SQL connection and development Admin secrets, apply migrations and start using the Development profile.
2. Confirm `AspNetUsers`, `AspNetRoles`, `AspNetUserRoles`, `AspNetUserClaims`, `AspNetRoleClaims`, `AspNetUserLogins`, `AspNetUserTokens`, `AuthenticationCodes` and migration history exist. Confirm the five roles.
3. Visit `/` and each role dashboard anonymously; confirm redirects to Login.
4. Login as the seeded Admin; confirm title, welcome, role and Admin navigation. Visit `/Admin/Dashboard`; visit the other three dashboards and confirm Access Denied.
5. Create departments and users through the Admin Departments and Users pages, or run the integration harness for isolated fixtures. Verify each role sees only its links and can access only its own dashboard.
6. Logout, revisit a protected route, and confirm login is required. Verify `/Account/Register` is unavailable.
7. Restart with the same secrets and confirm no duplicate roles/users. Student links now open the submission workflow; Coordinator and DepartmentHead future-feature links remain placeholders.

## Files created

All source files in this repository are new:

- `.gitignore`, `global.json`, `dotnet-tools.json`, `AcademicRepository.slnx`, `README.md`.
- `AcademicRepository/AcademicRepository.csproj`, `Program.cs`, `appsettings.json`, `Properties/launchSettings.json`.
- `Models/ApplicationUser.cs`, `Models/LoginViewModel.cs` (includes the dashboard record).
- `Data/ApplicationDbContext.cs`, `Data/IdentitySeeder.cs`.
- `Controllers/AccountController.cs`, `Controllers/HomeController.cs`, `Controllers/RoleControllers.cs`.
- `Views/_ViewImports.cshtml`, `Views/_ViewStart.cshtml`, `Views/Account/Login.cshtml`, `Views/Account/AccessDenied.cshtml`, `Views/Home/Index.cshtml`, `Views/Home/Error.cshtml`, `Views/Shared/_Layout.cshtml`, `Views/Shared/RoleDashboard.cshtml`, `Views/Shared/Placeholder.cshtml`.
- `Migrations/20261006061443_InitialIdentity.cs`, its `.Designer.cs`, and `Migrations/ApplicationDbContextModelSnapshot.cs`.
- `wwwroot/lib/bootstrap/bootstrap.min.css` (vendored Bootstrap with upstream license notice).
- `AcademicRepository.Tests/AcademicRepository.Tests.csproj`, `AcademicRepository.Tests/Program.cs`.

Application subpaths above are relative to `AcademicRepository/`; this inventory describes the original Milestone 1 files. Build output and check logs are ignored by Git. The Milestone 2/3 reports list subsequent additions and modifications. No file uploads, reviews or repository search were implemented.
