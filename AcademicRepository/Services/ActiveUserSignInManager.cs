using AcademicRepository.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AcademicRepository.Services;

public sealed class ActiveUserSignInManager(
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation, StudentDepartmentService departments)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(ApplicationUser user)
    {
        var roles = await UserManager.GetRolesAsync(user);
        return ApplicationRoles.CanAuthenticate(user, roles)
            && (!roles.Contains("Student") || await departments.MatchesAsync(user)) && await base.CanSignInAsync(user);
    }

    public override async Task<Microsoft.AspNetCore.Identity.SignInResult> PasswordSignInAsync(ApplicationUser user, string password, bool isPersistent, bool lockoutOnFailure)
    {
        var roles = await UserManager.GetRolesAsync(user);
        if (roles.Contains("Student") || !roles.Any(ApplicationRoles.Staff.Contains)) return Microsoft.AspNetCore.Identity.SignInResult.Failed;
        return await base.PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure);
    }
}
