using System.Globalization;
using System.Text;
using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.EntityFrameworkCore;

namespace AcademicRepository.Services;

public interface IRepositoryReportService
{
    Task<InstitutionRepositoryReport> GetInstitutionSummaryAsync(string userId);
    Task WriteCsvAsync(string userId, RepositoryFilterViewModel filter, Stream destination, CancellationToken cancellationToken = default);
}

public sealed class RepositoryReportService(ApplicationDbContext db, RepositoryService repository,
    IOperationalSettingsService operationalSettings) : IRepositoryReportService
{
    public async Task<InstitutionRepositoryReport> GetInstitutionSummaryAsync(string userId)
    {
        // Authorization and approved eligibility come from the repository's single source of truth.
        var query = await repository.InstitutionQueryAsync(userId, new());
        var inconsistent = await repository.HasInconsistentInstitutionRecordsAsync(userId);
        // Validate private storage in bounded batches, retaining only unavailable version IDs.
        // Aggregations remain database-side; no complete project entities are loaded.
        var unavailable = new List<int>();
        var cursor = 0;
        while (true)
        {
            var batch = await query.Where(r => r.Id > cursor).OrderBy(r => r.Id)
                .Select(r => new { r.Id, VersionId = r.SubmissionVersionId!.Value }).Take(200).ToListAsync();
            if (batch.Count == 0) break;
            var available = await repository.AvailableVersionIdsAsync(batch.Select(r => r.VersionId).ToArray());
            unavailable.AddRange(batch.Where(r => !available.Contains(r.VersionId)).Select(r => r.VersionId));
            cursor = batch[^1].Id;
        }
        inconsistent |= unavailable.Count > 0;
        if (unavailable.Count > 0) query = query.Where(r => !unavailable.Contains(r.SubmissionVersionId!.Value));
        var total = await query.CountAsync();
        var types = await query.GroupBy(r => r.SubmissionVersion!.ProjectTypeSnapshot)
            .Select(g => new RepositoryTypeCount(g.Key, g.Count())).ToListAsync();
        var years = await query.GroupBy(r => r.SubmissionVersion!.AcademicYearSnapshot).OrderByDescending(g => g.Key)
            .Select(g => new RepositoryGroupCount(g.Key ?? "Not recorded", g.Count())).ToListAsync();
        var semesters = await query.GroupBy(r => r.SubmissionVersion!.SemesterSnapshot).OrderBy(g => g.Key)
            .Select(g => new RepositoryGroupCount(g.Key ?? "Not recorded", g.Count())).ToListAsync();
        var trend = await query.GroupBy(r => new { r.CompletedAt!.Value.Year, r.CompletedAt.Value.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => new RepositoryApprovalTrend(g.Key.Year, g.Key.Month, g.Count())).ToListAsync();
        var counts = await query.GroupBy(r => new { r.ProjectSubmission.DepartmentId, r.SubmissionVersion!.ProjectTypeSnapshot })
            .Select(g => new { g.Key.DepartmentId, Type = g.Key.ProjectTypeSnapshot, Count = g.Count() }).ToListAsync();
        var departments = await db.Departments.AsNoTracking().Where(d => d.IsActive || query.Any(r => r.ProjectSubmission.DepartmentId == d.Id))
            .OrderBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync();
        var comparisons = departments.Select(d => new RepositoryDepartmentComparison(d.Id, d.Name,
            counts.Where(c => c.DepartmentId == d.Id && c.Type == ProjectType.Assignment).Sum(c => c.Count),
            counts.Where(c => c.DepartmentId == d.Id && c.Type == ProjectType.SemesterProject).Sum(c => c.Count),
            counts.Where(c => c.DepartmentId == d.Id && c.Type == ProjectType.FinalYearProject).Sum(c => c.Count),
            counts.Where(c => c.DepartmentId == d.Id && c.Type == ProjectType.ResearchProject).Sum(c => c.Count))).ToList();
        var currentYear = (await operationalSettings.GetAsync()).DefaultAcademicYear;
        var currentCount = await query.CountAsync(r => r.SubmissionVersion!.AcademicYearSnapshot == currentYear);
        var recent = await query.OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.ProjectSubmissionId).Take(5)
            .Select(r => new RepositoryListItem(r.ProjectSubmissionId, r.SubmissionVersionId!.Value, r.SubmissionVersion!.VersionNumber,
                r.SubmissionVersion.TitleSnapshot, r.ProjectSubmission.Student.FullName, r.ProjectSubmission.Student.StudentNumber,
                r.SubmissionVersion.Department.Name, r.SubmissionVersion.ProjectTypeSnapshot, r.SubmissionVersion.AcademicYearSnapshot,
                r.SubmissionVersion.SemesterSnapshot, r.SubmissionVersion.SupervisorNameSnapshot, r.SubmissionVersion.KeywordsSnapshot, r.CompletedAt!.Value)).ToListAsync();
        return new(total, await db.Departments.CountAsync(d => d.IsActive), currentYear, currentCount, comparisons, types, years, semesters, trend, recent, inconsistent);
    }

    public async Task WriteCsvAsync(string userId, RepositoryFilterViewModel filter, Stream destination, CancellationToken cancellationToken = default)
    {
        // Screen and export share authorization, eligibility, filters and sorting. Export ignores pagination.
        var query = await repository.InstitutionQueryAsync(userId, filter);
        await using var writer = new StreamWriter(destination, new UTF8Encoding(true), 16 * 1024, leaveOpen: true);
        await CsvWriter.WriteRowAsync(writer, ["Title", "Student Name", "Student ID", "Student Email", "Department", "Project Type", "Academic Year", "Semester",
            "Supervisor", "Course Name", "Course Code", "Keywords", "Approved Date (UTC)", "Approved Version", "Review Round"], cancellationToken);
        var offset = 0;
        while (true)
        {
            var batch = await RepositoryService.OrderedReviews(query, filter.Sort).Skip(offset).Take(200)
                .Select(r => new RepositoryCsvRow(r.SubmissionVersion!.TitleSnapshot, r.ProjectSubmission.Student.FullName,
                    r.ProjectSubmission.Student.StudentNumber, r.ProjectSubmission.Student.Email, r.SubmissionVersion.Department.Name,
                    r.SubmissionVersion.ProjectTypeSnapshot, r.SubmissionVersion.AcademicYearSnapshot, r.SubmissionVersion.SemesterSnapshot,
                    r.SubmissionVersion.SupervisorNameSnapshot, r.SubmissionVersion.CourseNameSnapshot, r.SubmissionVersion.CourseCodeSnapshot,
                    r.SubmissionVersion.KeywordsSnapshot, r.CompletedAt!.Value, r.SubmissionVersion.VersionNumber, r.ReviewRound, r.SubmissionVersionId!.Value))
                .ToListAsync(cancellationToken);
            if (batch.Count == 0) break;
            var available = await repository.AvailableVersionIdsAsync(batch.Select(r => r.VersionId).ToArray(), cancellationToken);
            foreach (var row in batch.Where(r => available.Contains(r.VersionId)))
                await CsvWriter.WriteRowAsync(writer, [row.Title, row.StudentName, row.StudentNumber, row.StudentEmail, row.Department,
                    row.ProjectType.ToString(), row.AcademicYear, row.Semester, row.Supervisor, row.CourseName, row.CourseCode, row.Keywords,
                    row.ApprovedAt.ToString("u", CultureInfo.InvariantCulture), row.VersionNumber.ToString(CultureInfo.InvariantCulture), row.ReviewRound.ToString(CultureInfo.InvariantCulture)], cancellationToken);
            offset += batch.Count;
        }
        await writer.FlushAsync(cancellationToken);
    }
}

public static class CsvWriter
{
    public static async Task WriteRowAsync(TextWriter writer, IEnumerable<string?> values, CancellationToken cancellationToken = default)
    {
        var cells = values.Select(value =>
        {
            var text = value ?? "";
            // Quoting alone does not stop spreadsheet formulas. Check beyond leading whitespace/control characters.
            var index = 0;
            while (index < text.Length && (char.IsWhiteSpace(text[index]) || char.IsControl(text[index]) || text[index] == '\uFEFF')) index++;
            if ((index < text.Length && "=+-@".Contains(text[index])) || (text.Length > 0 && text[0] is '\t' or '\r' or '\n')) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        });
        await writer.WriteLineAsync(string.Join(",", cells).AsMemory(), cancellationToken);
    }
}
