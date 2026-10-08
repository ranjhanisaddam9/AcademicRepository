# ACADEMICREPOSITORY SECURITY & PERFORMANCE AUDIT REPORT

Audit scope: Milestones 1 through 10, as requested. This review did not implement a new business workflow or start UI/Evaluator work. Existing M10 edits were already present in the working tree before this audit; no commit was created.

## A. Executive summary

- **Security: PASS WITH WARNINGS.** One high-impact authorization/business-boundary defect was found and fixed in code: a forged Admin staff-edit POST could convert a staff account into a Student without student OTP onboarding. A regression check was added, but SQL-backed tests could not run in this environment.
- **Performance: MEDIUM.** Three confirmed avoidable query/memory costs were reduced. No representative SQL benchmark or query plan could be measured.
- **Code quality: MEDIUM.** The controller/service/data/UI separation is generally understandable and DI lifetimes are coherent. The integration suite is a console executable rather than a discoverable `dotnet test` suite, and broad controllers/services need eventual focused extraction only when changes are required.
- **Release/data caveat:** previous audit evidence recorded three submissions, three file rows, and four review rows before the M10 database update, then zero of those rows afterward while three physical files remained. Cause was not established. This run did not reconnect to or modify the configured database. Treat that discrepancy as an unresolved data-integrity incident and investigate against a verified backup before release.

## B. Vulnerabilities

### SEC-001 — Admin could convert staff into Student without verified onboarding

- **Category / severity:** Broken access control / onboarding bypass — High, fixed in source; regression execution pending.
- **Affected component:** `AcademicRepository/Controllers/UsersController.cs`, staff `Edit` POST.
- **Exploitability / impact:** The UI omitted Student as an available staff-edit role and editing an existing Student was blocked, but a logged-in Admin could forge `Role=Student` and a structurally valid Student email in the staff-edit POST. The action accepted the Student role, changed identity fields/department, and assigned the role without OTP verification. This violated the verified self-onboarding boundary.
- **Fix:** Server rejects Student as a target role for staff edits. Existing Student creation remains rejected by Admin User Management. Added a regression case that forges the conversion and checks role/email stay unchanged.
- **Test:** Build passes. The new regression test is in `Milestone2Checks.cs` but could not execute because the test SQL Server TLS handshake fails here.

### SEC-002 — Admin diagnostics did N+1 lookups and loaded all active files

- **Category / severity:** Performance / resource exhaustion risk — Medium, fixed in source.
- **Affected component:** `AcademicRepository/Services/AdminSystemService.cs`.
- **Exploitability / impact:** Opening Admin diagnostics loaded all Students and performed a department query for each Student, then loaded every active file record before opening them sequentially. As data grew, this multiplied database round trips and memory use on an Admin request.
- **Fix:** Department mappings are loaded once from the Department table; Students and active file metadata are processed in keyset batches of 200. The issue cap remains 500.
- **Test:** Build passes; existing M10 diagnostics integration coverage could not run because of SQL Server TLS failure.

### SEC-003 — Admin user listing did one role query per displayed account

- **Category / severity:** Performance — Low/Medium, fixed in source.
- **Affected component:** `AcademicRepository/Controllers/UsersController.cs`, paged listing.
- **Exploitability / impact:** The page already loaded role assignments as a set, then called `IsInRoleAsync` once per account to determine Student ID display, adding up to 25 avoidable database queries per page.
- **Fix:** Build a per-page role lookup from the already-fetched assignments.
- **Test:** Build passes. Existing M10 user-list coverage is blocked with the SQL-backed suite.

### SEC-004 — Application lacked consistent browser security headers

- **Category / severity:** Defense-in-depth — Low, fixed in source.
- **Affected component:** `AcademicRepository/Program.cs` middleware.
- **Impact:** Pages did not consistently set clickjacking, MIME-sniffing, referrer, and browser-feature protections.
- **Fix:** Added `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`, a restrictive `Permissions-Policy`, and CSP directives for framing, plugins, base URLs, and form actions. A strict `script-src` policy is deliberately not claimed because `Views/Users/Form.cshtml` contains an inline role-toggle script; see remaining risks.
- **Test:** Added an authenticated-response header assertion in `Milestone10Checks.cs`. Build passes; assertion awaits SQL-backed test execution.

### SEC-005 — Upload endpoint had no abuse throttle

- **Category / severity:** Authenticated resource exhaustion — Medium, mitigation added; runtime verification pending.
- **Affected component:** `Program.cs`, `ProjectFilesController.Upload`.
- **Exploitability / impact:** An authenticated Student could make repeated large uploads; per-file and per-submission caps existed, but no request-rate cap applied.
- **Fix:** Added a 20-per-hour fixed-window limit partitioned by authenticated user ID (IP fallback) and applied it to uploads. Existing per-file size and per-submission file limits remain in place.
- **Test:** No SQL-backed rate-limit test could run in this environment. A global per-user storage quota is still absent.

