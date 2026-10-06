# AcademicRepository — Milestones 1 and 2

ASP.NET Core MVC, .NET 10, EF Core SQL Server, Identity, Razor and local Bootstrap 5.3.8. Foundation/authentication and Admin department/user management are implemented. Git is initialized; no commit was created.

See [MILESTONE2.md](MILESTONE2.md) for the latest migration, verification results, design decisions, complete changed-file inventory and manual checklist. The sections below retain the original Milestone 1 setup details.

## Setup

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
dotnet user-secrets set "DevelopmentAdmin:Email" "your-admin-email@example.com" --project AcademicRepository
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

For future approved schema changes, generate a migration with `dotnet ef migrations add <Name> --project AcademicRepository`. To inspect deployable SQL: `dotnet ef migrations script --idempotent --project AcademicRepository`. Migrations are applied explicitly, not automatically at application startup. Missing schema/connection failures are logged and stop startup. Production runtime database permissions can therefore be narrower than migration permissions.

Trust the local HTTPS development certificate if necessary with `dotnet dev-certs https --trust`. Browse `https://localhost:7145`. Login is `/Account/Login`; logout is an antiforgery-protected POST. Public registration has no endpoint. Home `/` and all role dashboards require authentication. An unauthorized role redirects to `/Account/AccessDenied`, which returns HTTP 403. Admin has no implicit access to other roles. Users with multiple assigned roles can access each assigned role's pages, following standard Identity role behavior.

## Checks and results

```powershell
dotnet run --project AcademicRepository.Tests
```

This executable integration harness uses real SQL Server Express and a uniquely named `AcademicRepository_Test_<guid>` database. It applies the checked-in migration, creates ephemeral random-password users, exercises MVC cookies and antiforgery, and deletes its own test database in `finally`. It does not create test users in the application database. It requires permission to create/delete test databases. It verifies role seeding is idempotent, development Admin login, all 16 role dashboard access combinations, role-specific navigation, home content, anonymous redirects, 403 Access Denied, disabled registration, logout/session removal, CSRF protection, POST-only logout and safe login return URLs.

Verified on this machine: solution build with zero warnings/errors; migration applied to SQL Server Express; Identity tables created; integration checks passed. The production database uses the configured connection; developer Admin credentials remain for you to configure. Future-feature links render placeholders only.

## Manual acceptance checklist

1. Configure SQL connection and development Admin secrets, apply migrations and start using the Development profile.
2. Confirm `AspNetUsers`, `AspNetRoles`, `AspNetUserRoles`, `AspNetUserClaims`, `AspNetRoleClaims`, `AspNetUserLogins`, `AspNetUserTokens` and migration history exist. Confirm the four roles.
3. Visit `/` and each role dashboard anonymously; confirm redirects to Login.
4. Login as the seeded Admin; confirm title, welcome, role and Admin navigation. Visit `/Admin/Dashboard`; visit the other three dashboards and confirm Access Denied.
5. Create departments and users through the Admin Departments and Users pages, or run the integration harness for isolated fixtures. Verify each role sees only its links and can access only its own dashboard.
6. Logout, revisit a protected route, and confirm login is required. Verify `/Account/Register` is unavailable.
7. Restart with the same secrets and confirm no duplicate roles/users. Open future-feature links and confirm placeholders only.

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

Application subpaths above are relative to `AcademicRepository/`; this inventory describes the original Milestone 1 files. Build output and check logs are ignored by Git. The Milestone 2 report lists subsequent additions and modifications. No submissions, reviews or repository search were implemented.
