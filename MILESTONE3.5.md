# Milestone 3.5 — SMIU Authentication, Department Scoping and ORIC/QEC

StudentNumber is stored as `CSC-20F-005`, derived from the compact email `CSC20F005@smiu.edu.pk`. Spring uses `CSC-20S-005`. Apply `FormatStudentNumbers` to reformat previously stored compact numbers; emails and Identity IDs are preserved.

Format correction: Student emails use `CSC20F005@smiu.edu.pk` (department prefix + two-digit year + F/S session + three-digit sequence). The old hyphenated examples are superseded. `BackfillPrefixStudentNumbers` preserves existing identities and backfills missing numbers only for matching Student emails. The corrected format passed 839 regression assertions with zero failures on 2026-10-06.

The current review is [MILESTONE3.5-VERIFICATION.md](MILESTONE3.5-VERIFICATION.md). It supersedes the earlier verification below. Students now self-onboard only after successful OTP verification; Admins create staff only. StudentNumber is stored separately, uniquely indexed and derived from the institutional email. Database department keywords map the eight supported programs. Apply all migrations, including `AddStudentEmailDepartmentMapping` and `AddUniqueStudentNumber`.

## Outcome and verification

Institutional account validation now accepts only a syntactically valid email whose host is `smiu.edu.pk` (case-insensitive). Students sign in with one-time email codes; Admin, Coordinator, DepartmentHead and ORICQEC use passwords. Staff password recovery verifies an emailed code and then uses ASP.NET Core Identity's reset token. Student password sign-in and Student staff-password recovery are blocked, including for legacy accounts that still have a password hash.

The additive migration `20261006081334_AddOtpAuthenticationAndOricQec` creates the `AuthenticationCodes` table, its unique user/purpose and nonce indexes, and a restrictive Identity-user foreign key. It was applied to the configured local SQL Server database. It does not alter or remove Identity users, departments or project submissions. An account audit found one incompatible existing account: `admin@example.com`. It was retained unchanged; this account must be corrected manually to an SMIU address before it can sign in. Configure a separate institutional development Admin in user secrets first if needed. No database reset occurred.

Verification completed:

- `dotnet build AcademicRepository.slnx --configuration Release --no-restore`: passed with 0 warnings and 0 errors.
- `dotnet run --project AcademicRepository.Tests --configuration Release --no-build`: passed all Milestone 1, 2, 3 and 3.5 SQL/MVC integration checks against a disposable uniquely named SQL Server database.
- The harness begins with the initial Identity migration and an existing non-SMIU Identity account, upgrades through all migrations, and confirms user/profile/password/department data remain intact.
- Checks include email parsing, passwordless Student creation, valid/invalid/expired/reused/locked OTPs, OTP cooldown and hourly throttling, inactive/nonexistent/ineligible users, staff recovery and reset for every staff role, ORICQEC access, role restrictions, existing submission regressions, forged department/owner values and department transfer behavior.

## Authentication and account setup

Student sign-in is at `/Account/StudentSignIn`. The app generates a cryptographically random six-digit code, stores only an HMAC digest bound to the user, purpose, nonce and Identity security stamp, and expires it after ten minutes by default. A successful code is consumed once. The app limits verification attempts, resends per hour and IP authentication requests. The response does not distinguish an unknown or ineligible account from an eligible one.

Staff use `/Account/Login` with their password. `/Account/ForgotPassword` accepts only eligible staff. A successful recovery code grants a short-lived, single-use reset session; the new password is validated and set through Identity. Students cannot use this flow. Account eligibility is checked at sign-in and cookie validation, and requires an active account, an SMIU address, an application role and a department for Student/Coordinator/DepartmentHead.

During local Development, OTP email delivery uses `DevelopmentLog` from `appsettings.Development.json`. Codes appear in the application log only in that environment. Microsoft 365 delivery now uses Microsoft Graph and your Entra app registration. Set `Email:DeliveryMode` to `MicrosoftGraph` and configure TenantId, ClientId, ClientSecret and From through user secrets or deployment configuration. See [OFFICE365.md](OFFICE365.md) for the exact setup commands and application Mail.Send permission requirements. Development logging is rejected outside Development; Graph configuration is validated during startup.

Production must also set `AuthenticationCodes__HmacKey` to a stable base64-encoded secret containing at least 32 cryptographically random bytes, using a deployment secret store. For local development, an optional stable key can be stored with user secrets; when omitted, Development generates an in-memory key at startup and outstanding codes become invalid after an app restart. Generate a value locally without printing it to source/configuration files:

```powershell
$otpKeyBytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
$otpKey = [Convert]::ToBase64String($otpKeyBytes)
dotnet user-secrets set 'AuthenticationCodes:HmacKey' $otpKey --project AcademicRepository
Remove-Variable otpKey, otpKeyBytes
```

Never use `DevelopmentLog` or an ephemeral signing key in Production. The Microsoft 365 client secret and OTP signing key do not belong in checked-in JSON.

The OTP test harness replaces the email sender with an in-memory recorder, so it never sends real mail. `appsettings.Development.json` is the safe manual development mode: start the app in Development and read the code in the application log. The default OTP lifetime is ten minutes, the resend cooldown is 60 seconds, the hourly user limit is five deliveries, verification permits five attempts, and the per-IP limit is 20 authentication requests per minute. Values are configurable under `AuthenticationCodes`.

