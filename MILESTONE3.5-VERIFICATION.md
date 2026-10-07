# MILESTONE 3.5 VERIFICATION REPORT

**StudentNumber storage clarification (2026-10-06):** email `CSC20F005@smiu.edu.pk` now derives stored StudentNumber `CSC-20F-005` (and `CSC20S005` derives `CSC-20S-005`). Emails remain compact. The shared resolver, onboarding/profile consistency checks and tests use this representation. `20261006113259_FormatStudentNumbers` reformats existing compact numbers without changing emails or Identity IDs; eight migrations are now applied locally. Build and all 839 regression assertions pass; no pending EF model changes. Evidence: `H:\AI\formatted-number-checks.log` and `formatted-number-migration.log`. The earlier legacy-account remediation remains outstanding.

**Subsequent format correction (2026-10-06):** the user clarified `CSC20F005@smiu.edu.pk`: leading department letters, two-digit year, F (Fall) or S (Spring), then three digits. This supersedes the hyphenated format described in the original review below. Parser, UI messages and fixtures were updated; new Fall/Spring and malformed-format tests pass. The full corrected suite has **839 passing assertions / zero failures**. Build passed; `20261006112935_BackfillPrefixStudentNumbers` is applied (seven migrations total), with no pending model changes. Applied older migrations were preserved. Local user, department, submission and role-assignment fingerprints remain unchanged. Evidence: `H:\AI\prefix-format-checks.log`, `prefix-migration.log`, `prefix-database-audit.txt`. The legacy Student whose email has no StudentNumber still requires its actual confirmed identity details; this correction does not rename that account.

Reviewed 2026-10-06 against the actual working tree and configured local SQL Server database. Scope: Milestones 1–3.5 only. No Milestone 4 implementation, account deletion, database reset, commit or push.

## Result

Clean, restore and Release build succeeded with **0 warnings / 0 errors**. The executable regression harness completed **830 passing assertions / 0 failing assertions**. These are assertions, including antiforgery and fixture checks, not 830 independent test cases. Six migrations are applied locally; EF reports no pending model changes. The current installation still has one legacy Student without a usable StudentNumber, so its data migration is incomplete and the installation is **not yet cleared for Milestone 4**.

| Area | Result | Evidence / qualification |
|---|---|---|
| A. Build | PASS | Clean, restore, build; local Kestrel startup and HTTP 200 login pages. |
| B. Database & Migrations | PASS | Real SQL upgrade tests; local update; before/after preservation fingerprints; no pending migration/model change. |
| C. SMIU Email Validation | PASS | Exact parsed host equality, case-insensitive; required positive/negative examples tested. Legacy non-SMIU account remains blocked. |
| D. Microsoft Graph Email | PASS | Transport/configuration/error tests and code inspection. Live delivery user-verified; not independently sent during review. |
| E. Student Self-Onboarding | PASS | All eight mappings; no account before successful OTP; role, department, StudentNumber and Identity session verified. |
| F. Student Passwordless Authentication | PASS | Eligible existing account reused; password route/recovery excluded; inactive and inconsistent profiles denied. Legacy data exception below. |
| G. OTP Security | PASS | Controls individually detailed below; expiry, replay, concurrency, attempt and delivery-failure tests. |
| H. Student Number Validation | **FAIL — existing data** | Code and unique-index tests pass, but one existing Student still has NULL StudentNumber and an incompatible email local part. |
| I. Department Mapping | PASS | Eight actual mappings; existing department ID/code retained; repeated seed tests. |
| J. Submission Department Security | PASS | Forged department ignored; create/edit use current authenticated Student.DepartmentId. |
| K. Student Ownership Security | PASS | CSC Student A / BSE Student B; direct ID manipulation denied on read/edit/delete/submit. |
| L. Staff Authentication | PASS | All four staff roles use password login; Student password rejected, even with legacy hash. |
| M. Staff Password Recovery | PASS | All four roles: reset/login, invalid/expired/used OTP, attempts, cooldown, grant expiry/replay. |
| N. ORIC/QEC | PASS | Idempotent role seed, Admin creation, no department required, recovery and protected dashboard. |
| O. Authorization | PASS | Server attributes, full role matrix, anonymous denial, mutation antiforgery, revoked-session checks. |
| P. Milestone 1 Regression | PASS | Authentication, logout, dashboards, role-specific navigation and access denied. |
| Q. Milestone 2 Regression | PASS | Department management and staff management; Student creation excluded under revised requirements. |
| R. Milestone 3 Regression | PASS | Draft/create/edit/delete/submit/details/list/dashboard, ownership and rowversion concurrency. |
| S. Automated Tests | PASS | Full SQL/MVC harness exit 0; 830 passing assertions, zero failed assertions. |

