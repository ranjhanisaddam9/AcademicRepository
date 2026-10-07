# MILESTONE 7 VERIFICATION REPORT

Verified 7 October 2026. The internal Approved Academic Repository is implemented over existing submission/review/version data. Milestone 8 and later functionality was not implemented. Changes remain uncommitted.

## A–P verification results

| Item | Result | Evidence |
|---|---|---|
| A. Repository Eligibility | PASS | Tests exclude Draft, Submitted, UnderReview, Rejected and Revision, even when an earlier approval exists. Broken approvals are withheld/reported. |
| B. Approved Version Resolution | PASS | Version 2 is resolved through its completed Approved review. Later unreviewed Version 3 and altered live metadata do not change repository content. |
| C. Approved Resource Resolution | PASS | Only approved Version 2 resource membership is returned. Rejected-only files, incorrect version IDs and unrelated resources are denied. |
| D. Repository List | PASS | Student own-project and Coordinator department lists, role navigation, academic fields and safe empty results tested. |
| E. Repository Details | PASS | Razor shows approved snapshot metadata, current owner identity, version/round, completion date, approval comments and safe file metadata. |
| F. Student Own-Project Access | PASS | Owner can view/download. Another Student in the same department cannot access details/resources or discover private counts. |
| G. Coordinator Department Access | PASS | Same-department approvals accessible; own department enforced before search/query projection. |
| H. Cross-Department Protection | PASS | Both CSC→BUS and BUS→CSC detail/download denial tested; manipulated DepartmentId filter cannot broaden scope. |
| I. Secure Resource Download | PASS | Authenticated attachment endpoints validate parent status, approved review/version and exact resource membership. Anonymous access denied; no paths/keys returned. |
| J. Basic Search | PASS | Title and keyword search tested; parameterized EF queries handle SQL-like input as text; reasonable input limits enforced. |
| K. Filters | PASS | Project type, academic year, semester and combined HTTP/service filters tested. |
| L. Pagination | PASS | SQL count/order/Skip/Take with 20-record pages; 25-record fixture gives disjoint 20/5 pages. Extreme page clamps safely. |
| M. Historical Version Integrity | PASS | Rejected history remains intact and accessible through existing authorized history pages; it is not exposed as approved repository content. |
| N. Repository Data Consistency | PASS | Missing version/resources, mismatched department/round, invalid dates, ambiguous approvals and missing physical files fail closed. Local broken record detected and withheld. |
| O. Automated Tests | PASS | Final executable SQL/MVC harness: **1,430 passing assertions, 0 failures**. Assertions are not separate test methods. |
| P. Regression Tests | PASS | Full Milestones 1, 2, 3, 3.5, 4, 5 and 6 suite passes alongside Milestone 7. |

## Build, runtime and database verification

Clean, package restore and Release build succeeded with **zero warnings and zero errors**. The updated application started against the configured SQL Server database on an isolated port. Login returned HTTP 200 and anonymous `/Repository` returned HTTP 302 to Login. The temporary instance was stopped. Restart the existing development app to load the new code. The isolated HTTP-only smoke instance emitted the expected missing HTTPS-port warning; normal HTTPS launch profiles are unchanged.

No migration or publication entity was required. EF reports no pending model changes; database update reports already up to date, with all eleven existing migrations applied. No schema/data reset occurred.

Before/after audits match for **5 users, 8 departments, 3 submissions, 5 role assignments, 2 ProjectFiles, 3 review decisions, 3 version snapshots and 2 version-resource memberships**. Original record fingerprints and both physical resource SHA256 fingerprints match. These checks compare the fresh Milestone 7 baseline, including the user's intervening review actions, rather than assuming the previous milestone's data is unchanged.

```powershell
dotnet clean AcademicRepository.slnx -c Release
dotnet restore AcademicRepository.slnx
dotnet build AcademicRepository.slnx -c Release
dotnet run --project AcademicRepository.Tests -c Release
dotnet ef migrations has-pending-model-changes --project AcademicRepository --configuration Release
dotnet ef database update --project AcademicRepository --configuration Release
dotnet run --project AcademicRepository.Tests -c Release -- --audit
```

