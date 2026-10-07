using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AcademicRepository.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

internal static partial class IntegrationChecks
{
    // Read-only audit of the configured database; deliberately prints no credentials or password hashes.
    static async Task AuditDatabaseAsync()
    {
        var config = new ConfigurationBuilder().SetBasePath(Path.GetFullPath("AcademicRepository"))
            .AddJsonFile("appsettings.json").AddJsonFile("appsettings.Development.json", true)
            .AddUserSecrets<Program>(true).AddEnvironmentVariables().Build();
        await using var connection = new SqlConnection(config.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync();
        async Task<string> Json(string sql)
        {
            using var cmd = new SqlCommand(sql + " FOR JSON PATH, INCLUDE_NULL_VALUES", connection);
            using var reader = await cmd.ExecuteReaderAsync();
            var result = new StringBuilder();
            while (await reader.ReadAsync()) result.Append(reader.GetString(0));
            return result.Length == 0 ? "[]" : result.ToString();
        }
        foreach (var (name, sql) in new Dictionary<string, string>
        {
            ["Users"] = "SELECT Id,UserName,Email,NormalizedEmail,FullName,DepartmentId,IsActive,EmailConfirmed,PasswordHash,SecurityStamp,ConcurrencyStamp FROM AspNetUsers ORDER BY Id",
            ["Departments"] = "SELECT Id,Name,Code,IsActive,CreatedAt,UpdatedAt FROM Departments ORDER BY Id",
            ["Submissions"] = "SELECT * FROM ProjectSubmissions ORDER BY Id",
            ["RoleAssignments"] = "SELECT * FROM AspNetUserRoles ORDER BY UserId,RoleId"
        })
        {
            var json = await Json(sql);
            using var document = JsonDocument.Parse(json);
            Console.WriteLine($"{name}: count={document.RootElement.GetArrayLength()}, SHA256={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))}");
        }
        Console.WriteLine("Migrations: " + await Json("SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId"));
        using var filesTable = new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE name='ProjectFiles'", connection);
        var hasFiles = Convert.ToInt32(await filesTable.ExecuteScalarAsync()) == 1;
        Console.WriteLine("Submitted projects without resources: " + await Json("SELECT s.Id,s.Title,s.StudentId FROM ProjectSubmissions s WHERE s.Status=1" +
            (hasFiles ? " AND NOT EXISTS (SELECT 1 FROM ProjectFiles f WHERE f.ProjectSubmissionId=s.Id)" : "")));
        if (hasFiles)
        {
            var fileJson = await Json("SELECT Id,ProjectSubmissionId,OriginalFileName,StoredFileName,ContentType,FileExtension,FileSize,ResourceType,Description,UploadedAt,UploadedByUserId FROM ProjectFiles ORDER BY Id");
            using var fileDocument = JsonDocument.Parse(fileJson);
            Console.WriteLine($"ProjectFiles: count={fileDocument.RootElement.GetArrayLength()}, SHA256={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileJson)))}");
            var resourceRoot = Path.GetFullPath(config["FileStorage:RootPath"] ?? "App_Data/ProjectFiles", Path.GetFullPath("AcademicRepository"));
            var resourceHashes = new List<object>();
            foreach (var file in fileDocument.RootElement.EnumerateArray())
            {
                var id = file.GetProperty("Id").GetInt32();
                var key = file.GetProperty("StoredFileName").GetString()!;
                if (Path.GetFileName(key) != key) { resourceHashes.Add(new { Id = id, State = "invalid key" }); continue; }
                try
                {
                    await using var stream = File.OpenRead(Path.Combine(resourceRoot, key));
                    resourceHashes.Add(new { Id = id, SHA256 = Convert.ToHexString(await SHA256.HashDataAsync(stream)) });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { resourceHashes.Add(new { Id = id, State = ex.GetType().Name }); }
            }
            Console.WriteLine("Physical resource hashes: " + JsonSerializer.Serialize(resourceHashes));
        }
        using var reviewsTable = new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE name='SubmissionReviews'", connection);
        var hasReviews = Convert.ToInt32(await reviewsTable.ExecuteScalarAsync()) == 1;
        if (hasReviews)
        {
            var reviewJson = await Json("SELECT Id,ProjectSubmissionId,ReviewerId,ReviewRound,Decision,Comments,StartedAt,CompletedAt FROM SubmissionReviews ORDER BY Id");
            using var reviews = JsonDocument.Parse(reviewJson);
            Console.WriteLine($"ReviewDecisions: count={reviews.RootElement.GetArrayLength()}, SHA256={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reviewJson)))}");
        }
        using var versionsTable = new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE name='SubmissionVersions'", connection);
        var hasVersions = Convert.ToInt32(await versionsTable.ExecuteScalarAsync()) == 1;
        Console.WriteLine("Submissions without version snapshots: " + await Json("SELECT s.Id,s.Title,s.Status FROM ProjectSubmissions s WHERE s.Status<>0" +
            (hasVersions ? " AND NOT EXISTS (SELECT 1 FROM SubmissionVersions v WHERE v.ProjectSubmissionId=s.Id)" : "")));
        if (hasVersions)
        {
            Console.WriteLine("Submission versions: " + await Json("SELECT Id,ProjectSubmissionId,VersionNumber,SubmittedAt FROM SubmissionVersions ORDER BY ProjectSubmissionId,VersionNumber"));
            Console.WriteLine("Approved without version: " + await Json("SELECT s.Id FROM ProjectSubmissions s WHERE s.Status=3 AND NOT EXISTS (SELECT 1 FROM SubmissionVersions v WHERE v.ProjectSubmissionId=s.Id)"));
            Console.WriteLine("Rejected without version: " + await Json("SELECT s.Id FROM ProjectSubmissions s WHERE s.Status=4 AND NOT EXISTS (SELECT 1 FROM SubmissionVersions v WHERE v.ProjectSubmissionId=s.Id)"));
            Console.WriteLine("Unlinked reviews: " + await Json("SELECT Id,ProjectSubmissionId,ReviewRound,Decision FROM SubmissionReviews WHERE SubmissionVersionId IS NULL"));
        }
        Console.WriteLine("Approved/Rejected without review history: " + await Json("SELECT s.Id,s.Title,s.Status FROM ProjectSubmissions s WHERE s.Status IN (3,4)" +
            (hasReviews ? " AND NOT EXISTS (SELECT 1 FROM SubmissionReviews r WHERE r.ProjectSubmissionId=s.Id)" : "")));
        Console.WriteLine("UnderReview without active review: " + await Json("SELECT s.Id,s.Title FROM ProjectSubmissions s WHERE s.Status=2" +
            (hasReviews ? " AND NOT EXISTS (SELECT 1 FROM SubmissionReviews r WHERE r.ProjectSubmissionId=s.Id AND r.Decision=0)" : "")));
        Console.WriteLine("Duplicate normalized emails: " + await Json("SELECT NormalizedEmail,COUNT(*) AS Count FROM AspNetUsers WHERE NormalizedEmail IS NOT NULL GROUP BY NormalizedEmail HAVING COUNT(*)>1"));
        Console.WriteLine("Submission department mismatches: " + await Json("SELECT s.Id,s.StudentId,s.DepartmentId AS SubmissionDepartment,u.DepartmentId AS UserDepartment FROM ProjectSubmissions s JOIN AspNetUsers u ON u.Id=s.StudentId WHERE u.DepartmentId IS NULL OR s.DepartmentId<>u.DepartmentId"));
        Console.WriteLine("Submission owners and states: " + await Json("SELECT s.Id,s.Title,s.Status,u.Email AS StudentEmail FROM ProjectSubmissions s JOIN AspNetUsers u ON u.Id=s.StudentId ORDER BY s.Id"));
        using var column = new SqlCommand("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('AspNetUsers') AND name='StudentNumber'", connection);
        var hasNumber = Convert.ToInt32(await column.ExecuteScalarAsync()) == 1;
        var students = await Json("SELECT u.Id,u.Email,u.DepartmentId," + (hasNumber ? "u.StudentNumber" : "NULL AS StudentNumber") + " FROM AspNetUsers u JOIN AspNetUserRoles ur ON ur.UserId=u.Id JOIN AspNetRoles r ON r.Id=ur.RoleId WHERE r.Name='Student'");
        Console.WriteLine("Student profiles: " + students);
        using var mappingColumn = new SqlCommand("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('Departments') AND name='StudentEmailKeyword'", connection);
        var hasMapping = Convert.ToInt32(await mappingColumn.ExecuteScalarAsync()) == 1;
        Console.WriteLine("Department mappings: " + await Json("SELECT Id,Name,Code," + (hasMapping ? "StudentEmailKeyword" : "NULL AS StudentEmailKeyword") + " FROM Departments ORDER BY Id"));
        var emails = await Json("SELECT Id,Email FROM AspNetUsers ORDER BY Id");
        using var emailDocument = JsonDocument.Parse(emails);
        Console.WriteLine("Non-institutional user IDs: " + JsonSerializer.Serialize(emailDocument.RootElement.EnumerateArray()
            .Where(e => !InstitutionalEmail.IsValid(e.GetProperty("Email").GetString())).Select(e => e.GetProperty("Id").GetString()).ToArray()));
        Console.WriteLine("Graph configured: " + new EmailOptions { DeliveryMode = config["Email:DeliveryMode"] ?? "", From = config["Email:From"] ?? "",
            TenantId = config["Email:TenantId"] ?? "", ClientId = config["Email:ClientId"] ?? "", ClientSecret = config["Email:ClientSecret"] ?? "" }.HasValidGraphConfiguration());
    }
}
