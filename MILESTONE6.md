# MILESTONE 6 VERIFICATION REPORT

Verified 7 October 2026. Milestone 6 implements rejected-submission revision, resubmission and required version history. Changes remain uncommitted alongside the previously completed Milestone 5 changes. No Milestone 7 functionality was added.

## A–P verification

| Item | Result | Evidence |
|---|---|---|
| A. Rejected → Revision | PASS | Owner POST succeeds only after a completed Rejected review linked to a preserved version. Duplicate requests conflict; GET and missing antiforgery fail. Submitted/UnderReview/Approved transitions denied. |
| B. Revision Editing | PASS | Explicit metadata mapping allows corrections; project type remains locked after first submission. Stale edits cannot change a submitted/under-review project. |
| C. Revision File Management | PASS | Upload, active removal and replacement tested in Revision; Submitted and Approved remain frozen. Never-submitted revision uploads can be physically deleted. |
| D. Department Lock | PASS | Owner/current department match required for revision. Forged DepartmentId/StudentId/Status/dates/version values cannot change identity or routing. |
| E. Submission Versioning | PASS | Initial Submit and Resubmit atomically capture metadata and active resources. Historical metadata, dates and membership remain unchanged. |
| F. Historical File Preservation | PASS | Previously submitted resources are soft removed; their original bytes remain downloadable through secure Version 1 routes after replacement and final approval. |
| G. Resubmission | PASS | Required metadata and at least one valid active physical resource required. Resubmission freezes resources and re-enters the same department queue. Missing physical resources block submission. |
| H. Version Number Increment | PASS | Versions 1, 2 and 3 tested; duplicate simultaneous resubmission produces one version/one conflict. |
| I. Review Round Preservation | PASS | Original review rowversion, decision and comments remain unchanged during revision and subsequent reviews. |
| J. Second Review Round | PASS | Round 2 links to Version 2, starts Pending and completes Approved. Same-department colleague can inspect but cannot complete the initiating reviewer's round. |
| K. Multiple Rejection Support | PASS | Separate scenario verifies Version 1 Rejected, Version 2 Rejected, Version 3 Approved without overwriting earlier states. |
| L. Cross-Department Security | PASS | BUS Coordinator cannot open CSC current project, Version 1/2 or their resources. Department-scoped queues and review services retain Milestone 5 controls. |
| M. Ownership Security | PASS | Other Student cannot view history, download historical files or start revision. Version/file ID mismatch denied. Unrelated roles and anonymous users denied. |
| N. Database Migration | PASS | Additive migration applied; no pending EF model changes. Before/after record and physical-file fingerprints match. Legacy upgrade fixtures verify safe backfill and ambiguous-history preservation. |
| O. Automated Tests | PASS | Final executable integration harness: 1,342 passing assertions, zero failures. These are assertions, not 1,342 separate test methods. |
| P. Regression Tests | PASS | Complete Milestones 1, 2, 3, 3.5, 4 and 5 suite passes with Milestone 6. |

## Build and runtime

Clean, package restore and Release build succeeded with **zero warnings and zero errors**. The final build and complete SQL Server/MVC harness passed after the last queue indicator change. The application started against the migrated configured database on an isolated port; Login returned HTTP 200. That temporary instance was stopped. Restart your existing development instance to load the new code.

```powershell
dotnet clean AcademicRepository.slnx -c Release
dotnet restore AcademicRepository.slnx
dotnet build AcademicRepository.slnx -c Release
dotnet run --project AcademicRepository.Tests -c Release
dotnet ef database update --project AcademicRepository --configuration Release
dotnet ef migrations has-pending-model-changes --project AcademicRepository --configuration Release
dotnet run --project AcademicRepository.Tests -c Release -- --audit
```

Evidence logs are outside Git at `H:\AI\milestone6-{clean,restore,build,final,model,migration}.log` and `H:\AI\milestone6-db-{before,after}.txt`. The harness creates isolated SQL Server databases and temporary files and cleans up its own fixtures. Live Graph email was not sent, following the user's earlier instruction; authentication/email regression used recording and failure fakes. Existing Graph configuration, Student OTP, development email redirect and ORICQEC flows remain unchanged.

