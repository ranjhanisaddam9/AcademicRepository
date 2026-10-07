# MILESTONE 4 VERIFICATION REPORT

Verified 2026-10-07 against the working tree and local SQL Server. Milestone 4 only: private project resources. Authentication, Student email mapping, staff management and existing ProjectSubmission were preserved. No Coordinator review or later milestone functionality was added. No commit/push was performed.

## Acceptance results

| Area | Result | Evidence |
|---|---|---|
| A. ProjectFile Model | PASS | Required metadata, length limits, independent rows, server-derived fields and unique storage keys. |
| B. ProjectSubmission Relationship | PASS | Restrictive one-to-many FK; original submission table retained; populated migration tests. |
| C. File Storage Abstraction | PASS | IFileStorageService StoreAsync/OpenReadAsync/DeleteAsync; filesystem operations confined to LocalFileStorageService. |
| D. Secure Non-Public Storage | PASS | Outside wwwroot; root validation; random keys; traversal rejection; static URL returns 404. |
| E. Upload | PASS | Authorized own-Draft uploads; PDF/DOCX/ZIP/PPTX fixtures; safe errors and compensation tests. |
| F. Multiple Resources | PASS | One-file-at-a-time workflow creates separate rows; duplicates retain distinct files; configurable count limit. |
| G. File Validation | PASS | Empty/oversized/disallowed/mismatched content/type rejected; server-derived MIME; configured limits. |
| H. Download Authorization | PASS | Own Draft/Submitted downloads; other-owner, mismatched department, staff, anonymous and unknown IDs denied. |
| I. Delete Authorization | PASS | Draft-only POST with antiforgery; success removes row and bytes; failure paths tested. |
| J. Student Ownership | PASS | CSC20F005 owner versus CSC20F006 attacker; direct ID manipulation tested. |
| K. Department Security | PASS | Current profile compared with submission; forged fields ignored; mismatch denies upload/download/delete. |
| L. Submission Freeze | PASS | Shared SQL parent-row locking plus rowversion; post-submit and concurrent upload/delete tests. |
| M. At-Least-One-Resource Rule | PASS | Empty draft rejected; upload then submit succeeds; race cannot leave newly Submitted package empty. |
| N. Database Migration | PASS | AddProjectFiles applied; fingerprints unchanged for existing data; no model drift. |
| O. Automated Tests | PASS | Full executable SQL/MVC regression harness: **995 passing assertions, 0 failing assertions**. |
| P. Previous Milestone Regression | PASS | Identity/roles/dashboard, department/staff management, submission flows, Student OTP/number mapping, Graph abstraction, staff recovery and ORICQEC. |

Counts are assertions, including fixture and antiforgery checks, not 995 independent test cases. Expected failure-injection/duplicate-key logs are not failed tests. Final process exit code: 0.

## Implemented workflow

1. Student signs in with institutional email and OTP; `CSC20F005@smiu.edu.pk` retains identity `CSC-20F-005` and Computer Science.
2. Create and save a Draft. Direct creation as Submitted is rejected, since a resource must first be associated with a saved submission.
3. Details and Edit display Resource Materials: resource type, optional description, file picker, upload action and resource list. Save pending metadata edits before uploading; uploads redirect to Details and invalidate stale edit tokens.
4. Upload one file per request, progressively adding resources. Each file has its own ProjectFile row and random physical name.
5. Own Draft resources may be downloaded or deleted. To delete a whole draft, first remove its resources; this avoids silently orphaning stored files.
6. Submit requires at least one ProjectFile. Submitted packages show a read-only resource list and authorized downloads. Upload/delete/metadata mutation are blocked.

The complete requested acceptance sequence was automated: CSC20F005 login, final-year-project Draft, FinalReport.pdf / SourceCode.zip / Presentation.pptx uploads, download with original filename, delete presentation and confirm bytes/row removed, re-upload, submit, then reject upload/delete/edit while permitting download. Tests use synthetic signature/container fixtures and do not claim Office/PDF rendering validation. Test email delivery is simulated; no live Graph message was sent.

## ProjectFile schema

