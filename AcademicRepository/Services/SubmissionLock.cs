using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.EntityFrameworkCore;

namespace AcademicRepository.Services;

// Call inside a transaction. File mutations and submission transitions share the same SQL row lock.
public sealed class SubmissionLock(ApplicationDbContext db)
{
    public Task<ProjectSubmission?> OwnedAsync(int id, string studentId) => db.ProjectSubmissions
        .FromSqlInterpolated($"SELECT * FROM ProjectSubmissions WITH (UPDLOCK, HOLDLOCK) WHERE Id={id} AND StudentId={studentId}")
        .SingleOrDefaultAsync();
}
