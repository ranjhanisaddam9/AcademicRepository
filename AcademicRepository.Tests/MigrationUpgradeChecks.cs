using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

internal static partial class IntegrationChecks
{
    static async Task MigrationUpgradeChecks()
    {
        var database = "AcademicRepository_UpgradeTest_" + Guid.NewGuid().ToString("N");
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer($"Server=.\\SQLEXPRESS;Database={database};Trusted_Connection=True;TrustServerCertificate=True").Options);
        try
        {
            await db.GetService<IMigrator>().MigrateAsync("20261006081334_AddOtpAuthenticationAndOricQec");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO Departments (Name,Code,IsActive,CreatedAt) VALUES ('Business Administration','BUS01',1,'2025-01-01');
                INSERT INTO AspNetRoles (Id,Name,NormalizedName) VALUES ('student-role','Student','STUDENT');
                INSERT INTO AspNetUsers (Id,UserName,NormalizedUserName,Email,NormalizedEmail,EmailConfirmed,FullName,DepartmentId,IsActive,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount)
                VALUES ('preserved-student','bus20f001@smiu.edu.pk','BUS20F001@SMIU.EDU.PK','bus20f001@smiu.edu.pk','BUS20F001@SMIU.EDU.PK',1,'Preserved Student',1,1,0,0,0,0);
                INSERT INTO AspNetUserRoles VALUES ('preserved-student','student-role');
                INSERT INTO ProjectSubmissions (Title,Abstract,Keywords,ProjectType,StudentId,DepartmentId,Status,CreatedAt)
                VALUES ('Preserved draft','Prior abstract','prior keywords',0,'preserved-student',1,0,'2025-01-01');
                INSERT INTO AuthenticationCodes (UserId,Purpose,Nonce,CodeHash,SecurityStamp,ExpiresAt,Attempts,LastSentAt,WindowStartedAt,SentInWindow)
                VALUES ('preserved-student',0,NEWID(),'old-digest','',DATEADD(minute,10,SYSUTCDATETIME()),0,SYSUTCDATETIME(),SYSUTCDATETIME(),1);
                """);
            await db.GetService<IMigrator>().MigrateAsync("20261007045246_AddProjectFiles");
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE ProjectSubmissions SET Status=2 WHERE Id=1;
                INSERT INTO ProjectFiles (ProjectSubmissionId,OriginalFileName,StoredFileName,ContentType,FileExtension,FileSize,ResourceType,UploadedAt,UploadedByUserId)
                VALUES (1,'Preserved.pdf','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.pdf','application/pdf','.pdf',10,0,'2025-01-01','preserved-student');
                """);
            var before = await db.Database.SqlQueryRaw<byte[]>("SELECT RowVersion AS Value FROM ProjectSubmissions WHERE Id=1").SingleAsync();
            await db.GetService<IMigrator>().MigrateAsync("20261007051802_AddSubmissionReviews");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO ProjectSubmissions (Title,Abstract,Keywords,ProjectType,StudentId,DepartmentId,Status,CreatedAt,SubmittedAt)
                VALUES ('Known submitted','Known abstract','known',2,'preserved-student',1,1,'2025-01-01','2025-01-02'),
                       ('Known rejected','Rejected abstract','known',2,'preserved-student',1,4,'2025-01-01','2025-01-02'),
                       ('Ambiguous approved','Current abstract','ambiguous',2,'preserved-student',1,3,'2025-01-01','2025-01-02');
                INSERT INTO ProjectFiles (ProjectSubmissionId,OriginalFileName,StoredFileName,ContentType,FileExtension,FileSize,ResourceType,UploadedAt,UploadedByUserId)
                VALUES (2,'Known.pdf','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.pdf','application/pdf','.pdf',10,0,'2025-01-01','preserved-student');
                INSERT INTO SubmissionReviews (ProjectSubmissionId,ReviewerId,ReviewRound,Decision,Comments,StartedAt,CompletedAt)
                VALUES (3,'preserved-student',1,2,'Preserved rejection','2025-01-03','2025-01-04'),
                       (4,'preserved-student',1,2,'Unknown old state','2025-01-03','2025-01-04'),
                       (4,'preserved-student',2,1,'Unknown new state','2025-01-05','2025-01-06');
                """);
            var reviewStamp = await db.Database.SqlQueryRaw<byte[]>("SELECT RowVersion AS Value FROM SubmissionReviews WHERE ProjectSubmissionId=4 AND ReviewRound=1").SingleAsync();
            await db.Database.MigrateAsync();
            await DepartmentSeeder.SeedAsync(db);
            await DepartmentSeeder.SeedAsync(db);
            var student = await db.Users.SingleAsync();
            var submission = await db.ProjectSubmissions.SingleAsync(s => s.Id == 1);
            var code = await db.AuthenticationCodes.SingleAsync();
            Check(student.Id == "preserved-student" && student.StudentNumber == "BUS-20F-001" && student.DepartmentId == 1 && student.FullName == "Preserved Student", "Upgrade backfills StudentNumber while preserving user identity and relationship");
            Check(submission.Title == "Preserved draft" && submission.Abstract == "Prior abstract" && submission.StudentId == student.Id
                && submission.DepartmentId == 1 && submission.RowVersion.SequenceEqual(before), "Upgrade preserves existing submission fields and rowversion");
            Check(code.Email == "BUS20F001@SMIU.EDU.PK" && code.ConsumedAt.HasValue && code.UserId == student.Id, "Upgrade backfills OTP email and invalidates pre-upgrade code");
            Check(await db.Departments.CountAsync() == 8 && await db.Departments.AnyAsync(d => d.Id == 1 && d.Code == "BUS01" && d.StudentEmailKeyword == "BUS"), "Mapping seeds reuse pre-existing department ID/code without duplication");
            Check(await db.UserRoles.CountAsync() == 1 && await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.tables WHERE name IN ('AspNetUsers','AspNetRoles','AspNetUserRoles','AspNetUserClaims','AspNetRoleClaims','AspNetUserLogins','AspNetUserTokens')").SingleAsync() == 7, "Upgrade preserves all Identity tables and role assignment");
            Check(!(await db.Database.GetPendingMigrationsAsync()).Any(), "All upgrade migrations applied");
            Check(await db.ProjectFiles.CountAsync() == 2 && await db.ProjectFiles.AnyAsync(f => f.OriginalFileName == "Preserved.pdf" && f.FileSize == 10 && f.ProjectSubmissionId == 1 && f.IsActive), "Version migration preserves existing ProjectFile metadata and initializes active state");
            Check(submission.Status == SubmissionStatus.UnderReview && !await db.SubmissionReviews.AnyAsync(r => r.ProjectSubmissionId == 1), "Legacy UnderReview status preserved without invented history");
            Check(await db.SubmissionVersions.CountAsync() == 2 && await db.SubmissionVersions.AnyAsync(v => v.ProjectSubmissionId == 2 && v.VersionNumber == 1 && v.AbstractSnapshot == "Known abstract")
                && await db.SubmissionVersionFiles.CountAsync() == 1, "Safe backfill captures only known single submitted metadata/resource states");
            Check(await db.SubmissionReviews.AnyAsync(r => r.ProjectSubmissionId == 3 && r.SubmissionVersionId != null && r.Comments == "Preserved rejection" && r.Decision == ReviewDecision.Rejected), "Known Round 1 review links to backfilled Version 1 without changing its decision");
            Check(!await db.SubmissionVersions.AnyAsync(v => v.ProjectSubmissionId == 1 || v.ProjectSubmissionId == 4)
                && await db.SubmissionReviews.Where(r => r.ProjectSubmissionId == 4).AllAsync(r => r.SubmissionVersionId == null)
                && (await db.SubmissionReviews.AsNoTracking().SingleAsync(r => r.ProjectSubmissionId == 4 && r.ReviewRound == 1)).RowVersion.SequenceEqual(reviewStamp), "Ambiguous legacy history remains unlinked and unchanged instead of fabricated");
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }
}