### SEC-006 — Production authentication cookies followed request scheme

- **Category / severity:** Cookie transport hardening — Medium, fixed in source.
- **Affected component:** `AcademicRepository/Program.cs`, Identity cookie configuration.
- **Exploitability / impact:** `SameAsRequest` could omit the Secure flag if production traffic reached the app over HTTP behind a TLS-terminating proxy that does not forward scheme metadata.
- **Fix:** Non-Development environments now use `CookieSecurePolicy.Always`; Development retains HTTP-friendly behavior.
- **Test:** Build passes. Runtime cookie attributes were not verified because the SQL-backed application host could not start.

No other Critical/High security vulnerability was confirmed by source inspection. The above statement is limited by the unavailable SQL-backed regression run.

## C. SQL injection

- Application raw SQL location: `Services/SubmissionLock.cs` uses `FromSqlInterpolated` for row locks. EF Core parameterizes the submission/user/department IDs.
- Other application queries use EF LINQ. Search fields are LINQ `Contains` expressions and therefore parameterized.
- Dynamic repository sort uses an enum `switch` whitelist in `RepositoryService.OrderedReviews`; client text is never appended to SQL.
- Static migration backfill SQL and test-only `FromSqlRaw` statements were inspected; they do not concatenate user-controlled values.
- No `Html.Raw`, `HtmlString`, or unsafe DOM HTML insertion was found in Razor/application scripts.
- **SQL INJECTION STATUS: PASS by code inspection.** Payload integration tests did not run because of the database TLS failure.

## D. Access control

- Student submissions and file operations derive the current Student ID from the authenticated principal and scope the service queries to that identity.
- Coordinator review and file access check the Coordinator is active, assigned a Department, and the target submission/file belongs to that Department.
- DepartmentHead repository access is Department-scoped in `RepositoryService`; a null Department does not grant global scope.
- Institution-wide repository/report/export operations require ORICQEC role in the service scope resolver. Academic modification remains in Coordinator POST actions.
- Admin management controllers require Admin. Admin is not authorized on Coordinator decision actions.
- `AutoValidateAntiforgeryTokenAttribute` is registered globally; mutating actions use POST. Logout is POST. Return URLs are restricted through `Url.IsLocalUrl` and `LocalRedirect`.
- Existing harness cases cover cross-Student, cross-Department, role, CSRF, and open-redirect cases. **Those HTTP/database cases were not executed in this run.**

## E. File security

- Upload uses a fixed extension/content-type allowlist, nonempty/size limits, max active files per submission, normalized basename, server-generated random storage key, and signature checks for supported file types. Double extensions are judged by the final extension and content signature; executable extensions are not allowed.
- Storage root is outside `wwwroot`; root/ancestor reparse points and file reparse points are rejected. Storage keys must match a server-generated GUID-plus-extension pattern and callers never submit filesystem paths.
- Student, Coordinator, and approved-repository downloads first scope database metadata and then open the generated storage key. Downloads use `FileStream`, attachment names, `nosniff`, and `no-store`.
- Tests in the existing harness cover dangerous extensions, invalid signatures, path traversal keys, ownership and department downloads, and concurrent upload/submission behavior, but could not execute in this run.
- **Malware scanning is not implemented.** Signature checks identify common file formats; they do not detect malware or all polyglot/document exploits. Keep uploaded files private and add malware scanning before production if threat requirements demand it.

## F. Authentication

- Student and staff OTPs use `RandomNumberGenerator`, HMAC-SHA256 with a protected key, Data Protection-protected challenge tokens, expiry, one-time consumption, attempt limits, resend cooldown, per-user request limits, and per-IP authentication rate limits. Default expiry is ten minutes. OTP delivery failure consumes the code and is not logged as successful delivery.
- Production startup requires a configured OTP HMAC key; ephemeral key generation is Development-only. Development OTP logging is forbidden outside Development.
- Staff password authentication uses ASP.NET Core Identity. Student password login is rejected by `ActiveUserSignInManager`; inactive accounts are rejected at login/cookie validation. Identity failed-attempt lockout is configured for five attempts.
- Auth cookies are HttpOnly and use `SecurePolicy.Always` outside Development; HTTPS redirection/HSTS apply, and ASP.NET defaults supply SameSite behavior. Deployments behind a TLS-terminating reverse proxy still need trusted forwarded headers for correct HTTPS redirection and scheme-aware URL generation. No proxy topology was available to verify.
- Login, OTP, and recovery POSTs use an IP rate-limit policy. Upload now has the additional per-user limit described above. Search remains paginated; reports/CSV are authorized and bounded in memory, but there is no separate search/report rate policy.
- OTP endpoint privacy is mostly uniform in returned challenge shape; exact timing and delivery side effects were not measured.

