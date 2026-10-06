using System.ComponentModel.DataAnnotations;

namespace AcademicRepository.Models;

public sealed class LoginViewModel
{
    [Required, EmailAddress]
    public string Email { get; set; } = "";
    [Required, DataType(DataType.Password)]
    public string Password { get; set; } = "";
    public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}

public sealed record DashboardViewModel(string FullName, IReadOnlyList<string> Roles, string Department, int? DepartmentCount = null, int? UserCount = null);
