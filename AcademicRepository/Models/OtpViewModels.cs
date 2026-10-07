using System.ComponentModel.DataAnnotations;
using AcademicRepository.Services;

namespace AcademicRepository.Models;

public sealed class RequestCodeViewModel
{
    [Required, EmailAddress, InstitutionalEmailAttribute, StringLength(256)]
    public string Email { get; set; } = "";
}
public sealed class VerifyCodeViewModel
{
    [Required, StringLength(2048)]
    public string ChallengeToken { get; set; } = "";
    [Required, RegularExpression("^[0-9]{6}$", ErrorMessage = "Enter the six-digit verification code."), Display(Name = "Verification Code")]
    public string Code { get; set; } = "";
    public string Email { get; set; } = "";
}
public sealed class StaffResetPasswordViewModel
{
    [Required, StringLength(2048)]
    public string GrantToken { get; set; } = "";
    [Required, DataType(DataType.Password), Display(Name = "New Password")]
    public string Password { get; set; } = "";
    [Required, DataType(DataType.Password), Compare(nameof(Password)), Display(Name = "Confirm Password")]
    public string ConfirmPassword { get; set; } = "";
}
