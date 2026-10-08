using System.ComponentModel.DataAnnotations;

namespace AcademicRepository.Models;

public enum ReviewDecision { Pending, Approved, Rejected }

public sealed class SubmissionReview
{
    public int? SubmissionVersionId { get; set; }
    public SubmissionVersion? SubmissionVersion { get; set; }
    public int Id { get; set; }
    public int ProjectSubmissionId { get; set; }
    public ProjectSubmission ProjectSubmission { get; set; } = null!;
    public string ReviewerId { get; set; } = "";
    public ApplicationUser Reviewer { get; set; } = null!;
    public int ReviewRound { get; set; }
    public ReviewDecision Decision { get; set; }
    public string? Comments { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class ReviewFilterViewModel
{
    [EnumDataType(typeof(SubmissionStatus))] public SubmissionStatus? Status { get; set; }
    [EnumDataType(typeof(ProjectType)), Display(Name = "Project Type")] public ProjectType? ProjectType { get; set; }
    [StringLength(30), Display(Name = "Academic Year")] public string? AcademicYear { get; set; }
    [StringLength(50)] public string? Semester { get; set; }
    [StringLength(200)] public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int? PageSize { get; set; }
    [StringLength(30)] public string Sort { get; set; } = "SubmittedAt";
    [StringLength(4)] public string Direction { get; set; } = "desc";
}

public sealed class ReviewDecisionViewModel
{
    [Range(1, int.MaxValue)] public int ReviewId { get; set; }
    [StringLength(2000)] public string? Comments { get; set; }
}

public sealed record CoordinatorProfile(string Name, int DepartmentId, string Department);
public sealed record ReviewQueueItem(int Id, string Title, string StudentName, string? StudentNumber,
    ProjectType ProjectType, string? AcademicYear, string? Semester, DateTime? SubmittedAt, SubmissionStatus Status, int VersionNumber = 0);
public sealed record CoordinatorDashboardViewModel(CoordinatorProfile Profile, int AwaitingReview, int UnderReview, int Approved,
    int Rejected, IReadOnlyList<ReviewQueueItem> Recent);
public sealed record ReviewListViewModel(CoordinatorProfile Profile, ReviewFilterViewModel Filter, bool QueueOnly,
    PagedResult<ReviewQueueItem> Results)
{
    public IReadOnlyList<ReviewQueueItem> Items => Results.Items;
    public int Total => Results.TotalCount;
    public int PageCount => Results.TotalPages;
}
public sealed record ReviewHistoryItem(int ReviewRound, string ReviewerName, ReviewDecision Decision,
    string? Comments, DateTime StartedAt, DateTime? CompletedAt, int? VersionId = null, DateTime? SubmittedAt = null);
public sealed record ReviewDetailsViewModel(SubmissionDetailsViewModel Project, string? StudentNumber, string Email,
    IReadOnlyList<ProjectFileViewModel> Files, IReadOnlyList<ReviewHistoryItem> History, bool CanStart, bool CanComplete,
    int? ActiveReviewId, string? WorkflowMessage);
