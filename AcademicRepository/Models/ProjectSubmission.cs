using System.ComponentModel.DataAnnotations;

namespace AcademicRepository.Models;

public enum ProjectType
{
    Assignment,
    [Display(Name = "Semester Project")] SemesterProject,
    [Display(Name = "Final Year Project")] FinalYearProject,
    [Display(Name = "Research Project")] ResearchProject
}

public enum SubmissionStatus
{
    Draft,
    Submitted,
    [Display(Name = "Under Review")] UnderReview,
    Approved,
    Rejected
}

public class ProjectSubmission
{
    public ICollection<ProjectFile> ProjectFiles { get; set; } = new List<ProjectFile>();
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Abstract { get; set; } = "";
    public string Keywords { get; set; } = "";
    public ProjectType ProjectType { get; set; }
    public string StudentId { get; set; } = "";
    public ApplicationUser Student { get; set; } = null!;
    public int DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
    public string? SupervisorName { get; set; }
    public string? CourseName { get; set; }
    public string? CourseCode { get; set; }
    public string? AcademicYear { get; set; }
    public string? Semester { get; set; }
    public SubmissionStatus Status { get; set; } = SubmissionStatus.Draft;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