## Migration and SubmissionVersion schema

Created and applied **20261007102332_AddSubmissionVersioning**. All eleven migrations are applied. It creates SubmissionVersions and SubmissionVersionFiles, adds nullable SubmissionReview.SubmissionVersionId, and adds ProjectFile.IsActive (existing rows default true) / DeletedAt. It does not recreate existing entities, reset data or delete decisions/resources. Downgrading after real version history exists would remove the new history schema and is not a preservation operation.

| SubmissionVersions field | SQL type / purpose |
|---|---|
| Id | int identity primary key |
| ProjectSubmissionId | required int restrictive FK to current ProjectSubmission |
| VersionNumber | required int >= 1; unique with ProjectSubmissionId |
| CreatedAt / SubmittedAt | required datetime2, UTC; distinct snapshot creation and official submission dates |
| CreatedByUserId | required nvarchar(450), restrictive Identity user FK |
| DepartmentIdSnapshot | required int, restrictive Department FK |
| TitleSnapshot | required nvarchar(250) |
| AbstractSnapshot | required nvarchar(max), application maximum 10,000 |
| KeywordsSnapshot | required nvarchar(1000) |
| ProjectTypeSnapshot | required int |
| SupervisorNameSnapshot / CourseNameSnapshot | nullable nvarchar(150) |
| CourseCodeSnapshot / AcademicYearSnapshot | nullable nvarchar(30) |
| SemesterSnapshot | nullable nvarchar(50) |

SubmissionVersionFiles uses composite primary key (SubmissionVersionId, ProjectFileId), with restrictive FKs to versions and files. This records each official active resource set without duplicating physical bytes. SubmissionReview.SubmissionVersionId is a nullable restrictive FK with a filtered unique index. New workflow reviews link to the latest version, and service rules require VersionNumber == ReviewRound. Nullable links permit unresolved legacy histories to remain honest. No application endpoint edits or deletes a submitted version, membership or completed review.

## File version/history strategy

Each upload gets a new immutable stored filename, including a replacement with the same displayed filename. ProjectFile metadata is not editable through the application. Files included in any submitted version cannot be physically removed through revision: Delete makes them inactive and records DeletedAt. Current resource lists and limits count only active files. Historical resource membership continues to reference retained metadata and bytes.

An active Draft/Revision upload that has never been officially submitted retains Milestone 4 physical deletion behavior. Restrictive version-file FKs provide another safeguard against deleting referenced records. All resource changes and submission transitions share the same locked parent SQL row and transaction. Storage error handling/compensation from Milestone 4 remains in place.

`/SubmissionVersions/{id}` shows submitted metadata, its resource set and the linked review comments. `/SubmissionVersions/{id}/Files/{fileId}` validates both authorized parent access and exact version membership before opening storage. Student access requires active account, institutional email, ownership and current department match; Coordinator access requires active role and matching department. Physical paths/storage keys are never returned. Existing Student downloads retain owner checks; Coordinator current routes retain department controls. During Revision, live metadata is private to the Student, while authorized Coordinators can inspect previously submitted versions.

## State transitions and UI

Revision is appended as SubmissionStatus value **5**, preserving existing numeric values. SubmissionWorkflowService centralizes Student transitions and snapshot creation; existing ReviewService continues Coordinator transitions and now attaches reviews to submitted versions. SubmissionWorkflow supplies common edit/discovery rules used by file/review/controller logic.

- Draft → Submitted: required metadata/resources; immutable Version 1 snapshot.
- Submitted → UnderReview: explicit Coordinator Start Review; Pending review linked to latest version.
- UnderReview → Approved/Rejected: active initiating Coordinator only; consistent review/parent update.
- Rejected → Revision: explicit Student owner POST; valid department and latest completed rejection/version required.
- Revision → Submitted: valid active resources/metadata; next immutable version and most recent SubmittedAt.