## G. Performance

- **Fixes applied:** Admin diagnostics now batch Students/files and use a single department map; Admin user listing no longer asks Identity for each row; shared layout operational settings now load values/updater summary in one query rather than three.
- Major submission/review/repository lists use database filtering, projection, and pagination. CSV writes ordered batches of 200 rows to a temporary file rather than buffering the complete export in RAM. Downloads stream files.
- Remaining costs: `CountOrphansAsync` builds a set of stored names in memory and enumerates the storage directory; repository availability requires filesystem checks for files in each displayed version. These are bounded by current storage/database size but scale with it.
- Query patterns use `DepartmentId`/`Status` for review queues and `Status` plus review/version links for repository selection. Existing indexes include StudentId+CreatedAt, unique student number, department mapping and review/version constraints. No query plans were available to justify new composite indexes.
- Search currently uses `Contains`/`LIKE`; at scale this can scan many rows. Full-text search should be considered in its planned milestone.
- **Benchmark: NOT RUN.** The required 10,000-submission/50,000-file/5,000-user SQL benchmark and query plan inspection require a working isolated SQL Server connection. No timing values are fabricated.

## H. Code quality

- **Architecture map:** MVC controllers receive/validate requests; focused services implement submission, review, version, file, repository/report, OTP, department, and admin settings operations; `ApplicationDbContext` is the EF/Identity data layer; Razor views and Bootstrap are UI; `Program.cs` owns middleware, DI, Identity, rate limiting, and startup seeding.
- **Positive findings:** No unnecessary repository/CQRS/MediatR layer; constructor DI is used; DbContext/services are scoped, file storage and time/hasher dependencies are safe singleton candidates; Graph HTTP client is created via `IHttpClientFactory`; request-path EF/network work is predominantly async; writes use ViewModels/explicit mapping rather than binding sensitive EF entities.
- **Maintainability risks:** `AccountController`, `UsersController`, `AdminSystemService`, `RepositoryService`, and `AuthenticationCodeService` have broad responsibilities and deserve focused changes only when requirements evolve. The Razor layout reads runtime settings on every request (now one SQL round trip); evaluate caching only after deciding how quickly operational settings must take effect.
- **Error handling/logging:** user-facing database/file errors are generic; logs use user/resource identifiers and exception types for most failure paths. OTP values are logged only by the explicit Development-only mail mode. No user-controlled Razor output bypass was found.
- **Concurrency/transactions:** review transitions and Student file/submission operations use SQL row locks, row versions, unique constraints and transactions. Existing race tests cover important workflows but were unavailable to execute here.
- **Test-runner caveat:** `AcademicRepository.Tests` is an executable harness and does not reference `Microsoft.NET.Test.Sdk` or a unit-test adapter. `dotnet test` exits successfully with no tests discovered; execute the harness with `dotnet run --project AcademicRepository.Tests`. This is a CI false-green risk and should be corrected in a future maintenance task.

## I. Refactoring

1. **Admin Student conversion guard** — Before: staff edit accepted a forged Student role. After: server rejects Student target role. Reason: enforce verified self-onboarding. Risk: low; staff account workflows unaffected. Test added; SQL execution pending.
2. **Admin diagnostics batching** — Before: per-Student mapping queries and all active file metadata loaded at once. After: one department mapping and keyset batches of 200. Reason: remove N+1 and unbounded materialization. Risk: low; same categories and 500 issue cap. Build passed; SQL test pending.
3. **Admin list role projection** — Before: role lookup per account. After: reuse paged role query. Reason: remove avoidable Identity round trips. Risk: low. Build passed; SQL test pending.
4. **Settings query consolidation** — Before: settings, max timestamp and updater fetched separately. After: one projection/round-trip. Reason: shared layout invokes settings each page. Risk: low. Build passed; SQL test pending.
5. **Security headers, cookie, and upload controls** — Added defense-in-depth headers, per-user upload rate policy, and Secure cookies in non-Development. Risk: low; restrictive script CSP deferred due inline script compatibility. Build passed; HTTP behavior pending.

## J. Dependencies

- Runtime packages: `Microsoft.AspNetCore.Identity.EntityFrameworkCore` 10.0.9, `Microsoft.EntityFrameworkCore.SqlServer` 10.0.9, `Microsoft.EntityFrameworkCore.Design` 10.0.9. Test project also references `Microsoft.AspNetCore.Mvc.Testing` 10.0.9. Bootstrap 5.3.8 CSS is vendored locally; no other JavaScript package was identified.
- Restore completed from local assets/config and Release build passed with 0 warnings/0 errors after disabling NuGet advisory retrieval for the offline restore.
- **Vulnerability/outdated package feed audit: NOT AVAILABLE.** NuGet.org service index access is blocked in this environment; `dotnet list package --vulnerable/--outdated` could not retrieve advisory/current-version data. Do not interpret that as a clean dependency audit. No upgrades were made.
- No Docker/deployment manifest was present in the repository inventory.

