using System.ComponentModel.DataAnnotations;

namespace AcademicRepository.Models;

public class CreateSubmissionViewModel
{
    [Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever]
    public string DepartmentName { get; set; } = "";
    [Required, EnumDataType(typeof(ProjectType)), Display(Name = "Project Type")]
    public ProjectType? ProjectType { get; set; }
    [Required, StringLength(250)]
    public string Title { get; set; } = "";
    [StringLength(10000)]
    public string? Abstract { get; set; }
    [StringLength(1000)]
    public string? Keywords { get; set; }
    [StringLength(150), Display(Name = "Supervisor Name")]
    public string? SupervisorName { get; set; }
    [StringLength(150), Display(Name = "Course Name")]
    public string? CourseName { get; set; }
    [StringLength(30), Display(Name = "Course Code")]
    public string? CourseCode { get; set; }
    [StringLength(30), Display(Name = "Academic Year")]
    public string? AcademicYear { get; set; }
    [StringLength(50)]
    public string? Semester { get; set; }
}

public sealed class EditSubmissionViewModel : CreateSubmissionViewModel
{
    public string? EditToken { get; set; }
}

public sealed record SubmissionListViewModel(int Id, string Title, ProjectType ProjectType, string Department,
    DateTime CreatedAt, DateTime? SubmittedAt, SubmissionStatus Status);

public sealed record SubmissionDetailsViewModel(int Id, string Title, ProjectType ProjectType, SubmissionStatus Status,
    string StudentName, string Department, string Abstract, string Keywords, string? SupervisorName,
    string? CourseName, string? CourseCode, string? AcademicYear, string? Semester, DateTime CreatedAt,
    DateTime? UpdatedAt, DateTime? SubmittedAt);

public sealed class DeleteSubmissionViewModel
{
    public string? EditToken { get; set; }
    [Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever]
    public string Title { get; set; } = "";
}

public sealed record StudentDashboardViewModel(DashboardViewModel Profile, int Total, int Drafts, int Submitted,
    int Approved, int Rejected, IReadOnlyList<SubmissionListViewModel> Recent, int Revisions = 0);
