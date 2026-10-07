# MILESTONE 5 VERIFICATION REPORT

Verified 7 October 2026 against the current application and SQL Server Express. Only Milestone 5 was implemented. Changes are uncommitted.

## Verification results

| Item | Result | Evidence |
|---|---|---|
| A. Coordinator Dashboard | PASS | MVC tests verify department name, scoped status counts and recent submissions. SQL queries filter by department before grouping. |
| B. Department-Scoped Review Queue | PASS | Submitted/UnderReview only; cross-department and Draft records excluded. SQL filtering, ordering and 20-record pagination tested, including multiple pages and extreme page values. |
| C. Review Details | PASS | Authorized details expose StudentNumber, email, project metadata and safe resource metadata; GET leaves status unchanged. |
| D. Start Review | PASS | Explicit antiforgery-protected POST changes Submitted to UnderReview and atomically creates Pending history. Duplicate start rejected. |
| E. SubmissionReview History | PASS | Separate entity, ordered rounds, immutable completed decisions, database uniqueness and legacy-history behavior tested. |
| F. Approval | PASS | Only active initiating Coordinator can approve current UnderReview record. Optional comments and server UTC completion tested. |
| G. Rejection | PASS | Same state/reviewer restrictions; rejection stored consistently with parent status. |
| H. Required Rejection Comments | PASS | Empty, whitespace and excessive comments rejected; 2,000-character maximum. Comments are HTML encoded. |
| I. Same-Department File Access | PASS | Authorized Coordinator downloads uploaded resource bytes in Submitted and reviewed states. |
| J. Cross-Department File Protection | PASS | Both directions tested; cross-department and Draft file IDs return 404 before storage access. |
| K. Direct URL Protection | PASS | Cross-department review IDs return 404; anonymous and unrelated roles denied server-side. |
| L. POST Tampering Protection | PASS | Cross-department Start/Approve/Reject fail without changes; forged identity, department, status and dates ignored. Route ID binding, CSRF and GET rejection tested. |
| M. Review Concurrency Protection | PASS | Simultaneous starts yield one success/one conflict; competing decisions yield one immutable result. Save failures roll back status/history together. |
| N. Student Review Visibility | PASS | Owner sees result, comments and rounds; another Student cannot access project/history. Reviewed metadata/resources remain frozen. |
| O. Database Migration | PASS | AddSubmissionReviews applied to configured database; no pending EF model changes. Existing record hashes unchanged. Upgrade tests preserve existing users, departments, submission status/rowversion and resource metadata. |
| P. Automated Tests | PASS | 1,204 passing assertions, zero failures in executable SQL/MVC integration harness. |
| Q. Regression Tests | PASS | Full Milestones 1, 2, 3, 3.5 and 4 suite passes alongside Milestone 5. |

## Build, runtime and commands

Clean, restore and Release build succeeded with zero warnings/errors. Updated Development application started on isolated port 5199 against the configured database; Login returned HTTP 200. The temporary instance was stopped. Existing running development instance was not stopped; restart it to load the new code. The HTTP-only smoke instance emitted the expected missing HTTPS-port warning; normal launch profiles retain HTTPS.

```powershell
dotnet clean AcademicRepository.slnx -c Release
dotnet restore AcademicRepository.slnx
dotnet build AcademicRepository.slnx -c Release --no-restore
dotnet run --project AcademicRepository.Tests -c Release --no-build
dotnet ef database update --project AcademicRepository --configuration Release
dotnet ef migrations has-pending-model-changes --project AcademicRepository --configuration Release
dotnet run --project AcademicRepository.Tests -c Release -- --audit
```

The tests use isolated SQL Server databases and temporary resource storage. They clean up their own fixtures. Production/local user data was not used for destructive tests. Evidence logs are outside the repository at `H:\AI\milestone5-{clean,restore,build,final,migration,model}.log` and `H:\AI\milestone5-db-{before,after}.txt`.

## Migration and schema

