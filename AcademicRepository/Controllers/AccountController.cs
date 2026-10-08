using AcademicRepository.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using AcademicRepository.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace AcademicRepository.Controllers;

public class AccountController(SignInManager<ApplicationUser> signInManager, AuthenticationCodeService codes,
    UserManager<ApplicationUser> users, ILogger<AccountController> logger) : Controller
{
    private const string SentMessage = "If an eligible active account exists, a verification code has been sent. Check your email. Resends are subject to a cooldown.";
    [AllowAnonymous, HttpGet]
    public IActionResult Login(string? returnUrl = null) => View(new LoginViewModel { ReturnUrl = returnUrl });

    [AllowAnonymous, HttpPost, EnableRateLimiting("authentication")]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        model.SignInMode = "staff";
        if (!ModelState.IsValid) return View(model);
        var result = await signInManager.PasswordSignInAsync(model.Email.Trim(), model.Password, model.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            var user = await users.FindByNameAsync(model.Email.Trim());
            var roles = user is null ? Array.Empty<string>() : (await users.GetRolesAsync(user)).ToArray();
            logger.LogInformation("Authentication completed. Action={Action} UserId={UserId} Role={Role} Result={Result}",
                "StaffPasswordLogin", user?.Id, string.Join(",", roles), "Success");
            return Url.IsLocalUrl(model.ReturnUrl) ? LocalRedirect(model.ReturnUrl!) : RedirectToAction("Index", "Home");
        }
        logger.LogWarning("Authentication failed. Action={Action} Role={Role} Result={Result}",
            "StaffPasswordLogin", "Staff", result.IsLockedOut ? "LockedOut" : "InvalidCredentials");
        ModelState.AddModelError("", result.IsLockedOut ? "Account temporarily locked. Try again later." : "Invalid login attempt.");
        return View(model);
    }

    [AllowAnonymous, HttpGet]
    public IActionResult StudentSignIn() => View("RequestCode", new RequestCodeViewModel());

    [AllowAnonymous, HttpPost, EnableRateLimiting("authentication")]
    public async Task<IActionResult> StudentSignIn(RequestCodeViewModel model)
    {
        if (!ModelState.IsValid) return View("RequestCode", model);
        var token = await codes.RequestAsync(model.Email, CodePurpose.StudentLogin, HttpContext.RequestAborted);
        ViewData["Status"] = SentMessage;
        return View("VerifyStudentCode", new VerifyCodeViewModel { Email = model.Email, ChallengeToken = token });
    }

    [AllowAnonymous, HttpPost, EnableRateLimiting("authentication")]
    public async Task<IActionResult> VerifyStudentCode(VerifyCodeViewModel model)
    {
        if (ModelState.IsValid)
        {
            var verified = await codes.VerifyAsync(model.ChallengeToken, model.Code, CodePurpose.StudentLogin);
            if (verified is not null && await signInManager.CanSignInAsync(verified.User))
            {
                await signInManager.SignInAsync(verified.User, isPersistent: false);
                return RedirectToAction("Dashboard", "Student");
            }
            ModelState.AddModelError("", "Invalid or expired verification code. Request a new code if needed.");
        }
        return View(model);
    }

    [AllowAnonymous, HttpGet]
    public IActionResult ForgotPassword() => View("RequestCode", new RequestCodeViewModel());

    [AllowAnonymous, HttpPost, EnableRateLimiting("authentication")]
    public async Task<IActionResult> ForgotPassword(RequestCodeViewModel model)
    {
        if (!ModelState.IsValid) return View("RequestCode", model);
        var token = await codes.RequestAsync(model.Email, CodePurpose.StaffRecovery, HttpContext.RequestAborted);
        ViewData["Status"] = SentMessage;
        return View("VerifyRecoveryCode", new VerifyCodeViewModel { Email = model.Email, ChallengeToken = token });
    }

    [AllowAnonymous, HttpPost, EnableRateLimiting("authentication")]
    public async Task<IActionResult> VerifyRecoveryCode(VerifyCodeViewModel model)
    {
        if (ModelState.IsValid)
        {
            var verified = await codes.VerifyAsync(model.ChallengeToken, model.Code, CodePurpose.StaffRecovery);
            if (verified?.RecoveryGrant is not null)
            {
                ModelState.Clear();
                return View("ResetStaffPassword", new StaffResetPasswordViewModel { GrantToken = verified.RecoveryGrant });
            }
            ModelState.AddModelError("", "Invalid or expired verification code. Request a new code if needed.");
        }
        return View(model);
    }

    [AllowAnonymous, HttpPost, EnableRateLimiting("authentication")]
    public async Task<IActionResult> ResetStaffPassword(StaffResetPasswordViewModel model)
    {
        if (ModelState.IsValid)
        {
            var result = await codes.ResetStaffPasswordAsync(model.GrantToken, model.Password);
            if (result.Succeeded)
            {
                TempData["Status"] = "Password reset successfully. Sign in with your new password.";
                return RedirectToAction(nameof(Login));
            }
            foreach (var error in result.Errors) ModelState.AddModelError("", error.Description);
        }
        model.Password = model.ConfirmPassword = "";
        ModelState.SetModelValue(nameof(model.Password), null, null);
        ModelState.SetModelValue(nameof(model.ConfirmPassword), null, null);
        return View(model);
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> Logout()
    {
        var userId = users.GetUserId(User);
        var user = userId is null ? null : await users.FindByIdAsync(userId);
        var roles = user is null ? Array.Empty<string>() : (await users.GetRolesAsync(user)).ToArray();
        logger.LogInformation("Authentication completed. Action={Action} UserId={UserId} Role={Role} Result={Result}",
            "Logout", userId, string.Join(",", roles), "Success");
        await signInManager.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous, HttpGet]
    public IActionResult AccessDenied()
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View();
    }
}