Approved remains final. Rejected stays frozen until Start Revision. Revision allows metadata correction and active-resource management; project type, department and Student identity are locked. Initial Draft transfer behavior from Milestone 3.5 remains compatible: a draft adopts the current server-side Student department on save; reviewed/revision submissions cannot be rerouted this way.

Student dashboard distinguishes Rejected / Revision Required and Revisions in Progress. Lists lead to feedback/start revision or edit/resources/resubmit according to state. Details displays comments before Start Revision, and historical versions/review rounds remain linked. Coordinator queue labels Initial Submission / Resubmission with version number. Filtering remains operational and department-scoped; no repository search or later reporting module was added.

SQL UPDLOCK/HOLDLOCK on the parent, rowversion edit tokens and unique version/review indexes protect double resubmission and stale actions. Snapshot, version-file membership and Submitted transition save atomically. Tests inject snapshot persistence failure and confirm rollback. State-changing actions use POST and the existing global antiforgery filter. ViewModels and explicit field mapping prevent overposting.

## Legacy data and preservation evidence

Before/after audit confirms **5 users, 8 departments, 3 submissions, 5 role assignments, 2 ProjectFiles and 1 review decision**. All original field fingerprints match. Both physical resource SHA256 values match. Review linking updates its new VersionId (and SQL rowversion), while preserving original review fields/decision/comments.

Backfill creates Version 1 only for a single known submitted state: non-Draft submitted/reviewed status, known SubmittedAt, at most one Round 1 review, no resource uploaded after submission, no review started before submission, and no modification timestamp beyond the last known submission/review event. It snapshots existing fields/resource membership and links a known first review. Snapshot CreatedAt is the migration's UTC capture time; historical SubmittedAt remains the recorded original date. It never invents missing files or reconstructs multiple unknown rounds.

Current local results:

- Submitted/reviewed submissions without a version: **0**.
- Approved submissions without a version: **0**.
- Rejected submissions without a version: **0**.
- Reviews unlinked to a version: **0**.
- Backfilled Version 1 records: submission IDs **1, 2 and 3**; existing review linked.
- UnderReview without active review / Approved or Rejected without review: **none**.
- Department inconsistencies or duplicate normalized emails: **none**.
- Existing Submitted record **2** has no resources; preserved with its accurately empty resource set. New/resubmitted versions require at least one valid active resource.

Ambiguous legacy records in other databases remain unlinked and are reported by `--audit`; they cannot Start Revision until their history is resolved. Migration tests explicitly preserve such records without fabricated snapshots. The local legacy Student `ranjhanisaddam@smiu.edu.pk` still has no StudentNumber/canonical department-prefix email; its data was preserved. This pre-existing account requires an agreed identity correction for canonical passwordless Student use. One pre-existing non-institutional Identity user remains blocked by institutional validation. Neither issue was silently repaired during Milestone 6.

## Tests added and fixes verified

Milestone6Checks covers the requested three-file initial package, rejection feedback, revision, replacement report/dataset, Version 2 submission and Round 2 approval; it also verifies a three-version/two-rejection cycle. Additional assertions cover owner and department isolation, status tampering, project-type lock, CSRF/GET rejection, duplicate Start Revision, concurrent resubmission, stale edits during review, active resource requirements, missing physical resources, immutable metadata/membership/comments, historical bytes, never-submitted cleanup, historical review display, same-department reviewer ownership, unrelated-role denial and rollback on snapshot save failure.

MigrationUpgradeChecks now verifies version backfill for known states, links a known rejection, preserves file metadata/active defaults, and leaves ambiguous multi-round or undated histories unlinked. DatabaseAudit adds decision fingerprints, physical resource hashes and version/history inconsistency reporting. Full earlier milestone checks remain enabled.

The first regression run exposed an overly broad department-match restriction on existing Draft transfer behavior; it was narrowed to Revision and the original transfer test passes. The initial acceptance fixture inherited the earlier three-file test limit; Milestone 6 fixtures use the default ten-file limit so the required four-file resubmitted package is tested. Application upload limits remain enforced. No failing assertion or Milestone 6 code defect remains.