Evidence logs: `H:\AI\milestone7-{clean,restore,build,final,model,migrations}.log` and `H:\AI\milestone7-db-{before,after}.txt`. Tests use isolated SQL Server databases and temporary resources; only those fixtures are modified/deleted. Live Microsoft Graph delivery was not retested, following the user's earlier instruction. Graph configuration and Student OTP/development redirect/staff recovery regression use the established recording/failure fakes.

## RepositoryService architecture and source of truth

IRepositoryService exposes approved-project list, details and download operations. RepositoryService performs role/account scope resolution, eligibility, search/filter/pagination, snapshot projection and storage availability checks. RepositoryController handles MVC binding/views and safe responses. There is no duplicated RepositoryProject, RepositoryEntry, publication status or academic metadata copy.

The centralized EligibleReviews query requires:

- Authorized parent ProjectSubmission with Status Approved.
- Exactly one Approved decision, completed at a valid UTC date; no Pending review.
- Linked SubmissionVersion belonging to the same parent, department and owner, with matching review round/version number and valid submitted/review date ordering.
- Required snapshot metadata and a nonempty resource membership whose files belong to the parent and have positive size.

The authoritative version is **Approved SubmissionReview.SubmissionVersionId**, not the latest version or live project fields. Academic title, abstract, keywords, project type, course, supervisor, academic year and semester come from the immutable snapshot. Owner name/StudentNumber are read from the linked Identity profile; department name is read through the snapshot's DepartmentId. Academic approval date is the Approved review's CompletedAt.

## Authorization and downloads

Both controller and service enforce authorization. Every service call reloads an active institutional Identity account, checks assigned department and verifies an allowed current role. Students get own approved records only, with matching current department. Coordinators get their department's approved records only. Missing department never becomes institution-wide scope. Admin, DepartmentHead and ORICQEC do not inherit repository access in this milestone.

Scope is a distinct input to the approved query, allowing future explicitly authorized DepartmentHead or institution-wide scope to be added without duplicating projection/search logic. Those scopes are not currently enabled. Existing DepartmentHead placeholder and ORICQEC protected dashboard remain unchanged.

Routes:

- `/Repository` — My Approved Projects / department Approved Repository.
- `/Repository/Details/{submissionId}` — approved snapshot details.
- `/Repository/{submissionId}/Versions/{versionId}/Files/{fileId}` — approved resource download.

IDs are route-bound. Downloads require the supplied version to equal the approval-linked version and the file to belong to that exact resource set. They reuse IFileStorageService outside wwwroot and return attachments with `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`. Rejected versions stay available only through existing authorized submission history endpoints. No repository link exposes stored filenames or physical paths.

## Search, pagination and physical availability

Search covers snapshot title and keyword string; storage/tagging was not redesigned. Project type, academic year and semester filters apply to snapshot values. Coordinator scope is fixed, with no department filter. EF uses parameterized queries, AsNoTracking and required-field projections. Sorting uses review completion date descending, then submission ID descending. Count, sorting and pagination run in SQL with 20-record pages.

Selected page resources are loaded in one batch query. Physical objects are opened to verify availability and expected size before showing an entry. Detail and download operations verify the approved resource set again. Unavailable entries are withheld, safe warnings are shown, and logs contain record/file IDs and error type rather than storage paths. Structural inconsistencies are detected only within the caller's authorized scope.

Filesystem availability cannot be represented by a SQL predicate. Pagination therefore counts structurally eligible matching approved records; physical checks apply to the bounded selected page. A page can contain fewer displayed entries if some files are unavailable. The UI labels this count as matching approved records and reports withheld entries. It never scans the full filesystem/repository into memory to fill a page. Physical file loss between inspection and download returns a safe error.

## Tests added and executed

Milestone7Checks runs after the existing Milestone 6 acceptance scenario and verifies:

