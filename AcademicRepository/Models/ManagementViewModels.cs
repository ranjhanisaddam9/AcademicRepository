using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AcademicRepository.Models;

public sealed class DepartmentFormViewModel
{
    [Required, StringLength(120)]
    public string Name { get; set; } = "";
    [Required, StringLength(20)]
    public string Code { get; set; } = "";
    [StringLength(3), RegularExpression("^([A-Z]{3})?$", ErrorMessage = "StudentCode must contain three letters, for example CSC.")]
    [Display(Name = "Student email code")]
    public string? StudentEmailKeyword { get; set; }
}

public sealed record DepartmentListViewModel(int Id, string Code, string Name, bool IsActive, string StudentEmailKeyword = "");
public sealed record DepartmentDetailsViewModel(int Id, string Code, string Name, bool IsActive, DateTime CreatedAt, DateTime? UpdatedAt, int UserCount);

public sealed class UserFormViewModel
{
    [Required, StringLength(150), Display(Name = "Full Name")]
    public string FullName { get; set; } = "";
    [Required, EmailAddress, Services.InstitutionalEmailAttribute, StringLength(256)]
    public string Email { get; set; } = "";
    [Required]
    public string Role { get; set; } = "";
    [Display(Name = "Department")]
    public int? DepartmentId { get; set; }
    [DataType(DataType.Password), Display(Name = "Temporary Password")]
    public string? TemporaryPassword { get; set; }
    public string? EditToken { get; set; }
    [ValidateNever]
    public IReadOnlyList<SelectListItem> Departments { get; set; } = Array.Empty<SelectListItem>();
}

public sealed record UserListViewModel(string Id, string FullName, string Email, string Role, string Department, bool IsActive);
public sealed class ResetPasswordViewModel
{
    [Required, DataType(DataType.Password), Display(Name = "New Temporary Password")]
    public string Password { get; set; } = "";
    [ValidateNever]
    public string UserName { get; set; } = "";
}
