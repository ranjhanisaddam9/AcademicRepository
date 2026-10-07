namespace AcademicRepository.Models;

// Immutable after official submission. Stored resources are never overwritten.
public sealed class SubmissionVersion
{
    public int Id { get; set; }
    public int ProjectSubmissionId { get; set; }
    public ProjectSubmission ProjectSubmission { get; set; } = null!;
    public int VersionNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime SubmittedAt { get; set; }
    public string CreatedByUserId { get; set; } = "";
    public string TitleSnapshot { get; set; } = "";
    public string AbstractSnapshot { get; set; } = "";
    public string KeywordsSnapshot { get; set; } = "";
    public ProjectType ProjectTypeSnapshot { get; set; }
    public string? SupervisorNameSnapshot { get; set; }
    public string? CourseNameSnapshot { get; set; }
    public string? CourseCodeSnapshot { get; set; }
    public string? AcademicYearSnapshot { get; set; }
    public string? SemesterSnapshot { get; set; }
    public int DepartmentIdSnapshot { get; set; }
    public Department Department { get; set; } = null!;
    public ICollection<SubmissionVersionFile> Files { get; set; } = new List<SubmissionVersionFile>();
}

public sealed class SubmissionVersionFile
{
    public int SubmissionVersionId { get; set; }
    public SubmissionVersion SubmissionVersion { get; set; } = null!;
    public int ProjectFileId { get; set; }
    public ProjectFile ProjectFile { get; set; } = null!;
}

public sealed record VersionSummary(int Id, int VersionNumber, DateTime SubmittedAt, string Title);
public sealed record VersionHistoryViewModel(int SubmissionId, IReadOnlyList<VersionSummary> Versions);
public sealed record VersionDetailsViewModel(int Id, int VersionNumber, SubmissionDetailsViewModel Project,
    IReadOnlyList<ProjectFileViewModel> Files, IReadOnlyList<ReviewHistoryItem> Reviews);
