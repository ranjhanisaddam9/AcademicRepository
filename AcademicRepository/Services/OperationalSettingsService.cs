using System.Globalization;
using System.ComponentModel.DataAnnotations;
using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AcademicRepository.Services;

public interface IOperationalSettingsService
{
    Task<OperationalSettingsViewModel> GetAsync(CancellationToken cancellationToken = default);
    Task UpdateAsync(OperationalSettingsViewModel value, string updatedBy, CancellationToken cancellationToken = default);
}

public sealed class OperationalSettingsService(ApplicationDbContext db, IConfiguration configuration,
    IOptions<FileStorageOptions> files, IOptions<AuthenticationCodeOptions> otp, TimeProvider clock) : IOperationalSettingsService
{
    private static readonly (string Key, string Description)[] Descriptions =
    [
        ("MaxFileSizeMB", "Maximum size for one uploaded resource in megabytes."),
        ("MaxFilesPerSubmission", "Maximum active resource files attached to one submission."),
        ("OtpExpiryMinutes", "Lifetime of a student or staff verification code in minutes."),
        ("OtpMaxAttempts", "Maximum verification attempts for one code."),
        ("OtpResendCooldownSeconds", "Minimum time between code requests for one email."),
        ("RepositoryPageSize", "Default repository results per page: 10, 20, 50 or 100."),
        ("DefaultAcademicYear", "Current academic-year label used in repository summaries and future defaults."),
        ("ApplicationDisplayName", "Name displayed in the application header."),
        ("SupportEmail", "Institutional contact address shown to users.")
    ];

    public async Task<OperationalSettingsViewModel> GetAsync(CancellationToken cancellationToken = default)
    {
        // The shared Razor layout reads settings on every request. Load values and their small
        // accountability summary together to avoid three round trips per rendered page.
        var rows = await (from setting in db.SystemSettings.AsNoTracking()
                          join user in db.Users.AsNoTracking() on setting.UpdatedByUserId equals user.Id into updaterUsers
                          from updater in updaterUsers.DefaultIfEmpty()
                          select new
                          {
                              setting.Key,
                              setting.Value,
                              setting.UpdatedAt,
                              UpdatedBy = setting.Key == "ApplicationDisplayName" ? updater!.FullName : null
                          }).ToListAsync(cancellationToken);
        var values = rows.ToDictionary(s => s.Key, s => s.Value);
        var now = clock.GetUtcNow();
        var yearStart = now.Month >= 7 ? now.Year : now.Year - 1;
        var model = new OperationalSettingsViewModel
        {
            MaxFileSizeMB = Parse("MaxFileSizeMB", files.Value.MaxFileSizeMB, 1, 100),
            MaxFilesPerSubmission = Parse("MaxFilesPerSubmission", files.Value.MaxFilesPerSubmission, 1, 100),
            OtpExpiryMinutes = Parse("OtpExpiryMinutes", otp.Value.ExpiryMinutes, 1, 30),
            OtpMaxAttempts = Parse("OtpMaxAttempts", otp.Value.MaxAttempts, 1, 10),
            OtpResendCooldownSeconds = Parse("OtpResendCooldownSeconds", otp.Value.ResendCooldownSeconds, 1, 600),
            RepositoryPageSize = PageRequest.NormalizeSize(Parse("RepositoryPageSize", 20, 10, 100)),
            DefaultAcademicYear = Get("DefaultAcademicYear", configuration["Repository:CurrentAcademicYear"] ?? $"{yearStart}-{yearStart + 1}"),
            ApplicationDisplayName = Get("ApplicationDisplayName", configuration["Application:DisplayName"] ?? "Academic Project Repository"),
            SupportEmail = NullIfBlank(Get("SupportEmail", configuration["Application:SupportEmail"] ?? "")),
            UpdatedAt = rows.Max(s => (DateTime?)s.UpdatedAt),
            UpdatedBy = rows.SingleOrDefault(s => s.Key == "ApplicationDisplayName")?.UpdatedBy
        };
        ValidatePersisted(model);
        return model;

        string Get(string key, string fallback) => values.TryGetValue(key, out var value) ? value : fallback;
        int Parse(string key, int fallback, int minimum, int maximum) => values.TryGetValue(key, out var value)
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= minimum && parsed <= maximum
                ? parsed : fallback;
    }

    public async Task UpdateAsync(OperationalSettingsViewModel value, string updatedBy, CancellationToken cancellationToken = default)
    {
        value.DefaultAcademicYear = value.DefaultAcademicYear.Trim();
        value.ApplicationDisplayName = value.ApplicationDisplayName.Trim();
        value.SupportEmail = NullIfBlank(value.SupportEmail?.Trim());
        ValidatePersisted(value);
        var supplied = new Dictionary<string, (string Value, string Type)>(StringComparer.OrdinalIgnoreCase)
        {
            ["MaxFileSizeMB"] = (Format(value.MaxFileSizeMB), "Integer"),
            ["MaxFilesPerSubmission"] = (Format(value.MaxFilesPerSubmission), "Integer"),
            ["OtpExpiryMinutes"] = (Format(value.OtpExpiryMinutes), "Integer"),
            ["OtpMaxAttempts"] = (Format(value.OtpMaxAttempts), "Integer"),
            ["OtpResendCooldownSeconds"] = (Format(value.OtpResendCooldownSeconds), "Integer"),
            ["RepositoryPageSize"] = (Format(value.RepositoryPageSize), "Integer"),
            ["DefaultAcademicYear"] = (value.DefaultAcademicYear, "String"),
            ["ApplicationDisplayName"] = (value.ApplicationDisplayName, "String"),
            ["SupportEmail"] = (value.SupportEmail ?? "", "String")
        };
        var entries = await db.SystemSettings.ToDictionaryAsync(s => s.Key, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var (key, (settingValue, type)) in supplied)
        {
            if (!entries.TryGetValue(key, out var entry))
            {
                entry = new SystemSetting { Key = key };
                db.SystemSettings.Add(entry);
            }
            entry.Value = settingValue;
            entry.DataType = type;
            entry.Description = Descriptions.Single(d => d.Key == key).Description;
            entry.UpdatedAt = now;
            entry.UpdatedByUserId = updatedBy;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static void ValidatePersisted(OperationalSettingsViewModel model)
    {
        var context = new ValidationContext(model);
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(model, context, errors, validateAllProperties: true))
            throw new InvalidOperationException("Operational settings contain invalid values. Restore valid configuration before using the settings page.");
        if (model.SupportEmail is not null && !InstitutionalEmail.IsValid(model.SupportEmail))
            throw new InvalidOperationException("SupportEmail must be a valid @smiu.edu.pk address.");
    }
}