## Remaining FAIL and remediation

**H — Legacy Student profile migration is incomplete.** The actual database contains Student ID `5afbc759-0d5f-4173-8c99-1d2bc6a59326`, email `ranjhanisaddam@smiu.edu.pk`, DepartmentId `1`, StudentNumber `NULL`. That email cannot supply the required `BUS-20F-001`-style StudentNumber. The migration deliberately does not invent identity information.

- Files involved: `Models/ApplicationUser.cs`, `Services/StudentDepartmentService.cs`, `Controllers/UsersController.cs`, `Migrations/20261006110025_AddUniqueStudentNumber.cs` under `AcademicRepository/`.
- Impact: this preserved legacy Student cannot authenticate using the new mapping rules. Its existing submission remains attached to the same Identity ID.
- Fix applied: new self-onboarded Students store StudentNumber; valid legacy Student emails are backfilled; unique indexes prevent duplicate numbers/emails; authentication rejects missing or inconsistent profiles.
- Required data correction: confirm this person's actual Student Number and matching SMIU mailbox, then update the **existing** Student through Admin profile management. This derives StudentNumber and department server-side and preserves its Identity ID. Do not register a replacement account or guess an address. Verify mailbox ownership through the next Student OTP sign-in. Review its submission department if the confirmed keyword differs from BUS.
- Re-test: backfill, uniqueness and invalid-profile denial pass automatically; actual legacy profile remains unchanged and cannot be declared migrated. Audit after correction and repeat its sign-in/submission checks.

One additional legacy non-SMIU account remains preserved (ID `0f26b3f3-3898-4bb4-a920-02228d2d6b52`; prior audit identified `admin@example.com`). It cannot authenticate under the institutional rule. Correct it to an authorized SMIU address through existing management if still required. The validator correctly blocks it; no account was silently renamed or deleted.

## Phase 1 — individual database/build checks

| Requirement | Result | Evidence |
|---|---|---|
| 1. Builds without errors | PASS | Release clean/build: zero warnings/errors. |
| 2. Starts successfully | PASS | Separate local Kestrel instance on port 5199; StudentSignIn and Login HTTP 200. |
| 3. Existing migrations valid | PASS | Initial-to-latest and M3.5-to-latest temporary SQL databases upgraded. |
| 4. Database updated | PASS | Two pending additive migrations applied to local database. |
| 5. No forward migration resets/destroys data | PASS | Forward migration inspection plus populated upgrade and fingerprint comparisons. Old OTPs intentionally invalidated. |
| 6. Identity tables intact | PASS | Upgrade test confirms all seven Identity tables and role relationship. |
| 7. Existing department intact | PASS | ID 1, Business Administration, code BUS01 preserved; keyword BUS added. |
| 8. Existing submission intact | PASS | One local submission; exact full-row fingerprint unchanged, including rowversion. |
| 9. Existing users intact | PASS | Four local users and four role assignments; original-field fingerprints unchanged. |
| 10. No duplicate department seed | PASS | Eight mappings locally; repeat seeding preserves IDs/names/codes in integration checks. |

Local before/after migration SHA256 fingerprints (same values after startup for users, submissions and role assignments):

| Data | Before count | After startup count | Preserved fingerprint |
|---|---:|---:|---|
| Users, original identity/profile fields | 4 | 4 | `C024F3F17CAC3F5EBB536FDD6726DF5FD6C45EC65C5AC75074EB7FF3FA62C220` |
| ProjectSubmissions, all fields | 1 | 1 | `DAA2D48C17C69297DA0259DB2966C6CAB35E15DDEAF9F35E57503DD827742B25` |
| User role assignments | 4 | 4 | `643CDBA5362578053F34A8E17A3E15007795E6ECDA8C1102AC96A9E4BF99D864` |
| Departments, original fields | 1 | 8 | Before/after migration: `938974A16F9A2180B744C026539D047E5A596A90BC2EDB7766CF5A23F00EB48C`; startup adds seven rows. |

No duplicate normalized emails or submission-to-current-profile department mismatches were found. The one existing submission requires no automatic repair. No sensitive credential values are included in these fingerprints or report.

