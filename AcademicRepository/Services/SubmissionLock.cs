using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.EntityFrameworkCore;

namespace AcademicRepository.Services;

// Call inside a transaction. File mutations and submission transitions share the same SQL row lock.
public sealed class SubmissionLock(ApplicationDbContext db)
{
    public async Task<ProjectSubmission?> DepartmentAsync(int id, int departmentId)
    {
        var submission = await db.ProjectSubmissions
            .FromSqlInterpolated($"SELECT * FROM ProjectSubmissions WITH (UPDLOCK, HOLDLOCK) WHERE Id={id} AND DepartmentId={departmentId} AND Status IN (1,2,3,4)")
            .SingleOrDefaultAsync();
        // A reused service scope must not decide from an older tracked copy.
        if (submission is not null) await db.Entry(submission).ReloadAsync();
        return submission;
    }
    public async Task<ProjectSubmission?> OwnedAsync(int id, string studentId)
    {
        var submission = await db.ProjectSubmissions.FromSqlInterpolated($"SELECT * FROM ProjectSubmissions WITH (UPDLOCK, HOLDLOCK) WHERE Id={id} AND StudentId={studentId}").SingleOrDefaultAsync();
        if (submission is not null) await db.Entry(submission).ReloadAsync();
        return submission;
    }
}