## K. Secrets

- **Current tracked configuration: NOT FOUND.** appsettings contains a Windows Integrated Security SQL Server connection (no username/password), Graph credentials are absent, and development secrets are configured externally. `.gitignore` excludes private `App_Data/ProjectFiles` and build/user files.
- **Targeted Git-history scan of appsettings and launchSettings config: NOT FOUND** for non-placeholder `ClientSecret`, `Password`, or `Pwd` values. This was a targeted configuration-history scan, not a credential scan of every historical blob.
- User Secrets and deployment variables were not read. No values are reproduced here.

## L. Database

- Inspected 13 EF migration source files. Migration `Up` bodies add/alter schema, create constraints/indexes, and perform documented OTP/student-number backfills; no migration `Up` was found that drops/recreates the database or deletes user, department, submission, file, or review rows. `Down` methods naturally remove objects introduced by their migrations.
- Current model includes unique normalized email, student number, department code/name/email keyword, file key, version number, and review round/active-pending constraints; relevant foreign keys use restrictive deletes.
- **Live migration status: NOT VERIFIED in this run.** The test fixture's SQL Server connection could not complete the server-required TLS handshake. No migration or application startup was attempted against the configured database.
- **Known data discrepancy:** prior audit snapshots at `H:\AI\database-before.txt` and `H:\AI\database-after-startup.txt` recorded the project database changing from 3 submissions/3 file rows/4 review rows to zero of those rows, while users/departments/roles remained and three physical files remained. Cause is unknown; no recovery or mutation was attempted. Verify a trusted backup and migration history before proceeding.

## M. Test results

- **Unit / isolated tests:** PASS — 32 Microsoft Graph/email configuration and sender checks, including failure handling, Development-only redirect/log behavior, and no credential/response leakage. Command: `dotnet run --project AcademicRepository.Tests -c Release --no-build -- --email-only`.
- **Integration:** BLOCKED — SQL/MVC harness stops in `MigrationUpgradeChecks` before database-backed cases. SQL Server error: server requires encryption, but the current machine/runtime reports it cannot support the required TLS connection.
- **Security:** PARTIAL — code inspection completed; new conversion/header regressions were added; SQL-backed IDOR/CSRF/file/rate/concurrency checks did not execute.
- **Performance:** NOT RUN — no SQL benchmark/query plans.
- **Regression:** BLOCKED — full M1–M10 console harness cannot run without SQL Server TLS. `dotnet test` itself discovers zero tests because this project uses a console harness.
- **Build:** PASS — Release solution build, 0 warnings, 0 errors. Restore used existing/local packages and `NuGetAudit=false` due blocked NuGet.org; package advisory verification remains unavailable.
- **Total tests:** 32 checks passed in the isolated email subset; SQL-backed test count/result unavailable (harness stopped before it began).

## N. Remaining risks

- Unresolved project-database data discrepancy described in section L; establish cause and restore/validate data before release.
- SQL-backed tests, live migration check, security regression matrix, and benchmark await an environment with a compatible SQL Server/TLS client.
- NuGet vulnerability/outdated metadata unavailable offline.
- No antivirus/malware scanning or global per-Student storage quota.
- No strict script CSP until the inline role-toggle script is externalized or nonce-based.
- TLS reverse-proxy forwarded-header configuration is deployment-dependent and was not inspected (Secure cookie transport is forced in non-Development regardless).
- `dotnet test` can appear green while discovering zero tests.

## O. Priority remediation plan

- **P0 — before release:** Investigate the recorded missing submission/file/review rows using verified backups and database audit logs. Do not run recovery against the current database without a validated restore plan.
- **P1 — before next milestone/release gate:** Rerun the console integration harness in a SQL Server environment with working TLS against its generated isolated database; specifically execute the new staff-to-Student rejection and security-header checks. Run package vulnerability/outdated scans with reachable NuGet advisory feeds. Convert the console checks to a test adapter or make CI fail when zero tests are discovered.
- **P2 — before production:** Externalize/nonce the inline script and deploy a tested strict CSP; validate forwarded headers/cookie Secure behavior for the actual proxy; consider storage quotas and malware scanning requirements; inspect execution plans before adding composite indexes.
- **P3 — future scale work:** Benchmark search and repository/report flows at the requested data volumes; assess SQL Server full-text search and safe caching for department/reference data.

## P. Final status

Important qualification: source fixes compile, but the new security regression checks and the complete Milestone 1–10 SQL-backed suite remain unverified until SQL Server TLS works. The recorded database discrepancy must be investigated before a release decision.

SECURITY AUDIT PASSED WITH WARNINGS — REVIEW FINDINGS BEFORE PROCEEDING