| Field | SQL type / constraints |
|---|---|
| Id | int identity, primary key |
| ProjectSubmissionId | int required FK, restrictive delete, indexed |
| OriginalFileName | nvarchar(255), required; sanitized basename |
| StoredFileName | nvarchar(64), required; unique index; opaque random key |
| ContentType | nvarchar(150), required; derived from allowed format |
| FileExtension | nvarchar(10), required; canonical lowercase |
| FileSize | bigint, required; server-parsed upload length |
| ResourceType | int; server-validated ProjectResourceType enum |
| Description | nvarchar(1000), nullable |
| UploadedAt | datetime2, UTC, server-assigned |
| UploadedByUserId | nvarchar(450), required Identity FK, restrictive delete, indexed |

ProjectSubmission has `ICollection<ProjectFile> ProjectFiles`. Resource types: Project Report, Research Paper, Source Code, Presentation, Dataset, Supporting Document, Other Resource. File bytes are not stored in ProjectSubmission or the database.

## Storage and limits

Default local directory:

`H:\AI\AcademicRepostory\AcademicRepository\App_Data\ProjectFiles`

Relative RootPath values resolve against the application content root. Root validation rejects wwwroot/descendants and symlink/junction ancestors. Keys are random GUID text plus an allowlisted extension. Stored keys and paths are never included in Student view models or downloads; filesystem paths are not constructed from original filenames.

```json
"FileStorage": {
  "RootPath": "App_Data/ProjectFiles",
  "MaxFileSizeMB": 50,
  "MaxFilesPerSubmission": 10
}
```

- Maximum file: **50 MB**, implemented as 50 × 1024 × 1024 bytes.
- Maximum resources per submission: **10**.
- Kestrel/IIS managed request-body limits and multipart limits use the configured maximum plus 1 MiB multipart overhead. External reverse-proxy/native IIS request limits must also allow that size.
- Integration tests deliberately use **1 MB / 3 resources** to exercise the same configurable limits without allocating large fixtures.
- Allowed extensions, centrally defined in `ResourceFileValidator.ContentTypes`: `.pdf`, `.doc`, `.docx`, `.ppt`, `.pptx`, `.xls`, `.xlsx`, `.csv`, `.txt`, `.zip`, `.jpg`, `.jpeg`, `.png`.
- Explicit rejection tests cover `.exe`, `.dll`, `.bat`, `.cmd`, `.ps1`, `.sh`, `.com`, `.scr`, `.msi`, `.aspx`, `.cshtml`, `.php`; all non-allowlisted extensions are rejected.
- PDF/JPEG/PNG/OLE formats receive header checks. ZIP/OpenXML require a ZIP header and readable archive metadata; DOCX/PPTX/XLSX require expected package entries. Archive contents are never extracted; excessive entry counts are rejected. TXT/CSV require valid UTF-8 without NUL bytes. Browser Content-Type is ignored.
- Signatures are basic format validation, **not malware scanning**, full document validation, or proof that an archive contains no scripts/macros.

## Authorization, consistency and concurrency

`ProjectFilesController` requires Student. Mutation endpoints use global MVC antiforgery validation. Route IDs are explicitly bound from routes; they are lookup targets only, never authorization. Current Student ID comes from Identity. `ProjectFileService` centralizes owner and current-department checks before access. Downloads use attachment disposition with the original sanitized name, `nosniff` and `no-store`.

Upload/delete and existing submission edits/deletes acquire the same SQL `UPDLOCK, HOLDLOCK` on the parent row inside a transaction. File mutations update the parent's rowversion. This serializes count checks and state transitions across application processes using the same database. Concurrent uploads cannot exceed the limit. Concurrent submit versus upload/delete permits one mutation and rejects the stale/frozen operation.

Upload order: validate authorization/state/metadata/content, store bytes, insert ProjectFile, commit. Storage failure creates no successful row. Save failure rolls back and attempts removal of the new file. If commit confirmation itself fails, the file is retained and its key logged for reconciliation rather than risking deletion of committed data.

Delete order: authorize and lock, delete metadata and commit, then delete bytes. A database failure preserves the file. A physical deletion failure returns an explicit 503 cleanup error and logs the opaque key; it does not claim full success. Remaining bytes have no authorized download record and are outside the public root.