## Institutional validation, parsing and onboarding

`Services/InstitutionalEmail.cs` uses `MailAddress.TryCreate`, address equality and exact host `smiu.edu.pk` with `OrdinalIgnoreCase`, not substring matching. MVC attributes and an Identity user validator enforce this on the server. All three requested valid domain examples pass; Gmail, Yahoo, Outlook, wrong suffix, fake subdomain suffix and `othersmiu.edu.pk` fail. Domain eligibility alone does not imply Student eligibility: `student@smiu.edu.pk` passes domain validation but lacks a Student Number.

`StudentDepartmentService` centralizes anchored Student email local-part parsing: department letters followed by a two-digit year, F/S session and three sequence digits. It derives uppercase StudentNumber with separators, for example `CSC-20F-005` and queries `Departments.StudentEmailKeyword` at runtime. There is no runtime department-name switch or `Contains("BUS")` check. Empty, malformed and unmapped XYZ inputs are rejected. The format follows the examples supplied; additional historical formats would need explicit confirmation.

Actual seeded mappings and successful resolution of each `KEY20F001` example:

| Keyword | Department | Actual ID | Existing department code | Result |
|---|---|---:|---|---|
| BUS | Business Administration | 1 | BUS01 | PASS |
| CSC | Computer Science | 2 | CSC | PASS |
| BSE | Software Engineering | 3 | BSE | PASS |
| ENG | English | 4 | ENG | PASS |
| ENV | Environment | 5 | ENV | PASS |
| BDS | Data Science | 6 | BDS | PASS |
| BAI | Artificial Intelligence | 7 | BAI | PASS |
| BIT | Information Technology | 8 | BIT | PASS |

`DepartmentSeeder` defines initial mappings only. Runtime resolution queries the database. Existing exact department names are reused, already-mapped renamed departments retain their IDs, and conflicting names/codes stop seeding rather than reassign relationships.

`AuthenticationCodeService` validates domain, parses the Student Number through the resolver and checks the mapped department before sending a first-time code. The pending `AuthenticationCodes` record has no UserId. A verified OTP consumes the challenge and creates the passwordless Identity user, assigns Student and DepartmentId, and records StudentNumber inside one transaction. The MVC action signs in through Identity. Invalid/expired/exhausted codes cannot create users. Concurrent verification permits one winner. Existing eligible accounts are reused.

Admin Create excludes Student in the dropdown and rejects forged Student POSTs. Staff-to-Student edits are rejected. Existing Students may be maintained, but Student password reset GET/POST are blocked and password fields are ignored for a Student edit. Staff roles are Admin, Coordinator, DepartmentHead and ORICQEC. Coordinator/DepartmentHead require a department; Admin/ORICQEC do not.

## OTP controls — separate results

| Control | Result | Evidence |
|---|---|---|
| Cryptographic generation | PASS | `RandomNumberGenerator.GetInt32`; six decimal digits. |
| Configurable expiration | PASS | Validated options; default 10 minutes; simulated-clock tests. |
| One-time use / consumption | PASS | ConsumedAt, SQL rowversion and transaction; replay/concurrent verification tests. |
| Expired OTP rejection | PASS | Existing Student, pending onboarding and all staff roles. |
| Incorrect OTP rejection | PASS | No sign-in, grant or account finalization. |
| Maximum attempts | PASS | Default five; correct code rejected after exhaustion. |
| Resend cooldown | PASS | Default 60 seconds; Student/pending/staff tests. |
| Request throttling | PASS | Five sends/email/purpose/hour tested; IP fixed-window policy inspected, default 20/minute on authentication POSTs. Test host raises IP limit. |
| Safe replacement | PASS | New nonce/hash replaces old challenge; prior token fails. |
| Non-plaintext storage | PASS | HMAC-SHA256 bound to email/user/purpose/nonce/security stamp; fixed-time digest comparison. |
| Production logging | PASS | DevelopmentLog guarded by environment and startup validation; production guard test. No code logged in Graph path. |
| Inactive Student rejection | PASS | Request/sign-in/current cookie eligibility checked and tested. |
| Delivery failure / timeout | PASS | Failed send consumes code using an uncancelled database save; regression tests verify it cannot onboard. |
| Staff reset grant security | PASS | Protected, random, hashed, expiring single-use grant; Identity reset token/password policy; all staff roles tested. |