## Department and role behavior

Student, Coordinator and DepartmentHead profiles require a department. Admin and ORICQEC do not. ORICQEC has a protected placeholder at `/ORICQEC/Dashboard`, is institution-wide and uses staff authentication. No Coordinator review, DepartmentHead search, ORIC reporting or Evaluator behavior was added.

Student submission forms show the assigned department as read-only. Create, edit and submit operations set `ProjectSubmission.DepartmentId` from the current server-side Student profile and never bind ownership or department from the form. Existing draft ownership restrictions remain. On a department transfer, editing a draft adopts the Student's current department. No Coordinator department workflow was added.

## Database commands

Run from the repository root with the .NET 10 SDK:

```powershell
dotnet ef database update --project AcademicRepository
dotnet build AcademicRepository.slnx --configuration Release
dotnet run --project AcademicRepository.Tests --configuration Release
dotnet run --project AcademicRepository --configuration Development
```

`database update` is safe to repeat. The migration is already applied to the configured local SQL Server database. Existing incompatible user accounts are not changed automatically because changing an address or resolving duplicates could assign access to the wrong person. Correct `admin@example.com` to an institution-verified SMIU address after signing in with another valid Admin account, or follow the institution's account-correction procedure.

## Manual checklist

1. Configure `DefaultConnection`, `DevelopmentAdmin:Email` as an `@smiu.edu.pk` address, and `DevelopmentAdmin:Password` with `dotnet user-secrets` as shown in [README.md](README.md). Configure email delivery mode and (optionally) a stable Development OTP HMAC key. Apply migrations and start in Development.
2. Confirm the migration history contains `20261006081334_AddOtpAuthenticationAndOricQec`, `AuthenticationCodes` exists, and all five roles exist. Confirm existing users, departments and submissions remain present.
3. Enter a valid Student email such as `CSC20F005@smiu.edu.pk` through Student Sign In. Confirm no account exists until its OTP is verified. Confirm the created account has no password, its StudentNumber is stored, and its department is mapped. Log out and sign in again with a fresh code. Admin creation must not offer Student.
4. Try invalid-domain email, invalid/expired/reused codes, too many attempts and an immediate resend. Confirm the generic response, cooldown and rejection behavior.
5. Try Student password sign-in and Student Forgot Password. Confirm neither signs in nor sends a staff recovery code.
6. Sign in as staff with a password. Recover an Admin, Coordinator, DepartmentHead and ORICQEC test account using the Development log; confirm the new password works and the old one fails.
7. Self-onboard Student A and Student B with CSC and BSE emails. Confirm A's submission always uses A's assigned department, forged `DepartmentId`/`StudentId` values do not change it, and B receives 404 for A's submission.
8. Confirm Student/Coordinator/DepartmentHead without a department cannot authenticate, while Admin and ORICQEC may have no department. Confirm only ORICQEC can open `/ORICQEC/Dashboard`.
9. Confirm drafts can still be created, edited, deleted and submitted, and that uploads, review actions, repository search, reporting and evaluation remain unavailable.

## Files created

- `AcademicRepository/Models/AuthenticationCode.cs`
- `AcademicRepository/Models/OtpViewModels.cs`
- `AcademicRepository/Services/AuthenticationCodeService.cs`
- `AcademicRepository/Services/AuthenticationEmailSender.cs`
- `AcademicRepository/Services/InstitutionalEmail.cs`
- `AcademicRepository/Views/Account/RequestCode.cshtml`
- `AcademicRepository/Views/Account/VerifyStudentCode.cshtml`
- `AcademicRepository/Views/Account/VerifyRecoveryCode.cshtml`
- `AcademicRepository/Views/Account/ResetStaffPassword.cshtml`
- `AcademicRepository/Views/Account/_VerifyCode.cshtml`
- `AcademicRepository/Views/ORICQEC/Dashboard.cshtml`
- `AcademicRepository/Migrations/20261006081334_AddOtpAuthenticationAndOricQec.cs`
- `AcademicRepository/Migrations/20261006081334_AddOtpAuthenticationAndOricQec.Designer.cs`
- `AcademicRepository.Tests/Milestone35Checks.cs`
- `MILESTONE3.5.md`

## Files modified

- `AcademicRepository/Controllers/AccountController.cs`, `Controllers/RoleControllers.cs`, `Controllers/UsersController.cs`
- `AcademicRepository/Data/ApplicationDbContext.cs`, `Data/IdentitySeeder.cs`
- `AcademicRepository/Models/LoginViewModel.cs`, `Models/ManagementViewModels.cs`
- `AcademicRepository/Program.cs`, `Services/ActiveUserSignInManager.cs`
- `AcademicRepository/Views/Account/Login.cshtml`, `Views/Shared/_Layout.cshtml`, `Views/Users/Form.cshtml`, `Views/Users/Index.cshtml`
- `AcademicRepository/appsettings.json`, `appsettings.Development.json`
- `AcademicRepository/Migrations/ApplicationDbContextModelSnapshot.cs`
- `AcademicRepository.Tests/Program.cs`, `Milestone2Checks.cs`, `Milestone3Checks.cs`
- `README.md`

The checkout also contains earlier uncommitted Milestone 3 submission files and migration from the existing worktree; they were preserved and covered by regression checks. No Milestone 4 work was started. No commit or push was performed.
