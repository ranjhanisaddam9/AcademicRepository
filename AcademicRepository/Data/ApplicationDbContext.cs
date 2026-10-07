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

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<ProjectFile>(entity =>
        {
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
