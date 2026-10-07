using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using AcademicRepository.Models;
using Microsoft.AspNetCore.Identity;

namespace AcademicRepository.Services;

public static class InstitutionalEmail
{
    public static bool IsValid(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        var value = email.Trim();
        return MailAddress.TryCreate(value, out var address)
            && string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase)
            && string.Equals(address.Host, "smiu.edu.pk", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class InstitutionalEmailAttribute : ValidationAttribute
{
    public InstitutionalEmailAttribute() => ErrorMessage = "Use a valid @smiu.edu.pk email address.";
    public override bool IsValid(object? value) => InstitutionalEmail.IsValid(value as string);
}

public sealed class InstitutionalUserValidator : IUserValidator<ApplicationUser>
{
    public Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user) =>
        Task.FromResult(InstitutionalEmail.IsValid(user.Email) ? IdentityResult.Success : IdentityResult.Failed(
            new IdentityError { Code = "InstitutionalEmailRequired", Description = "Use a valid @smiu.edu.pk email address." }));
}

public static class ApplicationRoles
{
    public static readonly string[] Staff = ["Admin", "Coordinator", "DepartmentHead", "ORICQEC"];
    public static bool RequiresDepartment(string role) => role is "Student" or "Coordinator" or "DepartmentHead";
    public static bool CanAuthenticate(ApplicationUser user, IEnumerable<string> roles) =>
        user.IsActive && InstitutionalEmail.IsValid(user.Email)
        && roles.Any(r => r == "Student" || Staff.Contains(r))
        && (!roles.Any(RequiresDepartment) || user.DepartmentId.HasValue);
}