SQL and local storage cannot share an atomic transaction. A process crash or storage failure can therefore leave inaccessible orphan files. Administrators can reconcile keys on disk against `ProjectFiles.StoredFileName` during a maintenance window; preserve/review recently written or uncertain-commit files before deleting anything. Missing referenced files are reported safely on download. No automatic cleanup worker or audit-trail feature was introduced.

## Database migration and preservation

Created and applied: **`20261007045246_AddProjectFiles`**. It creates only ProjectFiles, indexes and foreign keys. It does not recreate ProjectSubmission, reset a database, or remove old migrations. There are nine applied migrations; EF reports no pending model changes. Downgrade was not run.

Before/after audit of the configured database:

| Data | Count before/after | SHA256, unchanged |
|---|---:|---|
| Original user/profile fields | 5 / 5 | `A21EED0E81BCB3CC88509B3F4156DCF442FF6EE2E01FDD7D6609CD9CB697CB5C` |
| Departments | 8 / 8 | `8C0789F92A278539DA96183D1F55C44C1871A52C16440403316E126011589A4B` |
| Submissions, all fields | 2 / 2 | `46B6443B0CCFF27993D188654CE5221D363575F987E220D57A5A8F6A373A5C2C` |
| User-role relationships | 5 / 5 | `DF72275E4F923948123C0830C0B7F48751DF7DF15FF1C05974B3A5B169C84C85` |

Existing **Submitted projects without resources: 2**:

- ID **1**, title **Test 1**.
- ID **2**, title **Test 1**.

Both remain Submitted and unmodified, with zero invented files. The new minimum-resource rule applies only to a new Draft-to-Submitted transition. A separate populated temporary-database upgrade confirms preserved user, department, submission and rowversion data, and no fabricated ProjectFiles.

## Tests added and executed

`Milestone4Checks.cs` adds:

- Corrected Student identity/department acceptance and the full three-resource workflow.
- PDF/DOCX/ZIP/PPTX upload; duplicate original filenames with distinct physical keys.
- Empty, oversized, unsupported/executable/script, mismatched signature/archive, invalid enum and binary-text rejection.
- Sensitive-field overposting and traversal-style original filename sanitation.
- Own versus other-owner/mismatched-department/anonymous/all-staff-role access.
- Own Draft and Submitted downloads, attachment filename, safe metadata, no public static URL, unknown ID and missing bytes.
- POST-only deletion, upload/delete antiforgery and actual physical removal.
- Maximum count under concurrent uploads; upload/submit and last-resource-delete/submit races.
- Storage-root/traversal rejection and safe draft deletion with resources.
- Injected storage upload failure, database upload failure with cleanup, database deletion failure, and physical deletion failure with explicit orphan handling.

Milestone 3 regression fixtures now upload resources before submitting. Creation without resources is explicitly rejected. The existing profile-transfer fixture has a real test resource. Previous email, OTP, password rejection, all-role staff recovery, role/route authorization and ORICQEC checks still run.

Executed successfully:

```powershell
dotnet clean AcademicRepository.slnx -c Release
dotnet restore AcademicRepository.slnx
dotnet build AcademicRepository.slnx -c Release --no-restore
dotnet run --project AcademicRepository.Tests -c Release --no-build
dotnet ef migrations has-pending-model-changes --project AcademicRepository --configuration Release --no-build
dotnet ef database update --project AcademicRepository --configuration Release --no-build
dotnet run --project AcademicRepository.Tests -c Release --no-build -- --audit
```

Build: **0 warnings, 0 errors**. The executable test harness, not `dotnet test` discovery, is the test command. Fixtures use uniquely named temporary SQL databases and private directories, cleaned up after tests. The real application database audit is read-only.

Standalone startup against the local application database succeeded at `http://127.0.0.1:5199`; Student Sign In returned HTTP 200 with the corrected email example. Private storage initialized successfully. This temporary HTTP smoke-test instance was stopped; the existing user's app process was not terminated. The HTTPS redirect warning on that HTTP-only smoke URL does not replace HTTPS deployment testing.

Evidence logs: `H:\AI\milestone4-clean.log`, `milestone4-restore.log`, `milestone4-build.log`, `milestone4-final.log`, `milestone4-migration.log`, `milestone4-startup.log`, `milestone4-db-before.txt`, `milestone4-db-after.txt`. `git diff --check` passed.