- Approved Version 2 metadata, four-file membership and review completion date.
- Rejected-only resource and incorrect version/resource ID denial.
- Owner and same-department Coordinator byte downloads and private attachment headers.
- Another Student and both cross-department directions, including direct service access.
- Nonapproved status exclusion despite existing approval, altered live metadata and arbitrary later snapshot.
- Missing approved version/resource snapshot, incorrect round/department/date and ambiguous multiple approvals.
- Title/keyword/type/year/semester/combined filters, no results, invalid lengths/enums/page values and SQL-like text.
- Bounded disjoint pagination, ordering, HTTP binding and extreme-page clamping.
- Missing physical resource safe error, withheld listing and reported inconsistency.
- Anonymous/unrelated roles, missing department, inactive users, approved freeze and preserved rejected history.

DatabaseAudit additionally fingerprints versions/resource membership and reports approved records without completed approvals, linked versions or resources, and inconsistent approval/version links. Complete earlier milestone tests remain enabled. Both full Milestone 7 test runs passed; the final run after added inconsistency tests produced **1,430 passing assertions and zero failures**.

## Broken/inconsistent approved records and known issues

Current database contains one Approved project: **ID 2, “Test 1”**, owned by `csc20f005@smiu.edu.pk`. Its approval links to Version 1, but that version has **no resources**. This is a preserved legacy empty-resource submission. It is deliberately **not repository eligible** and is withheld with a scoped warning. Do not fabricate an approved file set or bypass the freeze to make it appear. Its data needs an explicitly agreed correction workflow outside this milestone; a new properly submitted/reviewed project can be used for manual repository testing.

Other local checks found no completed-approval/version-link/date/round inconsistencies and no missing physical objects among the two existing resource files. There are no unlinked reviews or missing submitted-version snapshots. Historical relationships were preserved.

Earlier known data issues remain: legacy Student `ranjhanisaddam@smiu.edu.pk` lacks canonical StudentNumber/email mapping; one non-institutional legacy Identity account remains blocked by validation. They were not silently changed. Local storage retains historical evidence and does not add malware scanning or a cleanup module. Missing physical files are detected safely, not automatically reconstructed.

There is no unresolved Milestone 7 code/test failure. The legacy empty-resource approval is a reported data inconsistency with access safely denied. The application is ready for Milestone 8 development, with this data correction still outstanding.

## Files created

Relative to `H:\AI\AcademicRepostory`:

- `MILESTONE7.md`
- `AcademicRepository.Tests/Milestone7Checks.cs`
- `AcademicRepository/Models/RepositoryViewModels.cs`
- `AcademicRepository/Services/RepositoryService.cs`
- `AcademicRepository/Controllers/RepositoryController.cs`
- `AcademicRepository/Views/Repository/Index.cshtml`
- `AcademicRepository/Views/Repository/Details.cshtml`
- `AcademicRepository/Views/Repository/Error.cshtml`

## Files modified

- `README.md`
- `AcademicRepository.Tests/Program.cs`
- `AcademicRepository.Tests/DatabaseAudit.cs`
- `AcademicRepository/Program.cs`
- `AcademicRepository/Views/Shared/_Layout.cshtml`

## Manual acceptance checklist

1. Restart the app. Create a complete canonical Student project with at least one file, submit, and have the same-department Coordinator approve it.
2. Student opens My Approved Projects; Coordinator opens Approved Repository. Inspect the approved metadata, version, review round, date and file download.
3. Run the two-round rejection/revision/approval scenario; confirm the repository shows the approval-linked corrected version and excludes rejected-only files.
4. Search title/keywords and combine filters; test pagination with the integration fixtures.
5. Try another Student's and another department's IDs; confirm safe denial. Log out and confirm repository/download requires authentication.
6. Confirm the known empty-resource Approved ID 2 is withheld and the warning is visible to its authorized users.

MILESTONE 7 VERIFIED — READY FOR MILESTONE 8