## Files created during Milestone 6

Paths relative to `H:\AI\AcademicRepostory`:

- `MILESTONE6.md`
- `AcademicRepository.Tests/Milestone6Checks.cs`
- `AcademicRepository/Models/SubmissionVersion.cs`
- `AcademicRepository/Services/SubmissionWorkflowService.cs`
- `AcademicRepository/Services/SubmissionVersionService.cs`
- `AcademicRepository/Controllers/SubmissionVersionsController.cs`
- `AcademicRepository/ViewComponents/SubmissionVersionsViewComponent.cs`
- `AcademicRepository/Views/Shared/_VersionHistory.cshtml`
- `AcademicRepository/Views/SubmissionVersions/Details.cshtml`
- `AcademicRepository/Migrations/20261007102332_AddSubmissionVersioning.cs`
- `AcademicRepository/Migrations/20261007102332_AddSubmissionVersioning.Designer.cs`

## Files modified during Milestone 6

- `README.md`
- `AcademicRepository.Tests/Program.cs`
- `AcademicRepository.Tests/MigrationUpgradeChecks.cs`
- `AcademicRepository.Tests/DatabaseAudit.cs`
- `AcademicRepository/Controllers/StudentSubmissionsController.cs`
- `AcademicRepository/Data/ApplicationDbContext.cs`
- `AcademicRepository/Migrations/ApplicationDbContextModelSnapshot.cs`
- `AcademicRepository/Models/ProjectSubmission.cs`
- `AcademicRepository/Models/ProjectFile.cs`
- `AcademicRepository/Models/SubmissionReview.cs`
- `AcademicRepository/Models/SubmissionViewModels.cs`
- `AcademicRepository/Program.cs`
- `AcademicRepository/Services/SubmissionLock.cs`
- `AcademicRepository/Services/ProjectFileService.cs`
- `AcademicRepository/Services/ReviewService.cs`
- `AcademicRepository/Services/StudentSubmissionService.cs`
- `AcademicRepository/Views/StudentSubmissions/Details.cshtml`
- `AcademicRepository/Views/StudentSubmissions/Edit.cshtml`
- `AcademicRepository/Views/StudentSubmissions/_Fields.cshtml`
- `AcademicRepository/Views/Student/Dashboard.cshtml`
- `AcademicRepository/Views/Shared/_SubmissionTable.cshtml`
- `AcademicRepository/Views/Shared/_ReviewHistory.cshtml`
- `AcademicRepository/Views/Coordinator/Review.cshtml`
- `AcademicRepository/Views/Coordinator/Submissions.cshtml`
- `AcademicRepository/Views/Coordinator/_ReviewQueueTable.cshtml`

Earlier Milestone 5 uncommitted files remain in the workspace; they are inventoried in MILESTONE5.md and were preserved.

## Manual acceptance and known limits

1. Restart the app. As canonical Student, submit a project with resources; confirm Version 1.
2. As same-department Coordinator, start and reject with comments. Student reads feedback and starts Revision.
3. Edit allowed metadata, remove a historical report and upload its correction/additional dataset. Open Version 1 and download the original report.
4. Resubmit, confirm Version 2 and Coordinator queue entry. Start Round 2 and approve; confirm both comments and versions remain visible.
5. Try other Student/BUS Coordinator historical IDs, forged department/type and stale forms; confirm denial and frozen Approved state.

Historical storage intentionally grows because reviewed evidence is retained; no historical cleanup/reassignment feature was added. Malware scanning remains an existing local-storage limitation. Backfill trusts the integrity of recorded pre-Milestone-6 timestamps and the previously verified frozen workflow; ambiguous records require manual analysis. A stopped/deactivated reviewer may require future reassignment handling. No full diff engine, repository, evaluation, reporting, notifications or audit module was implemented.

The application is ready for Milestone 7 development; existing legacy-account correction remains a documented data task.

MILESTONE 6 VERIFIED — READY FOR MILESTONE 7