Created and applied `20261007051802_AddSubmissionReviews`. Its Up creates only SubmissionReviews and its indexes/constraints; it does not alter or delete existing records or invent historical decisions. Do not downgrade this migration after real reviews exist: its Down drops the new review table.

| Column | SQL type / rules |
|---|---|
| Id | int identity primary key |
| ProjectSubmissionId | int, required restrictive FK to ProjectSubmissions |
| ReviewerId | nvarchar(450), required restrictive FK to AspNetUsers |
| ReviewRound | int, at least 1 |
| Decision | int: Pending=0, Approved=1, Rejected=2 |
| Comments | nullable nvarchar(2000); nonblank required for rejection |
| StartedAt | required datetime2, generated UTC |
| CompletedAt | nullable datetime2, UTC; null for Pending and required for completed decisions |
| RowVersion | SQL rowversion concurrency token |

ReviewRound is implemented: first round 1; next round is existing maximum plus 1. Unique submission/round prevents overwriting historical rounds. A filtered unique submission index where Decision=0 prevents multiple active Pending reviews. No revision or resubmission endpoint is implemented.

## Architecture and authorization

IReviewService/ReviewService handles scoped dashboard/list/detail queries, transitions, history and downloads through dependency injection. Every Coordinator service operation reloads the user and checks active status, institutional email, Coordinator role and assigned department. Missing department denies access with configuration guidance; it never broadens scope. Cookie validation revokes sessions if required department assignment is removed.

All discovery queries apply DepartmentId and exclude Draft before returning rows. Search covers title, Student name and formatted StudentNumber; additional status/type/year/semester filters and pagination execute in SQL. ReviewQueue narrows to Submitted/UnderReview. The current format remains `CSC20F005@smiu.edu.pk` / `CSC-20F-005`.

Transitions use a transaction and SQL UPDLOCK/HOLDLOCK on the scoped parent submission, then validate current state and active reviewer. Parent status and review decision persist together. Completed reviews cannot be reopened through this milestone. Another Coordinator in the same department can view/download but cannot complete a colleague's active review. Posted ReviewId must match the active record; all identities, timestamps and resulting decisions come from server logic.

CoordinatorController also requires the Coordinator role; global MVC antiforgery protects mutations. Admin, DepartmentHead, ORICQEC and Student roles do not inherit review powers. Missing/cross-department resources return safe 404 responses. Database errors return safe conflict/unavailable messages without connection strings or paths.

`/Coordinator/Files/Download/{id}` adds Coordinator authorization using the existing IFileStorageService. The Student ProjectFiles endpoints retain their existing ownership rules. Storage remains outside wwwroot; links expose resource IDs, never stored keys/physical paths. Draft resources remain private. Student Details uses an ownership-checked view component for review history.

## Tests added and executed

Milestone5Checks exercises dashboard/navigation/queues, all review transitions, current Student identity display, rejection validation/encoding, optional approval comments, duplicate actions, simultaneous starts/decisions, initiating reviewer enforcement, tampered POST fields, cross-department GET/POST/download, Draft privacy, missing department and inactive users, unrelated roles, service ownership checks, scoped multi-page filtering, database unique constraints and atomic rollback for start and completion failures. It also verifies Student history and frozen reviewed resources/metadata.

MigrationUpgradeChecks now applies the previous resource migration before upgrading to Milestone 5, preserving an existing UnderReview record and file metadata without inventing a review. DatabaseAudit now fingerprints ProjectFiles and reports missing completed/active review histories. Program runs these checks with the full earlier milestone regression suite.

Executed final complete harness: **1,204 passing assertions; 0 failing**. These are assertions in an executable integration harness, not 1,204 separate test methods. Email regression uses recording/failure fakes; no live Microsoft Graph email was sent, following the user's earlier instruction. Existing Graph configuration is present and unchanged.

## Existing database preservation and inconsistencies

