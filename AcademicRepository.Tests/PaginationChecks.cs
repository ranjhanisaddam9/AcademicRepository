using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.EntityFrameworkCore;

internal static partial class IntegrationChecks
{
    public static void PaginationOnly()
    {
        Check(PageRequest.NormalizeSize(10) == 10 && PageRequest.NormalizeSize(20) == 20
            && PageRequest.NormalizeSize(50) == 50 && PageRequest.NormalizeSize(100) == 100,
            "Pagination supports only the configured page sizes");
        Check(PageRequest.NormalizeSize(50000) == 20 && PageRequest.NormalizeSize(-1) == 20,
            "Arbitrary page sizes fall back to the safe default");
        Check(PageRequest.NormalizeSize(25, 50) == 50 && !PageRequest.IsAllowedSize(25),
            "Configured repository page sizes use the same safe choices");
        Check(PageRequest.NormalizePage(0, 201, 20) == 1 && PageRequest.NormalizePage(-10, 201, 20) == 1
            && PageRequest.NormalizePage(999999, 201, 20) == 11,
            "Zero, negative and out-of-range pages normalize safely");

        var page = new PagedResult<int>(Enumerable.Range(1, 20).ToArray(), 3, 10, 21);
        Check(page.TotalPages == 3 && page.HasPreviousPage && !page.HasNextPage,
            "Paged result metadata reports page bounds correctly");
        var empty = new PagedResult<int>(Array.Empty<int>(), 1, 20, 0);
        Check(empty.TotalPages == 1 && !empty.HasPreviousPage && !empty.HasNextPage,
            "Empty result has a stable first page");

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=localhost;Database=PaginationSqlInspection;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var db = new ApplicationDbContext(options);
        var sql = db.ProjectSubmissions.AsNoTracking()
            .Where(s => s.DepartmentId == 42 && s.Status == SubmissionStatus.Submitted)
            .OrderByDescending(s => s.SubmittedAt).ThenByDescending(s => s.Id)
            .Skip(20).Take(10).Select(s => new { s.Id, s.Title }).ToQueryString();
        var where = sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
        var orderBy = sql.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase);
        var offset = sql.IndexOf("OFFSET", StringComparison.OrdinalIgnoreCase);
        Check(where >= 0 && orderBy > where && offset > orderBy && sql.Contains("FETCH NEXT", StringComparison.OrdinalIgnoreCase),
            "EF Core emits scoped filtering and ordering before SQL OFFSET/FETCH pagination");
    }
}
