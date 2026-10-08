using AcademicRepository.Data;
using AcademicRepository.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

internal static class ConcurrencyChecks
{
    public static void Run()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=(local);Database=MetadataOnly;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var db = new ApplicationDbContext(options);
        foreach (var type in new[] { typeof(ProjectSubmission), typeof(SubmissionReview), typeof(ApplicationUser), typeof(Department), typeof(SystemSetting), typeof(AuthenticationCode) })
        {
            var entity = db.Model.FindEntityType(type)!;
            var property = entity.FindProperty("RowVersion");
            Check(property is not null && property.IsConcurrencyToken && property.ValueGenerated == ValueGenerated.OnAddOrUpdate
                && property.GetColumnType()?.Equals("rowversion", StringComparison.OrdinalIgnoreCase) == true,
                $"{type.Name} has SQL Server rowversion concurrency metadata");
        }

        var review = db.Model.FindEntityType(typeof(SubmissionReview))!;
        Check(review.GetIndexes().Any(index => index.IsUnique && index.GetFilter() == "[Decision] = 0"),
            "Database permits only one active review per submission");
        var versions = db.Model.FindEntityType(typeof(SubmissionVersion))!;
        Check(versions.GetIndexes().Any(index => index.IsUnique && index.Properties.Select(p => p.Name).SequenceEqual(["ProjectSubmissionId", "VersionNumber"])),
            "Database prevents duplicate version numbers per submission");

        var root = Directory.GetCurrentDirectory();
        var reviewSource = File.ReadAllText(Path.Combine(root, "AcademicRepository", "Services", "ReviewService.cs"));
        Check(reviewSource.Contains("submission.Status != SubmissionStatus.UnderReview")
            && reviewSource.Contains("model.ReviewId != review.Id")
            && reviewSource.Contains("This record was changed by another user"), "Stale approval or rejection is rejected with a safe conflict message");
        var workflowSource = File.ReadAllText(Path.Combine(root, "AcademicRepository", "Services", "SubmissionWorkflowService.cs"));
        Check(workflowSource.Contains("locks.OwnedAsync(id, userId)") && workflowSource.Contains("db.SubmissionVersions.Add(snapshot)"),
            "Revision and resubmission transition under the parent submission lock");
        var userSource = File.ReadAllText(Path.Combine(root, "AcademicRepository", "Controllers", "UsersController.cs"));
        Check(userSource.Contains("user.ConcurrencyStamp") && userSource.Contains("user.RowVersion") && userSource.Contains("ConcurrencyFailure"),
            "Stale staff edit is checked against Identity concurrency stamp and rowversion");
        var departmentSource = File.ReadAllText(Path.Combine(root, "AcademicRepository", "Controllers", "DepartmentsController.cs"));
        Check(departmentSource.Contains("OriginalValue = model.RowVersion") && departmentSource.Contains("DbUpdateConcurrencyException"),
            "Department edit compares submitted rowversion and handles stale writes");
        var settingSource = File.ReadAllText(Path.Combine(root, "AcademicRepository", "Services", "OperationalSettingsService.cs"));
        Check(settingSource.Contains("ConcurrencyToken") && settingSource.Contains("Property(s => s.RowVersion).OriginalValue")
            && settingSource.Contains("StaleDataConflictException"), "Settings update checks submitted concurrency token");
        var migration = Path.Combine(root, "AcademicRepository", "Migrations");
        Check(Directory.GetFiles(migration, "*AddOptimisticConcurrency*.cs").Length > 0,
            "Additive concurrency migration exists");
    }

    private static void Check(bool condition, string label)
    {
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {label}");
        if (!condition) throw new InvalidOperationException(label);
    }
}
