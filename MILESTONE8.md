# MILESTONE 8 VERIFICATION REPORT

Verified on 7 October 2026 against the actual SQL Server application and the isolated SQL/MVC regression harness. Milestone 8 is implemented; no Milestone 9 functionality was added.

## A–Q verification

| Requirement | Result | Evidence |
| --- | --- | --- |
| A. DepartmentHead Dashboard | PASS | Authenticated MVC dashboard renders the assigned department, type totals, current academic year, year/semester summaries and recent approvals. |
| B. Department-Scoped Repository | PASS | SQL queries require the current Head's database DepartmentId. CSC and BUS fixtures see only their respective departments. |
| C. Approved-Only Enforcement | PASS | Draft, Submitted, UnderReview, Rejected and Revision details are rejected; counts exclude them. Incomplete approvals are withheld. |
| D. Approved Version Resolution | PASS | Shared approved-review resolver returns approved Version 2 despite rejected Version 1 and an additional unreviewed Version 3. |
| E. Title Search | PASS | Database-side title searches return authorized matches and exclude another department's exact title. |
| F. Abstract/Keyword Search | PASS | Snapshot abstract and keyword searches pass, including case-insensitive and whitespace-trimmed terms. |
| G. Student Search | PASS | Full name and canonical StudentNumber search pass, including lowercase input. |
| H. Supervisor Search | PASS | Supervisor matches work in both general search and the dedicated filter. |
| I. Filters | PASS | Project type, academic year, semester and supervisor combine with search; year options exclude other departments. Invalid inputs return 400. |
| J. Sorting/Pagination | PASS | Newest/oldest approval and title A–Z/Z–A sort in SQL; 25 scoped records paginate 20/5 without overlap. Links retain criteria, and excessive page numbers are clamped. |
| K. Repository Details | PASS | Approved snapshot metadata, Student ID/email, approval details and approved resources render through authenticated MVC. |
| L. Approved Resource Download | PASS | Approved-version download succeeds; rejected-version, incorrect file/version and cross-department requests fail. Private storage remains outside wwwroot. |
| M. Cross-Department Protection | PASS | Both directions of CSC/BUS ID manipulation fail for details/downloads. A forged department filter does not expand results. |
| N. Department Statistics | PASS | Exact department totals/type/year/semester/current-year counts and recent ordering checked against controlled SQL fixtures; BUS records do not affect CSC results. |
| O. Read-Only Enforcement | PASS | Head-only POST attempts to review, approve, reject, edit, delete, revise and upload are denied. GET-only repository routes reject POST. Academic row/key/rowversion fingerprints remain identical. |
| P. Automated Tests | PASS | Final run: **1,499 passing assertions, zero failures**, excluding the aggregate success line. |
| Q. Regression Tests | PASS | Full Milestones 1, 2, 3, 3.5, 4, 5, 6, 7 and 8 SQL/MVC suite passes. |

## Build, runtime and migrations

Cleaned Release, restored packages and built the complete solution: **0 warnings, 0 errors**. Release avoids locking the user's running Debug executable. A separate Development instance on port 5199 started successfully, connected to SQL Server and ran idempotent startup seeding. Login returned 200; anonymous DepartmentHead Dashboard and Repository returned 302 to login. The separate verification instance was stopped.

Executed from the repository root with .NET 10 available:

```powershell
dotnet clean AcademicRepository.slnx -c Release
dotnet restore AcademicRepository.slnx
dotnet build AcademicRepository.slnx -c Release --no-restore
dotnet run --project AcademicRepository.Tests -c Release --no-build
dotnet ef migrations has-pending-model-changes --project AcademicRepository --configuration Release --no-build
dotnet ef database update --project AcademicRepository --configuration Release --no-build
dotnet run --project AcademicRepository.Tests -c Release --no-build -- --audit
```

EF reports no pending model changes. All **11 existing migrations** are applied; database update applied no migrations. No entity, schema, migration or index changes were required. Existing foreign-key and review/version indexes are retained. Search uses bounded database queries suitable for this milestone; no large-production-dataset performance benchmark was performed, and contains searches are not full-text search.

Verification logs are outside the Git repository under `H:\AI`: `milestone8-clean.log`, `milestone8-restore.log`, `milestone8-build.log`, `milestone8-final.log`, `milestone8-audit-before.log` and `milestone8-audit-after.log`. `git diff --check` passed.

## Implementation

`IRepositoryService` now exposes `GetDepartmentRepositoryStatsAsync`. `RepositoryService` reloads the active institutional user and department, checks current database role membership, and gives DepartmentHead a department-wide scope. Missing DepartmentId denies access with administrator guidance, including login/session validation. Admin/ORICQEC membership alone never grants repository access or insights. Adding an unrelated role to a Head does not broaden the department boundary. Existing independently assigned roles retain their existing authorization semantics.

All repository operations reuse the existing `EligibleReviews` and approved-review version link. The service does not copy repository records or infer approval from the newest version. A valid approved parent, completed approval, matching version/owner/department/round, resource membership and metadata are required. Details/downloads validate the complete approved resource set and physical availability. Historical rejected resources are not exposed to Heads.

Head search runs through EF Core SQL Server over title, abstract, keywords, Student name/number and supervisor. Search values are parameterized, trimmed and length-limited; explicit `Latin1_General_100_CI_AS` collation makes these searches case-insensitive. Type/year/semester/supervisor restrictions apply before Count, OrderBy, Skip and Take. Existing Student/Coordinator title/keyword search behavior is preserved. Academic-year dropdown options derive only from authorized eligible snapshots, capped at 100 distinct years. Semester remains a metadata text filter; supervisor is a text filter, with no new entity.

