using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AcademicRepository.Models;

public sealed class OperationalSettingsViewModel
{
    [Range(1, 100, ErrorMessage = "Choose a file size from 1 to 100 MB.")] public int MaxFileSizeMB { get; set; } = 50;
    [Range(1, 100)] public int MaxFilesPerSubmission { get; set; } = 10;
    [Range(1, 30, ErrorMessage = "OTP expiry must be from 1 to 30 minutes.")] public int OtpExpiryMinutes { get; set; } = 10;
    [Range(1, 10, ErrorMessage = "OTP attempts must be from 1 to 10.")] public int OtpMaxAttempts { get; set; } = 5;
    [Range(1, 600, ErrorMessage = "OTP resend cooldown must be from 1 to 600 seconds.")] public int OtpResendCooldownSeconds { get; set; } = 60;
    [Range(10, 100, ErrorMessage = "Repository page size must be from 10 to 100.")] public int RepositoryPageSize { get; set; } = 20;
    [RegularExpression("^20[0-9]{2}-20[0-9]{2}$", ErrorMessage = "Academic year must use YYYY-YYYY format.")] public string DefaultAcademicYear { get; set; } = "";
    [Required, StringLength(80)] public string ApplicationDisplayName { get; set; } = "Academic Project Repository";
    [StringLength(256), EmailAddress] public string? SupportEmail { get; set; }
    [ValidateNever] public DateTime? UpdatedAt { get; set; }
    [ValidateNever] public string? UpdatedBy { get; set; }
}

public sealed record AdminCount(string Label, int Count);
public sealed record AdminDashboardViewModel(IReadOnlyList<AdminCount> Overview, IReadOnlyList<AdminCount> Warnings,
    bool DatabaseHealthy, bool FileStorageHealthy, bool GraphConfigured, string Environment, string ApplicationVersion, DateTime UtcNow);
public sealed record AdminSystemInformationViewModel(string Environment, string ApplicationVersion, DateTime UtcNow,
    bool DatabaseHealthy, bool FileStorageHealthy, bool GraphConfigured, string? SenderEmail, int MaxFileSizeMB,
    int MaxFilesPerSubmission, int OtpExpiryMinutes, int OtpMaxAttempts, int OtpResendCooldownSeconds, string DefaultAcademicYear);
public sealed record DataDiagnosticIssue(string Category, string Record, string Description);
public sealed record DataConsistencyViewModel(DateTime CheckedAtUtc, int OrphanFileCount, IReadOnlyList<DataDiagnosticIssue> Issues);
public sealed record UserListItem(string Id, string FullName, string Email, string Role, string Department, bool IsActive,
    string? StudentNumber, DateTime CreatedAt);
public sealed record UserListPageViewModel(IReadOnlyList<UserListItem> Users, int Page, int PageCount, int Total,
    string? Search, string? Role, int? DepartmentId, bool? IsActive, IReadOnlyList<SelectListItem> Departments);