## Files created

Paths relative to `H:\AI\AcademicRepostory`:

- `AcademicRepository/Models/ProjectFile.cs`
- `AcademicRepository/Services/LocalFileStorageService.cs`
- `AcademicRepository/Services/ResourceFileValidator.cs`
- `AcademicRepository/Services/ProjectFileService.cs`
- `AcademicRepository/Services/SubmissionLock.cs`
- `AcademicRepository/Controllers/ProjectFilesController.cs`
- `AcademicRepository/ViewComponents/ProjectResourcesViewComponent.cs`
- `AcademicRepository/Views/Shared/Components/ProjectResources/Default.cshtml`
- `AcademicRepository/Views/ProjectFiles/ResourceError.cshtml`
- `AcademicRepository/Migrations/20261007045246_AddProjectFiles.cs`
- `AcademicRepository/Migrations/20261007045246_AddProjectFiles.Designer.cs`
- `AcademicRepository.Tests/Milestone4Checks.cs`
- `MILESTONE4.md`

## Files modified for Milestone 4

- `.gitignore` — exclude private uploaded resources.
- `AcademicRepository/Models/ProjectSubmission.cs` — navigation collection only.
- `AcademicRepository/Data/ApplicationDbContext.cs` — ProjectFiles mappings.
- `AcademicRepository/Migrations/ApplicationDbContextModelSnapshot.cs` — additive schema.
- `AcademicRepository/Program.cs` — storage DI/options, body limits, storage startup validation.
- `AcademicRepository/appsettings.json` — storage configuration.
- `AcademicRepository/Controllers/StudentSubmissionsController.cs` — resource requirement/shared locking/safe parent deletion.
- `AcademicRepository/Views/StudentSubmissions/Create.cshtml`
- `AcademicRepository/Views/StudentSubmissions/Edit.cshtml`
- `AcademicRepository/Views/StudentSubmissions/Details.cshtml`
- `AcademicRepository/Views/StudentSubmissions/Delete.cshtml`
- `AcademicRepository.Tests/Program.cs` — isolated storage/test configuration and suite invocation.
- `AcademicRepository.Tests/Milestone3Checks.cs`
- `AcademicRepository.Tests/Milestone35Checks.cs`
- `AcademicRepository.Tests/MigrationUpgradeChecks.cs`
- `AcademicRepository.Tests/DatabaseAudit.cs` — legacy empty-package reporting.
- `README.md` — current milestone and workflow.

Earlier uncommitted milestone changes remain in the checkout and were preserved.

## Known limitations and production recommendations

- No malware scanner exists. Add scanning between validation and permanent storage before accepting untrusted production resources. Format checks do not detect viruses, malicious macros, or unsafe archive contents.
- Local files require a persistent private volume, restricted service-account permissions and coordinated database/file backups. Multiple app hosts would need shared storage or a replacement storage provider; local disk failover was not tested.
- Disk/database operations are not atomic; the documented orphan reconciliation remains necessary after exceptional failures. File cleanup failures return safe user-facing errors and log keys without binary data or physical paths.
- Align native IIS/reverse-proxy request limits and timeouts with application limits; use HTTPS. Configure disk quotas/monitoring appropriate to expected usage.
- Whole-draft deletion requires removing its resources first. TXT/CSV currently require UTF-8; macro/legacy Office files receive only basic container signature checks.
- The pre-existing legacy Student `ranjhanisaddam@smiu.edu.pk` still lacks a derivable StudentNumber and remains preserved, as does the legacy non-SMIU account. The current canonical CSC Student account is present with `CSC-20F-005`. No identity-data repair was guessed during Milestone 4.
- Graph live delivery was previously confirmed by the user. This run used fake delivery and retained the Development-only recipient override. Remove the override outside local testing; its production prohibition remains tested.

Milestone 4 code, migration and regression acceptance checks pass. Existing legacy empty packages remain intact as explicitly required. Coordinator review, approval/rejection, repository/search, reporting, evaluator/marks, notifications and later milestones were not implemented.

MILESTONE 4 VERIFIED — READY FOR MILESTONE 5