Sorting supports newest approval by default, oldest approval, title A–Z and title Z–A, with deterministic ID tie-breakers. Pages contain at most 20 database-selected entries. Requested pages beyond the last page are clamped. Physical resource checks can withhold entries from a selected page; matching counts describe structurally eligible records and may exceed displayed entries when storage is unavailable. This existing Milestone 7 behavior is retained.

Statistics use scoped SQL Count/GroupBy against the same eligible approved reviews, including project type and submitted snapshot academic year/semester. Current academic-year count matches the snapshot academic-year label. The default year changes in July using the injected UTC clock; configure `Repository:CurrentAcademicYear` (environment variable `Repository__CurrentAcademicYear`) to use the institution's actual label. Recent approvals show up to five available entries from the newest 20-record page. Counts validate metadata/resource membership; physical availability is checked for displayed/downloaded entries rather than scanning all private files for every dashboard request. The dashboard states this distinction.

DepartmentHead controllers/routes are GET-only and role-protected; shared RepositoryController additionally delegates every operation to the authorized service. No mutation controls appear for Head-only users. Existing Coordinator, Student, historical-version and file-controller role/ownership protections remain enforced. The shared repository error link now explicitly targets RepositoryController so dashboard errors have a working return link.

## Tests added and defects fixed

`Milestone8Checks.cs` adds real SQL/MVC fixtures for two departments, Head authentication, dashboard HTML/navigation, exact statistics, all requested search fields, combined filters, all four sorts, scoped year options, pagination, validation, approved-version selection, secure downloads and status exclusion. Security checks exercise direct URL IDs, forged scope inputs, denied mutations with unchanged academic fingerprints, missing department, inactive accounts, session revocation and unrelated additional roles. Fixtures and physical test resources live in the harness's temporary database/storage; the application database is not used for fixture mutations.

The Milestone 7 negative-role test was updated because DepartmentHead repository access is now intentional; Admin and ORICQEC remain rejected. Existing review/version restrictions on DepartmentHead remain tested. The full executable harness, including authentication, onboarding/OTP, staff recovery, user management, resource management, review concurrency, revision/versioning and approved repository tests, was executed.

An initial dashboard SQL query attempted to order a projected group record, which EF could not translate. Sorting was moved to the grouped SQL query before projection. Dashboard SQL/MVC and the complete suite passed after the fix. No outstanding Milestone 8 test failures remain.

## Data preservation and existing inconsistencies

Before/after read-only application-database audits match **exactly**, including row counts, SHA-256 row fingerprints, migration history and physical file hashes:

| Existing data | Count |
| --- | ---: |
| Identity users | 5 |
| Departments | 8 |
| Project submissions | 3 |
| User role assignments | 5 |
| Project files | 2 |
| Review decisions | 3 |
| Submission versions | 3 |
| Version/file memberships | 2 |

No duplicate departments, duplicate normalized emails, submission department mismatches or broken approval/version links were found. Existing decisions, version snapshots, IDs, role assignments and file contents are unchanged.

Known legacy data remains preserved:

- Approved project **ID 2, “Test 1”**, has approved Version 1 (database VersionId 2) but no version resources. It is safely withheld; no placeholder file or altered approval was created.
- One legacy Student account has a noncanonical email local part and no StudentNumber. It requires a deliberate administrator-led identity correction under the existing authentication rules, outside this milestone.
- One legacy non-institutional user remains blocked by existing institutional authentication validation.

These are existing data issues, not new Milestone 8 regressions. A Head in a department without eligible approvals will see an empty repository with appropriate guidance. To demonstrate populated screens manually, approve a real submission with resources through the existing Coordinator workflow.

Live Microsoft Graph delivery was not repeated because the user previously instructed us to skip it after their verification. Authentication regression tests use the existing fake/recording sender and cover failure handling; this milestone introduces no email configuration change. Graph secrets remain outside committed settings.

## Files created

- `AcademicRepository/Controllers/DepartmentHeadController.cs`
- `AcademicRepository/Views/DepartmentHead/Dashboard.cshtml`
- `AcademicRepository.Tests/Milestone8Checks.cs`
- `MILESTONE8.md`

## Files modified

- `AcademicRepository/Controllers/RepositoryController.cs`
- `AcademicRepository/Controllers/RoleControllers.cs`
- `AcademicRepository/Models/RepositoryViewModels.cs`
- `AcademicRepository/Program.cs`
- `AcademicRepository/Services/RepositoryService.cs`
- `AcademicRepository/Views/Account/Login.cshtml`
- `AcademicRepository/Views/Repository/Details.cshtml`
- `AcademicRepository/Views/Repository/Error.cshtml`
- `AcademicRepository/Views/Repository/Index.cshtml`
- `AcademicRepository/Views/Shared/_Layout.cshtml`
- `AcademicRepository.Tests/Milestone7Checks.cs`
- `AcademicRepository.Tests/Program.cs`
- `README.md`

## Manual acceptance checklist

1. Sign in with an active DepartmentHead staff account assigned to Computer Science; open Dashboard and Academic Repository.
2. Confirm counts/insights and results contain only eligible approved Computer Science submissions.
3. Search title, abstract, keyword, Student name/number and supervisor; combine filters and check sorting/paging.
4. Open Details and download an approved resource; verify metadata/resources belong to the review-linked approved version.
5. Try another department's detail/file IDs and Coordinator/Student mutation URLs; confirm denial.
6. Remove the Head's department or deactivate the account; verify the existing session is rejected and configuration guidance appears where applicable.
7. Repeat with another department and confirm Student/Coordinator repository and review/revision workflows remain unchanged.

Ready to proceed to Milestone 9 when explicitly instructed. No Milestone 9 development, commits or pushes were performed.

MILESTONE 8 VERIFIED — READY FOR MILESTONE 9
