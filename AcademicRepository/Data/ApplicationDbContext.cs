using AcademicRepository.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AcademicRepository.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<ProjectSubmission> ProjectSubmissions => Set<ProjectSubmission>();
    public DbSet<AuthenticationCode> AuthenticationCodes => Set<AuthenticationCode>();
    public DbSet<ProjectFile> ProjectFiles => Set<ProjectFile>();
    public DbSet<SubmissionReview> SubmissionReviews => Set<SubmissionReview>();
    public DbSet<SubmissionVersion> SubmissionVersions => Set<SubmissionVersion>();
    public DbSet<SubmissionVersionFile> SubmissionVersionFiles => Set<SubmissionVersionFile>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<SystemSetting>(entity =>
        {
            entity.HasIndex(s => s.Key).IsUnique();
            entity.Property(s => s.RowVersion).IsRowVersion();
            entity.Property(s => s.Key).UseCollation("Latin1_General_100_CI_AS");
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<SubmissionVersion>(entity =>
        {
            entity.HasIndex(v => new { v.ProjectSubmissionId, v.VersionNumber }).IsUnique();
            entity.ToTable(t => t.HasCheckConstraint("CK_SubmissionVersions_Number", "[VersionNumber] >= 1"));
            entity.Property(v => v.CreatedByUserId).HasMaxLength(450);
            entity.Property(v => v.TitleSnapshot).HasMaxLength(250);
            entity.Property(v => v.AbstractSnapshot).HasMaxLength(10000);
            entity.Property(v => v.KeywordsSnapshot).HasMaxLength(1000);
            entity.Property(v => v.SupervisorNameSnapshot).HasMaxLength(150);
            entity.Property(v => v.CourseNameSnapshot).HasMaxLength(150);
            entity.Property(v => v.CourseCodeSnapshot).HasMaxLength(30);
            entity.Property(v => v.AcademicYearSnapshot).HasMaxLength(30);
            entity.Property(v => v.SemesterSnapshot).HasMaxLength(50);
            entity.HasOne(v => v.ProjectSubmission).WithMany().HasForeignKey(v => v.ProjectSubmissionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(v => v.Department).WithMany().HasForeignKey(v => v.DepartmentIdSnapshot).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(v => v.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<SubmissionVersionFile>(entity =>
        {
            entity.HasKey(f => new { f.SubmissionVersionId, f.ProjectFileId });
            entity.HasOne(f => f.SubmissionVersion).WithMany(v => v.Files).HasForeignKey(f => f.SubmissionVersionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(f => f.ProjectFile).WithMany().HasForeignKey(f => f.ProjectFileId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<SubmissionReview>(entity =>
        {
            entity.HasOne(r => r.SubmissionVersion).WithMany().HasForeignKey(r => r.SubmissionVersionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(r => r.SubmissionVersionId).IsUnique().HasFilter("[SubmissionVersionId] IS NOT NULL");
            entity.Property(r => r.ReviewerId).HasMaxLength(450).IsRequired();
            entity.Property(r => r.Comments).HasMaxLength(2000);
            entity.Property(r => r.RowVersion).IsRowVersion();
            entity.HasOne(r => r.ProjectSubmission).WithMany(s => s.Reviews).HasForeignKey(r => r.ProjectSubmissionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(r => r.Reviewer).WithMany().HasForeignKey(r => r.ReviewerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(r => new { r.ProjectSubmissionId, r.ReviewRound }).IsUnique();
            entity.HasIndex(r => r.ProjectSubmissionId).HasDatabaseName("IX_SubmissionReviews_OnePending").IsUnique().HasFilter("[Decision] = 0");
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_SubmissionReviews_Round", "[ReviewRound] >= 1");
                t.HasCheckConstraint("CK_SubmissionReviews_Decision", "([Decision] = 0 AND [CompletedAt] IS NULL) OR ([Decision] IN (1,2) AND [CompletedAt] IS NOT NULL)");
                t.HasCheckConstraint("CK_SubmissionReviews_RejectionComments", "[Decision] <> 2 OR ([Comments] IS NOT NULL AND LEN(LTRIM(RTRIM([Comments]))) > 0)");
            });
        });
        builder.Entity<ProjectFile>(entity =>
        {
            entity.Property(f => f.IsActive).HasDefaultValue(true);
            entity.Property(f => f.OriginalFileName).HasMaxLength(255).IsRequired();
            entity.Property(f => f.StoredFileName).HasMaxLength(64).IsRequired();
            entity.HasIndex(f => f.StoredFileName).IsUnique();
            entity.Property(f => f.ContentType).HasMaxLength(150).IsRequired();
            entity.Property(f => f.FileExtension).HasMaxLength(10).IsRequired();
            entity.Property(f => f.Description).HasMaxLength(1000);
            entity.Property(f => f.UploadedByUserId).HasMaxLength(450).IsRequired();
            entity.HasOne(f => f.ProjectSubmission).WithMany(s => s.ProjectFiles)
                .HasForeignKey(f => f.ProjectSubmissionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ApplicationUser>().WithMany().HasForeignKey(f => f.UploadedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<AuthenticationCode>(entity =>
        {
            entity.Property(c => c.CodeHash).HasMaxLength(64).IsRequired();
            entity.Property(c => c.SecurityStamp).HasMaxLength(256).IsRequired();
            entity.Property(c => c.RecoveryGrantHash).HasMaxLength(64);
            entity.Property(c => c.RowVersion).IsRowVersion();
            entity.Property(c => c.Email).HasMaxLength(256).IsRequired().UseCollation("Latin1_General_100_CI_AS");
            entity.HasIndex(c => new { c.Email, c.Purpose }).IsUnique();
            entity.HasIndex(c => c.Nonce).IsUnique();
            entity.HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<Department>(entity =>
        {
            entity.Property(d => d.RowVersion).IsRowVersion();
            entity.Property(d => d.Name).IsRequired().HasMaxLength(120).UseCollation("Latin1_General_100_CI_AS");
            entity.Property(d => d.Code).IsRequired().HasMaxLength(20).UseCollation("Latin1_General_100_CI_AS");
            entity.HasIndex(d => d.Name).IsUnique();
            entity.HasIndex(d => d.Code).IsUnique();
            entity.Property(d => d.StudentEmailKeyword).HasMaxLength(3).UseCollation("Latin1_General_100_CI_AS");
            entity.HasIndex(d => d.StudentEmailKeyword).IsUnique().HasFilter("[StudentEmailKeyword] IS NOT NULL");
            entity.Property(d => d.IsActive).HasDefaultValue(true);
        });
        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(u => u.RowVersion).IsRowVersion();
            entity.Property(u => u.FullName).IsRequired().HasMaxLength(150);
            entity.Property(u => u.StudentNumber).HasMaxLength(11).UseCollation("Latin1_General_100_CI_AS");
            entity.HasIndex(u => u.StudentNumber).IsUnique().HasFilter("[StudentNumber] IS NOT NULL");
            entity.HasIndex(u => u.NormalizedEmail).HasDatabaseName("EmailIndex").IsUnique().HasFilter("[NormalizedEmail] IS NOT NULL");
            entity.Property(u => u.IsActive).HasDefaultValue(true);
            entity.HasOne(u => u.Department).WithMany(d => d.Users)
                .HasForeignKey(u => u.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<ProjectSubmission>(entity =>
        {
            entity.Property(s => s.Title).IsRequired().HasMaxLength(250);
            entity.Property(s => s.Abstract).IsRequired().HasMaxLength(10000);
            entity.Property(s => s.Keywords).IsRequired().HasMaxLength(1000);
            entity.Property(s => s.SupervisorName).HasMaxLength(150);
            entity.Property(s => s.CourseName).HasMaxLength(150);
            entity.Property(s => s.CourseCode).HasMaxLength(30);
            entity.Property(s => s.AcademicYear).HasMaxLength(30);
            entity.Property(s => s.Semester).HasMaxLength(50);
            entity.Property(s => s.RowVersion).IsRowVersion();
            entity.HasOne(s => s.Student).WithMany().HasForeignKey(s => s.StudentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(s => s.Department).WithMany().HasForeignKey(s => s.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(s => new { s.StudentId, s.CreatedAt });
        });
    }
}
