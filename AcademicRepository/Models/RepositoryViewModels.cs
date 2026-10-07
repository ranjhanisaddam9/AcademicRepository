using System.ComponentModel.DataAnnotations;

namespace AcademicRepository.Models;
public enum RepositorySort { NewestApproved, OldestApproved, TitleAZ, TitleZA }

public sealed class RepositoryFilterViewModel
{
    [StringLength(200)] public string? Search { get; set; }
    [EnumDataType(typeof(ProjectType)), Display(Name = "Project Type")] public ProjectType? ProjectType { get; set; }
    [StringLength(30), Display(Name = "Academic Year")] public string? AcademicYear { get; set; }
    [StringLength(50)] public string? Semester { get; set; }
    [Range(1, int.MaxValue)] public int Page { get; set; } = 1;
    [StringLength(150)] public string? Supervisor { get; set; }
    [EnumDataType(typeof(RepositorySort))] public RepositorySort Sort { get; set; }
}

public sealed record RepositoryScope(string Label, string UserId, int DepartmentId, bool DepartmentWide, bool DepartmentHead = false, string DepartmentName = "");
public sealed record RepositoryListItem(int SubmissionId, int VersionId, int VersionNumber, string Title, string StudentName,
    string? StudentNumber, string Department, ProjectType ProjectType, string? AcademicYear, string? Semester,
    string? Supervisor, string Keywords, DateTime ApprovedAt);
public sealed record RepositoryListViewModel(RepositoryScope Scope, RepositoryFilterViewModel Filter,
    IReadOnlyList<RepositoryListItem> Items, int MatchingRecords, int PageCount, bool HasUnavailableRecords, IReadOnlyList<string>? AcademicYears = null);
public sealed record RepositoryDetailsViewModel(int SubmissionId, int VersionId, int VersionNumber, int ReviewRound,
    string StudentName, string? StudentNumber, string Department, string Title, string Abstract, string Keywords,
    ProjectType ProjectType, string? Supervisor, string? CourseName, string? CourseCode, string? AcademicYear,
    string? Semester, DateTime SubmittedAt, DateTime ApprovedAt, string ReviewerName, string? ApprovalComments,
    IReadOnlyList<ProjectFileViewModel> Files, string? StudentEmail = null);
public sealed record RepositoryTypeCount(ProjectType Type, int Count);
public sealed record RepositoryGroupCount(string Label, int Count);
public sealed record DepartmentRepositoryStats(string Department, int Total, IReadOnlyList<RepositoryTypeCount> ProjectTypes,
    IReadOnlyList<RepositoryGroupCount> AcademicYears, IReadOnlyList<RepositoryGroupCount> Semesters,
    string CurrentAcademicYear, int CurrentAcademicYearCount, IReadOnlyList<RepositoryListItem> Recent, bool HasUnavailableRecords);
