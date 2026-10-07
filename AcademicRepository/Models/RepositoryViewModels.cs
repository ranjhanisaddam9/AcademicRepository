using System.ComponentModel.DataAnnotations;

namespace AcademicRepository.Models;

public sealed class RepositoryFilterViewModel
{
    [StringLength(200)] public string? Search { get; set; }
    [EnumDataType(typeof(ProjectType)), Display(Name = "Project Type")] public ProjectType? ProjectType { get; set; }
    [StringLength(30), Display(Name = "Academic Year")] public string? AcademicYear { get; set; }
    [StringLength(50)] public string? Semester { get; set; }
    [Range(1, int.MaxValue)] public int Page { get; set; } = 1;
}

public sealed record RepositoryScope(string Label, string UserId, int DepartmentId, bool Coordinator);
public sealed record RepositoryListItem(int SubmissionId, int VersionId, int VersionNumber, string Title, string StudentName,
    string? StudentNumber, string Department, ProjectType ProjectType, string? AcademicYear, string? Semester,
    string? Supervisor, string Keywords, DateTime ApprovedAt);
public sealed record RepositoryListViewModel(RepositoryScope Scope, RepositoryFilterViewModel Filter,
    IReadOnlyList<RepositoryListItem> Items, int MatchingRecords, int PageCount, bool HasUnavailableRecords);
public sealed record RepositoryDetailsViewModel(int SubmissionId, int VersionId, int VersionNumber, int ReviewRound,
    string StudentName, string? StudentNumber, string Department, string Title, string Abstract, string Keywords,
    ProjectType ProjectType, string? Supervisor, string? CourseName, string? CourseCode, string? AcademicYear,
    string? Semester, DateTime SubmittedAt, DateTime ApprovedAt, string ReviewerName, string? ApprovalComments,
    IReadOnlyList<ProjectFileViewModel> Files);
