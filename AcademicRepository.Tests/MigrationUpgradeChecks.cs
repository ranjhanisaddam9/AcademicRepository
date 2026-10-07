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
            var before = await db.Database.SqlQueryRaw<byte[]>("SELECT RowVersion AS Value FROM ProjectSubmissions WHERE Id=1").SingleAsync();
            await db.Database.MigrateAsync();
            await DepartmentSeeder.SeedAsync(db);
            await DepartmentSeeder.SeedAsync(db);
            var student = await db.Users.SingleAsync();
            var submission = await db.ProjectSubmissions.SingleAsync();
            var code = await db.AuthenticationCodes.SingleAsync();
            Check(student.Id == "preserved-student" && student.StudentNumber == "BUS-20F-001" && student.DepartmentId == 1 && student.FullName == "Preserved Student", "Upgrade backfills StudentNumber while preserving user identity and relationship");
            Check(submission.Title == "Preserved draft" && submission.Abstract == "Prior abstract" && submission.StudentId == student.Id
                && submission.DepartmentId == 1 && submission.RowVersion.SequenceEqual(before), "Upgrade preserves existing submission fields and rowversion");
            Check(code.Email == "BUS20F001@SMIU.EDU.PK" && code.ConsumedAt.HasValue && code.UserId == student.Id, "Upgrade backfills OTP email and invalidates pre-upgrade code");
            Check(await db.Departments.CountAsync() == 8 && await db.Departments.AnyAsync(d => d.Id == 1 && d.Code == "BUS01" && d.StudentEmailKeyword == "BUS"), "Mapping seeds reuse pre-existing department ID/code without duplication");
            Check(await db.UserRoles.CountAsync() == 1 && await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sys.tables WHERE name IN ('AspNetUsers','AspNetRoles','AspNetUserRoles','AspNetUserClaims','AspNetRoleClaims','AspNetUserLogins','AspNetUserTokens')").SingleAsync() == 7, "Upgrade preserves all Identity tables and role assignment");
            Check(!(await db.Database.GetPendingMigrationsAsync()).Any(), "All upgrade migrations applied");
            Check(!await db.ProjectFiles.AnyAsync(), "ProjectFiles migration invents no resources for existing submissions");
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }
}
