using System.ComponentModel.DataAnnotations;

namespace AcademicRepository.Models;

public enum ProjectResourceType
{
    [Display(Name = "Project Report")] Report,
    [Display(Name = "Research Paper")] ResearchPaper,
    [Display(Name = "Source Code")] SourceCode,
    Presentation,
    Dataset,
    [Display(Name = "Supporting Document")] SupportingDocument,
    [Display(Name = "Other Resource")] Other
}

public sealed class ProjectFile
{
    public bool IsActive { get; set; } = true;
    public DateTime? DeletedAt { get; set; }
    public int Id { get; set; }
    public int ProjectSubmissionId { get; set; }
    public ProjectSubmission ProjectSubmission { get; set; } = null!;
    public string OriginalFileName { get; set; } = "";
    public string StoredFileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string FileExtension { get; set; } = "";
    public long FileSize { get; set; }
    public ProjectResourceType ResourceType { get; set; }
    public string? Description { get; set; }
    public DateTime UploadedAt { get; set; }
    public string UploadedByUserId { get; set; } = "";
}

public sealed class UploadResourceViewModel
{
    [Required] public IFormFile? File { get; set; }
    [Required, EnumDataType(typeof(ProjectResourceType)), Display(Name = "Resource Type")]
    public ProjectResourceType? ResourceType { get; set; }
    [StringLength(1000)] public string? Description { get; set; }
}

public sealed record ProjectFileViewModel(int Id, string OriginalFileName, ProjectResourceType ResourceType,
    string? Description, long FileSize, DateTime UploadedAt)
{
    public string DisplaySize => FileSize >= 1024 * 1024 ? $"{FileSize / (1024d * 1024):0.#} MB" : $"{Math.Max(0.1, FileSize / 1024d):0.#} KB";
}

public sealed record ResourceListViewModel(int SubmissionId, bool CanManage, IReadOnlyList<ProjectFileViewModel> Files,
    int MaxFileSizeMB, int MaxFilesPerSubmission);
