using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AcademicRepository.Services;

public sealed class AuthenticationCodeHasher
{
    private readonly byte[] key;
    public AuthenticationCodeHasher(IOptions<AuthenticationCodeOptions> options, IWebHostEnvironment environment)
    {
        var configured = options.Value.HmacKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!environment.IsDevelopment()) throw new InvalidOperationException("Configure AuthenticationCodes:HmacKey as a base64 secret of at least 32 random bytes before starting outside Development.");
            key = RandomNumberGenerator.GetBytes(32);
        }
        else
        {
            try { key = Convert.FromBase64String(configured); }
            catch (FormatException) { throw new InvalidOperationException("AuthenticationCodes:HmacKey must be a base64 secret."); }
            if (key.Length < 32) throw new InvalidOperationException("AuthenticationCodes:HmacKey requires at least 32 random bytes.");
        }
    }
    public string Hash(string value) => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value)));
    public bool Matches(string value, string hash) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(value)), Encoding.ASCII.GetBytes(hash));
}

public sealed record VerifiedCode(ApplicationUser User, string? RecoveryGrant);

public sealed class AuthenticationCodeService(ApplicationDbContext db, UserManager<ApplicationUser> users,
    AuthenticationCodeHasher hasher, IDataProtectionProvider protection, IAuthenticationEmailSender emailSender,
    IOptions<AuthenticationCodeOptions> options, TimeProvider clock, ILogger<AuthenticationCodeService> logger,
    StudentDepartmentService departments)
{
    private readonly IDataProtector tokens = protection.CreateProtector("AcademicRepository.AuthenticationCodes.v1");
    private AuthenticationCodeOptions Settings => options.Value;

    public async Task<string> RequestAsync(string email, CodePurpose purpose, CancellationToken cancellationToken = default)
    {
        var nonce = Guid.NewGuid();
        if (!InstitutionalEmail.IsValid(email)) return Challenge(purpose, nonce);
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            if (purpose != CodePurpose.StudentLogin || await departments.ResolveAsync(email) is not { IsActive: true })
                return Challenge(purpose, nonce);
        }
        else if (!await EligibleAsync(user, purpose)) return Challenge(purpose, nonce);
        var normalizedEmail = email.Trim().ToUpperInvariant();
        var now = clock.GetUtcNow();
        var existing = await db.AuthenticationCodes.SingleOrDefaultAsync(c => c.Email == normalizedEmail && c.Purpose == purpose, cancellationToken);
        if (existing is not null)
        {
            if (now - existing.LastSentAt < TimeSpan.FromSeconds(Settings.ResendCooldownSeconds)) return Challenge(purpose, existing.Nonce);
            if (now - existing.WindowStartedAt < TimeSpan.FromHours(1) && existing.SentInWindow >= Settings.MaxRequestsPerHour) return Challenge(purpose, existing.Nonce);
        }
        var entry = existing ?? new AuthenticationCode { Email = normalizedEmail, Purpose = purpose, WindowStartedAt = now };
        entry.UserId = user?.Id;
        if (now - entry.WindowStartedAt >= TimeSpan.FromHours(1)) { entry.WindowStartedAt = now; entry.SentInWindow = 0; }
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        entry.Nonce = nonce;
        entry.SecurityStamp = user?.SecurityStamp ?? "";
        entry.CodeHash = hasher.Hash(CodeValue(entry, code));
        entry.ExpiresAt = now.AddMinutes(Settings.ExpiryMinutes);
        entry.LastSentAt = now;
        entry.SentInWindow++;
        entry.Attempts = 0;
        entry.ConsumedAt = null;
        entry.RecoveryGrantHash = null;
        entry.RecoveryGrantExpiresAt = null;
        if (existing is null) db.AuthenticationCodes.Add(entry);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { return Challenge(purpose, Guid.NewGuid()); }
        try { await emailSender.SendCodeAsync(user?.Email ?? email.Trim(), code, purpose, cancellationToken); }
        catch (Exception exception)
        {
            logger.LogWarning("Authentication email delivery failed ({ExceptionType}). Verify email configuration.", exception.GetType().Name);
            entry.ConsumedAt = now;
            // Persist invalidation even when the HTTP request or Graph timeout was cancelled.
            try { await db.SaveChangesAsync(CancellationToken.None); } catch (DbUpdateException) { }
        }
        return Challenge(purpose, nonce);
    }

    public async Task<VerifiedCode?> VerifyAsync(string token, string code, CodePurpose purpose)
    {
        var parts = Read(token);
        if (parts is null || parts.Length != 2 || parts[0] != purpose.ToString() || !Guid.TryParse(parts[1], out var nonce)) return null;
        var entry = await db.AuthenticationCodes.SingleOrDefaultAsync(c => c.Nonce == nonce && c.Purpose == purpose);
        var now = clock.GetUtcNow();
        if (entry is null || entry.ConsumedAt.HasValue || entry.ExpiresAt <= now || entry.Attempts >= Settings.MaxAttempts) return null;
        var user = entry.UserId is null ? null : await users.FindByIdAsync(entry.UserId);
        entry.Attempts++;
        var valid = code.Length == 6 && code.All(c => c is >= '0' and <= '9') && hasher.Matches(CodeValue(entry, code), entry.CodeHash);
        Department? newDepartment = null;
        if (valid && entry.UserId is null)
        {
            newDepartment = purpose == CodePurpose.StudentLogin ? await departments.ResolveAsync(entry.Email) : null;
            valid = newDepartment is { IsActive: true } && await users.FindByEmailAsync(entry.Email) is null;
        }
        else if (valid)
            valid = await EligibleAsync(user, purpose) && entry.SecurityStamp == user!.SecurityStamp
                && string.Equals(entry.Email, user.Email, StringComparison.OrdinalIgnoreCase);
        if (!valid)
        {
            try { await db.SaveChangesAsync(); } catch (DbUpdateConcurrencyException) { db.Entry(entry).State = EntityState.Detached; }
            return null;
        }
        // Consume and provision in the same transaction. The rowversion permits only one winner.
        await using var transaction = await db.Database.BeginTransactionAsync();
        string? grant = null;
        try
        {
            entry.ConsumedAt = now;
            await db.SaveChangesAsync();
            if (entry.UserId is null)
            {
                user = new ApplicationUser { UserName = entry.Email.ToLowerInvariant(), Email = entry.Email.ToLowerInvariant(),
                    StudentNumber = StudentDepartmentService.StudentNumberFromEmail(entry.Email),
                    FullName = entry.Email[..entry.Email.IndexOf('@')], DepartmentId = newDepartment!.Id, EmailConfirmed = true };
                if (!(await users.CreateAsync(user)).Succeeded || !(await users.AddToRoleAsync(user, "Student")).Succeeded)
                {
                    await transaction.RollbackAsync();
                    db.ChangeTracker.Clear();
                    return null;
                }
                entry.UserId = user.Id;
                entry.SecurityStamp = user.SecurityStamp ?? "";
            }
            if (purpose == CodePurpose.StaffRecovery)
            {
                var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                entry.RecoveryGrantHash = hasher.Hash(secret);
                entry.RecoveryGrantExpiresAt = now.AddMinutes(Settings.RecoveryGrantExpiryMinutes);
                grant = tokens.Protect($"grant\n{entry.Id}\n{secret}");
            }
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync();
            db.ChangeTracker.Clear();
            return null;
        }
        return new VerifiedCode(user!, grant);
    }

    public async Task<IdentityResult> ResetStaffPasswordAsync(string token, string password)
    {
        var invalid = IdentityResult.Failed(new IdentityError { Code = "InvalidRecovery", Description = "The recovery session is invalid or expired. Request a new code." });
        var parts = Read(token);
        if (parts is null || parts.Length != 3 || parts[0] != "grant" || !int.TryParse(parts[1], out var id)) return invalid;
        await using var transaction = await db.Database.BeginTransactionAsync();
        var entry = await db.AuthenticationCodes.SingleOrDefaultAsync(c => c.Id == id && c.Purpose == CodePurpose.StaffRecovery);
        if (entry?.RecoveryGrantHash is null || entry.RecoveryGrantExpiresAt <= clock.GetUtcNow() || !hasher.Matches(parts[2], entry.RecoveryGrantHash)) return invalid;
        var user = entry.UserId is null ? null : await users.FindByIdAsync(entry.UserId);
        if (!await EligibleAsync(user, CodePurpose.StaffRecovery) || entry.SecurityStamp != user!.SecurityStamp) return invalid;
        var resetToken = await users.GeneratePasswordResetTokenAsync(user!);
        var result = await users.ResetPasswordAsync(user!, resetToken, password);
        if (!result.Succeeded) return result;
        entry.RecoveryGrantHash = null;
        entry.RecoveryGrantExpiresAt = null;
        try { await db.SaveChangesAsync(); await transaction.CommitAsync(); }
        catch (DbUpdateConcurrencyException) { return invalid; }
        return IdentityResult.Success;
    }

    private async Task<bool> EligibleAsync(ApplicationUser? user, CodePurpose purpose)
    {
        if (user is null) return false;
        var roles = await users.GetRolesAsync(user);
        if (!ApplicationRoles.CanAuthenticate(user, roles)) return false;
        return purpose == CodePurpose.StudentLogin ? roles.Contains("Student") && await departments.MatchesAsync(user)
            : !roles.Contains("Student") && roles.Any(ApplicationRoles.Staff.Contains);
    }
    private string Challenge(CodePurpose purpose, Guid nonce) => tokens.Protect($"{purpose}\n{nonce}");
    private static string CodeValue(AuthenticationCode entry, string code) => $"{entry.Email}\n{entry.UserId}\n{entry.Purpose}\n{entry.Nonce}\n{entry.SecurityStamp}\n{code}";
    private string[]? Read(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 2048) return null;
        try { return tokens.Unprotect(token).Split('\n'); }
        catch (CryptographicException) { return null; }
    }
}
