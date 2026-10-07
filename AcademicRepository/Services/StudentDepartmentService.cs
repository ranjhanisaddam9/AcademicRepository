using System.Text.RegularExpressions;
using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.EntityFrameworkCore;

namespace AcademicRepository.Services;

public sealed partial class StudentDepartmentService(ApplicationDbContext db)
{
    // CSC20F005: department prefix, two-digit year, Fall/Spring, three-digit sequence.
    [GeneratedRegex(@"\A(?<keyword>[A-Za-z]+)(?<intake>[0-9]{2}[FfSs])(?<sequence>[0-9]{3})\z", RegexOptions.CultureInvariant)]
    private static partial Regex StudentIdPattern();

    public static string? StudentNumberFromEmail(string? email)
    {
        if (!InstitutionalEmail.IsValid(email)) return null;
        var value = email!.Trim();
        var number = value[..value.LastIndexOf('@')];
        var match = StudentIdPattern().Match(number);
        return match.Success
            ? $"{match.Groups["keyword"].Value}-{match.Groups["intake"].Value}-{match.Groups["sequence"].Value}".ToUpperInvariant()
            : null;
    }

    public static string? KeywordFromEmail(string? email)
    {
        if (!InstitutionalEmail.IsValid(email)) return null;
        var value = email!.Trim();
        var match = StudentIdPattern().Match(value[..value.LastIndexOf('@')]);
        return match.Success ? match.Groups["keyword"].Value.ToUpperInvariant() : null;
    }

    public async Task<Department?> ResolveAsync(string? email)
    {
        var keyword = KeywordFromEmail(email);
        return keyword is null ? null : await db.Departments.SingleOrDefaultAsync(d => d.StudentEmailKeyword == keyword);
    }

    public async Task<bool> MatchesAsync(ApplicationUser user)
    {
        var department = await ResolveAsync(user.Email);
        return department is not null && user.DepartmentId == department.Id
            && user.StudentNumber is not null
            && string.Equals(user.StudentNumber, StudentNumberFromEmail(user.Email), StringComparison.OrdinalIgnoreCase);
    }
}