Fresh before/after audit: **5 users, 8 departments, 3 submissions, 5 role assignments and 2 ProjectFiles**. Counts and SHA256 fingerprints match for all five groups. All ten migrations are applied; EF reports no pending model changes. Existing mappings and relationships remain intact; no duplicate normalized emails or submission department mismatches were found.

- Approved/Rejected submissions without review history: **none**.
- UnderReview submissions without a Pending review: **none**.
- Existing Submitted records 1 and 2 have no resources. Preserved; migration does not retroactively enforce new-submission upload rules or fabricate files.
- Legacy Student `ranjhanisaddam@smiu.edu.pk` still has no StudentNumber and does not match the canonical Student email format. Preserved; requires an agreed identity/data correction before use through canonical Student onboarding. The canonical CSC Student remains correctly mapped.
- One preserved legacy non-institutional user remains blocked by existing institutional authentication validation.

No Milestone 5 failure remains. If future data contains UnderReview without Pending history, the UI reports the inconsistency and denies completion. Completed legacy records without history display a warning; history is never fabricated. Removing/deactivating a reviewer can leave their pending review awaiting a future reassignment capability; no automatic takeover was added. Resource malware scanning remains outside the current local-storage implementation. Existing local files were not modified by review operations; automated tests verified real resource downloads and frozen mutations using isolated uploads.

## Files created

Paths below are relative to the repository root `H:\AI\AcademicRepostory`:

- `MILESTONE5.md`
- `AcademicRepository.Tests/Milestone5Checks.cs`
- `AcademicRepository/Controllers/CoordinatorController.cs`
- `AcademicRepository/Models/SubmissionReview.cs`
- `AcademicRepository/Services/ReviewService.cs`
- `AcademicRepository/ViewComponents/StudentReviewHistoryViewComponent.cs`
- `AcademicRepository/Migrations/20261007051802_AddSubmissionReviews.cs`
- `AcademicRepository/Migrations/20261007051802_AddSubmissionReviews.Designer.cs`
- `AcademicRepository/Views/Coordinator/Dashboard.cshtml`
- `AcademicRepository/Views/Coordinator/Submissions.cshtml`
- `AcademicRepository/Views/Coordinator/_ReviewQueueTable.cshtml`
- `AcademicRepository/Views/Coordinator/Review.cshtml`
- `AcademicRepository/Views/Coordinator/ReviewError.cshtml`
- `AcademicRepository/Views/Shared/_ReviewHistory.cshtml`

## Files modified

- `README.md`
- `AcademicRepository.Tests/DatabaseAudit.cs`
- `AcademicRepository.Tests/MigrationUpgradeChecks.cs`
- `AcademicRepository.Tests/Program.cs`
- `AcademicRepository/Controllers/RoleControllers.cs`
- `AcademicRepository/Data/ApplicationDbContext.cs`
- `AcademicRepository/Migrations/ApplicationDbContextModelSnapshot.cs`
- `AcademicRepository/Models/ProjectSubmission.cs`
- `AcademicRepository/Program.cs`
- `AcademicRepository/Services/SubmissionLock.cs`
- `AcademicRepository/Views/Account/Login.cshtml`
- `AcademicRepository/Views/Shared/_Layout.cshtml`
- `AcademicRepository/Views/StudentSubmissions/Details.cshtml`

## Manual acceptance checklist

1. Restart the app, sign in as a Coordinator with an assigned department, inspect counts/queue/filters and current Student ID format.
2. Open a Submitted project; confirm opening does not change status. Start Review explicitly.
3. Attempt completion from a second same-department Coordinator; confirm view/download works and decisions are denied.
4. Reject with blank comments, then valid comments; confirm validation and Student-visible history. Approve another project with optional comments.
5. Try another department's project/file IDs and POST actions; confirm denial without metadata or mutation.
6. As the owner, confirm reviewed resources download while edits/uploads/deletions/resubmission remain denied. Check unrelated roles have no review controls.

Milestone 5 is ready for Milestone 6 development. No Milestone 6 work has begun.

MILESTONE 5 VERIFIED — READY FOR MILESTONE 6
