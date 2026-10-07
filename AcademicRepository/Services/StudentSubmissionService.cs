using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.EntityFrameworkCore;

namespace AcademicRepository.Services;

// Queries always scope private submission data to the authenticated student's ID.
public sealed class StudentSubmissionService(ApplicationDbContext db)
{
    public IQueryable<ProjectSubmission> OwnedBy(string studentId) => db.ProjectSubmissions.Where(s => s.StudentId == studentId);

    public Task<List<SubmissionListViewModel>> ListAsync(string studentId, int? limit = null)
    {
        var query = OwnedBy(studentId).AsNoTracking().OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .Select(s => new SubmissionListViewModel(s.Id, s.Title, s.ProjectType, s.Department.Name, s.CreatedAt, s.SubmittedAt, s.Status));
        return (limit.HasValue ? query.Take(limit.Value) : query).ToListAsync();
    }

    public async Task<StudentDashboardViewModel> DashboardAsync(string studentId, DashboardViewModel profile)
    {
        var counts = await OwnedBy(studentId).GroupBy(s => s.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync();
        int Count(SubmissionStatus status) => counts.SingleOrDefault(c => c.Status == status)?.Count ?? 0;
        return new StudentDashboardViewModel(profile, counts.Sum(c => c.Count), Count(SubmissionStatus.Draft), Count(SubmissionStatus.Submitted),
            Count(SubmissionStatus.Approved), Count(SubmissionStatus.Rejected), await ListAsync(studentId, 5), Count(SubmissionStatus.Revision));
    }
}