Production requires a stable base64 `AuthenticationCodes:HmacKey` of at least 32 random bytes. Development may use an ephemeral key, invalidating outstanding OTPs after restart. Persist ASP.NET data-protection keys appropriately when deploying; multi-instance deployment was not tested.

## Microsoft Graph verification

Abstraction: `IAuthenticationEmailSender`; implementation: `AuthenticationEmailSender`, injected into the code service through DI. No SMTP client/package is used. No Graph SDK package is required: built-in `HttpClient` and `System.Text.Json` call the OAuth token endpoint and Graph REST `POST /v1.0/users/{From}/sendMail`.

Authentication uses app-only OAuth client credentials with scope `https://graph.microsoft.com/.default`. Required permission is **Microsoft Graph application `Mail.Send`, with administrator consent**. The sender mailbox is `noreply@smiu.edu.pk`. Microsoft documents [sendMail and its permissions](https://learn.microsoft.com/en-us/graph/api/user-sendmail?view=graph-rest-1.0) and [application permissions/admin consent](https://learn.microsoft.com/en-us/graph/permissions-reference#mail-send).

Required keys: `Email:DeliveryMode=MicrosoftGraph`, `Email:TenantId`, `Email:ClientId`, `Email:ClientSecret`, `Email:From`. Configure them with `dotnet user-secrets set ... --project AcademicRepository` in Development or environment variables `Email__TenantId`, `Email__ClientId`, `Email__ClientSecret`, `Email__From`, `Email__DeliveryMode` in deployment. Setup commands are in `OFFICE365.md`. Committed JSON contains no client secret or HMAC key. The read-only local audit confirmed valid configuration without revealing values.

Transport tests cover token form encoding, recipient/purpose/body, bearer authorization, configured sender, accepted response, authentication failure, Graph rejection and the production logging guard. Graph acceptance is not a guarantee of inbox delivery. On 2026-10-06 the user explicitly stated delivery was already verified and instructed skipping another live test. No real message was sent during this review. Agent-observed end-to-end delivery for both Student and recovery workflows is therefore not claimed.

## Submission and authorization regression

The existing ProjectSubmission entity/table was retained. Draft create/edit/delete, direct submit, editing then submitting, details, My Submissions, dashboard counts, required fields and immutable submitted rows pass. SQL rowversion blocks stale edits/deletes and concurrent mutation after submission.

`Views/StudentSubmissions/_Fields.cshtml` renders a disabled department dropdown with only the assigned department and no bound DepartmentId. The controller uses strongly typed view models and explicit field assignment. Forged StudentId, DepartmentId, Status and timestamps cannot overwrite server values. **Actual behavior:** malicious create/edit department values are ignored; persisted DepartmentId is the current authenticated Student's department. Editing a draft after a legitimate profile change adopts the new profile department. Submitted historical records are not silently rewritten.

Ownership filtering occurs in `StudentSubmissionService.OwnedBy` and controller lookups. CSC Student A / BSE Student B tests receive 404 on cross-owner detail/edit/delete and forged edit-to-submit attempts. Hidden navigation is not an authorization boundary.

Users and Departments controllers require Admin; each role dashboard requires its corresponding role; StudentSubmissions requires Student. Home requires authentication, with an anonymous error page. Public Account actions are limited to authentication/recovery and AccessDenied; Logout is authorized POST. Global automatic antiforgery validation protects unsafe MVC actions. Anonymous and cross-role route matrices pass. AccessDenied returns 403. Identity stamp/inactive/profile checks invalidate ineligible cookies.

Coordinator/DepartmentHead and ProjectSubmission each have DepartmentId foreign keys, supporting future same-department queries. ORICQEC requires no DepartmentId. Coordinator review, DepartmentHead repository search, ORIC reporting, Evaluator, marks and evaluation entities were not implemented. Existing status enum values do not provide approval/rejection workflows.

## Files changed during this verification

Paths below are relative to this repository, distinct from the pre-existing uncommitted milestone changes.

Modified:

- `AcademicRepository/Models/ApplicationUser.cs` — StudentNumber.
- `AcademicRepository/Data/ApplicationDbContext.cs` — unique StudentNumber and normalized email indexes.
- `AcademicRepository/Services/StudentDepartmentService.cs` — canonical number extraction and profile consistency.
- `AcademicRepository/Services/AuthenticationCodeService.cs` — number persistence and delivery-timeout invalidation.
- `AcademicRepository/Controllers/UsersController.cs` — derive number when maintaining an existing Student.
- `AcademicRepository/Migrations/ApplicationDbContextModelSnapshot.cs` — updated schema.
- `AcademicRepository.Tests/Program.cs` — upgrade fixture compatibility, new suite calls, read-only audit option.
- `AcademicRepository.Tests/Milestone2Checks.cs` — meaningful department assertion and Student management negatives.
- `AcademicRepository.Tests/Milestone3Checks.cs` — StudentNumber fixtures and BSE ownership scenario.
- `AcademicRepository.Tests/Milestone35Checks.cs` — consistent fixtures, delivery-failure test support, real assertion replacing unconditional check.
- `README.md`, `MILESTONE3.5.md`, `OFFICE365.md` — current setup/review references and corrected onboarding instructions.

Created:

- `AcademicRepository/Migrations/20261006110025_AddUniqueStudentNumber.cs` and `.Designer.cs` — additive number backfill and uniqueness.
- `AcademicRepository.Tests/OnboardingChecks.cs` — all mappings, onboarding, concurrent/replayed/expired/exhausted OTP, duplicate identities, profile mismatch, all-role recovery limits and delivery failure.
- `AcademicRepository.Tests/MigrationUpgradeChecks.cs` — populated upgrade preservation, number backfill, old OTP invalidation and seed preservation.
- `AcademicRepository.Tests/DatabaseAudit.cs` — read-only configured-database fingerprints and compatibility audit; no secret output.
- `MILESTONE3.5-VERIFICATION.md` — this report.

## Commands and evidence

Executed successfully from `H:\AI\AcademicRepostory` with .NET SDK 10.0.401 / EF 10.0.9:

```powershell
dotnet clean AcademicRepository.slnx -c Release
dotnet restore AcademicRepository.slnx
dotnet build AcademicRepository.slnx -c Release --no-restore
dotnet run --project AcademicRepository.Tests -c Release --no-build
dotnet ef database update --project AcademicRepository --configuration Release --no-build
dotnet ef migrations has-pending-model-changes --project AcademicRepository --configuration Release --no-build
dotnet ef migrations list --project AcademicRepository --configuration Release --no-build
dotnet run --project AcademicRepository.Tests -c Release --no-build -- --audit
```

This repository uses an executable test harness; `dotnet run` is the test command, not a claim about `dotnet test` discovery. Tests use uniquely named temporary SQL databases, deleting only their own fixtures. The audit mode reads the configured application database and never deletes it. The final run contains expected EF error logs from deliberate duplicate-key rejection tests; these are caught assertions, not failed tests.

Evidence files outside Git: `H:\AI\verification-clean.log`, `verification-restore.log`, `verification-build.log`, `verification-final.log`, `database-before.txt`, `database-after-migration.txt`, `database-after-startup.txt`. The final harness exits 0; 830 PASS assertions excluding the final aggregate message, zero uppercase FAIL assertions. Initial build attempts encountered sandbox permissions and a running Debug executable lock; Release verification avoided disturbing that app. A migration-test rowversion conversion assertion was corrected to compare bytes; the final populated upgrade test passes.

Applied migrations:

1. `20261006061443_InitialIdentity`
2. `20261006064402_AddDepartmentsAndUserProfiles`
3. `20261006071514_AddProjectSubmissions`
4. `20261006081334_AddOtpAuthenticationAndOricQec`
5. `20261006101559_AddStudentEmailDepartmentMapping`
6. `20261006110025_AddUniqueStudentNumber`

Forward upgrades were tested; destructive downgrades were not run. Unique-index creation fails on conflicting legacy identities rather than deleting them. The configured local database had no such duplicate conflicts. Migrations are explicitly applied before startup, not automatically reset on launch.

## Remaining manual checks / readiness

1. Correct the identified existing Student using its confirmed Student Number and institutional mailbox while preserving its Identity ID; verify OTP login and its existing submission.
2. Correct the preserved non-SMIU legacy account if still needed; confirm its intended role and password login.
3. Restart the user's normal application instance to load the reviewed code. The temporary verification instance was stopped; the pre-existing Debug process was not terminated.
4. Live Graph delivery was user-verified and skipped as requested. No additional agent send is required; if revalidating a deployment, exercise Student OTP and staff recovery with authorized mailboxes.

Application-code checks pass. The unresolved existing Student data migration prevents an unqualified readiness approval for the current installation. No further milestone work was performed.

MILESTONE 3.5 NOT VERIFIED — DO NOT PROCEED
