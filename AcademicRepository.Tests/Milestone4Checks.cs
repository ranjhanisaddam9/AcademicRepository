using System.IO.Compression;
using System.Net;
using System.Text;
using AcademicRepository.Data;
using AcademicRepository.Models;
using AcademicRepository.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

internal static partial class IntegrationChecks
{
    static readonly byte[] TestPdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\n%%EOF");
    static byte[] TestArchive(string? entry = null)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry(entry ?? "source/readme.txt").Open())) writer.Write("test resource");
            if (entry is not null) { using var writer = new StreamWriter(archive.CreateEntry("[Content_Types].xml").Open()); writer.Write("<Types />"); }
        }
        return output.ToArray();
    }
    static async Task<HttpResponseMessage> UploadTestFileAsync(HttpClient client, int id, string name = "Report.pdf", byte[]? bytes = null,
        string resourceType = "0", Dictionary<string, string>? extra = null, bool csrf = true)
    {
        var page = await client.GetStringAsync("/Student/Submissions");
        using var form = new MultipartFormDataContent();
        if (csrf) form.Add(new StringContent(HiddenValue(page, "__RequestVerificationToken")), "__RequestVerificationToken");
        form.Add(new StringContent(resourceType), "ResourceType");
        form.Add(new StringContent("Academic resource"), "Description");
        if (extra is not null) foreach (var (key, value) in extra) form.Add(new StringContent(value), key);
        var file = new ByteArrayContent(bytes ?? TestPdf);
        file.Headers.ContentType = new("application/octet-stream"); // Browser MIME is deliberately not trusted.
        form.Add(file, "File", name);
        return await client.PostAsync($"/ProjectFiles/Upload/{id}", form);
    }
    static async Task Milestone4Checks(WebApplicationFactory<Program> factory, RecordingAuthenticationEmailSender sender, string storageRoot, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        using var student = Client(factory);
        using var other = Client(factory);
        await LoginStudentAsync(student, "CSC20F005@smiu.edu.pk", sender);
        await LoginStudentAsync(other, "CSC20F006@smiu.edu.pk", sender);
        var owner = await db.Users.AsNoTracking().SingleAsync(u => u.Email == "CSC20F005@smiu.edu.pk");
        Check(owner.StudentNumber == "CSC-20F-005" && await db.Departments.AnyAsync(d => d.Id == owner.DepartmentId && d.StudentEmailKeyword == "CSC"), "Milestone 4 retains Student identity and department parsing");
        async Task<int> Draft(string title)
        {
            Check((await PostFormAsync(student, "/Student/Submissions/Create", SubmissionFields(title, "SaveDraft"))).StatusCode == HttpStatusCode.Redirect, "Create resource draft " + title);
            return await db.ProjectSubmissions.Where(s => s.StudentId == owner.Id && s.Title == title).Select(s => s.Id).SingleAsync();
        }
        async Task<HttpResponseMessage> Delete(int fileId, HttpClient? client = null)
        {
            client ??= student;
            var page = await client.GetStringAsync("/Student/Submissions");
            return await client.PostAsync($"/ProjectFiles/Delete/{fileId}", Form(page, new()));
        }
        async Task<HttpResponseMessage> Submit(int id)
        {
            var page = await student.GetStringAsync($"/Student/Submissions/{id}/Edit");
            var values = SubmissionFields("AI Based Academic Repository", "Submit");
            values["ProjectType"] = "2"; values["Abstract"] = "Complete abstract"; values["Keywords"] = "academic, repository";
            values["EditToken"] = HiddenValue(page, "EditToken");
            return await student.PostAsync($"/Student/Submissions/{id}/Edit", Form(page, values));
        }
        var id = await Draft("AI Based Academic Repository");
        var rejected = await Submit(id);
        Check(rejected.StatusCode == HttpStatusCode.OK && (await rejected.Content.ReadAsStringAsync()).Contains("Please upload at least one resource"), "Empty draft cannot be submitted");
        var forged = new Dictionary<string, string> { ["DepartmentId"] = "1", ["StudentId"] = "forged", ["StoredFileName"] = "../../attack.pdf",
            ["UploadedByUserId"] = "forged", ["UploadedAt"] = "2000-01-01", ["FileSize"] = "1", ["ContentType"] = "text/html", ["Id"] = "99999" };
        Check((await UploadTestFileAsync(student, id, "FinalReport.pdf", extra: forged)).StatusCode == HttpStatusCode.Redirect, "Valid PDF upload ignores sensitive overposted fields");
        Check((await UploadTestFileAsync(student, id, "SourceCode.zip", TestArchive(), "2")).StatusCode == HttpStatusCode.Redirect, "Valid ZIP upload");
        Check((await UploadTestFileAsync(student, id, "Presentation.pptx", TestArchive("ppt/presentation.xml"), "3")).StatusCode == HttpStatusCode.Redirect, "Valid PowerPoint resource upload");
        var files = await db.ProjectFiles.AsNoTracking().Where(f => f.ProjectSubmissionId == id).ToListAsync();
        Check(files.Count == 3 && files.All(f => f.UploadedByUserId == owner.Id && f.UploadedAt > DateTime.UtcNow.AddMinutes(-5))
            && await db.ProjectSubmissions.AnyAsync(s => s.Id == id && s.DepartmentId == owner.DepartmentId && s.StudentId == owner.Id), "Three resource rows preserve owner and department");
        var report = files.Single(f => f.OriginalFileName == "FinalReport.pdf");
        Check(report.FileSize == TestPdf.Length && report.ContentType == "application/pdf" && report.Id != 99999 && report.StoredFileName != report.OriginalFileName, "Metadata and random storage key are server-derived");
        var details = await student.GetStringAsync($"/Student/Submissions/{id}");
        Check(details.Contains("Resource Materials") && details.Contains("FinalReport.pdf") && details.Contains("Academic resource") && details.Contains(" KB")
            && files.All(f => !details.Contains(f.StoredFileName)) && !details.Contains(storageRoot), "Resource UI exposes safe metadata and human-readable size only");
        Check((await UploadTestFileAsync(student, id)).StatusCode == HttpStatusCode.BadRequest, "Maximum file count enforced");
        var download = await student.GetAsync($"/ProjectFiles/Download/{report.Id}");
        Check(download.StatusCode == HttpStatusCode.OK && (await download.Content.ReadAsByteArrayAsync()).SequenceEqual(TestPdf)
            && download.Content.Headers.ContentDisposition!.ToString().Contains("FinalReport.pdf") && download.Headers.Contains("X-Content-Type-Options"), "Own Draft download returns bytes as attachment with original name");
        Check((await student.GetAsync("/App_Data/ProjectFiles/" + report.StoredFileName)).StatusCode == HttpStatusCode.NotFound, "Private resource has no public static URL");
        Check((await other.GetAsync($"/ProjectFiles/Download/{report.Id}")).StatusCode == HttpStatusCode.NotFound, "Same-department Student cannot download another owner's file");
        Check((await Delete(report.Id, other)).StatusCode == HttpStatusCode.NotFound, "Other Student cannot delete resource");
        Check((await UploadTestFileAsync(other, id)).StatusCode == HttpStatusCode.NotFound, "Other Student cannot upload to project");
        Check((await student.GetAsync("/ProjectFiles/Download/999999")).StatusCode == HttpStatusCode.NotFound, "Unknown resource returns safe 404");
        Check((await student.GetAsync($"/ProjectFiles/Delete/{report.Id}")).StatusCode == HttpStatusCode.MethodNotAllowed, "Resource deletion is POST-only");
        Check((await student.PostAsync($"/ProjectFiles/Delete/{report.Id}", new FormUrlEncodedContent(new Dictionary<string,string>()))).StatusCode == HttpStatusCode.BadRequest, "Delete antiforgery enforced");
        Check((await UploadTestFileAsync(student, id, csrf: false)).StatusCode == HttpStatusCode.BadRequest, "Upload antiforgery enforced");
        var presentation = files.Single(f => f.FileExtension == ".pptx");
        Check((await Delete(presentation.Id)).StatusCode == HttpStatusCode.Redirect && !await db.ProjectFiles.AnyAsync(f => f.Id == presentation.Id)
            && !File.Exists(Path.Combine(storageRoot, presentation.StoredFileName)), "Draft resource deletion removes DB row and physical file");
        Check((await UploadTestFileAsync(student, id, "Presentation.pptx", TestArchive("ppt/presentation.xml"), "3")).StatusCode == HttpStatusCode.Redirect, "Corrected presentation can be reuploaded");
        Check((await Submit(id)).StatusCode == HttpStatusCode.Redirect && await db.ProjectSubmissions.AnyAsync(s => s.Id == id && s.Status == SubmissionStatus.Submitted), "Resource package submits successfully");
        Check((await UploadTestFileAsync(student, id)).StatusCode == HttpStatusCode.Conflict, "Submitted package rejects upload");
        Check((await Delete(report.Id)).StatusCode == HttpStatusCode.Conflict, "Submitted package rejects resource deletion");
        Check((await student.GetAsync($"/ProjectFiles/Download/{report.Id}")).StatusCode == HttpStatusCode.OK, "Submitted resource remains downloadable by owner");
        Check((await student.GetAsync($"/Student/Submissions/{id}/Edit")).StatusCode == HttpStatusCode.Conflict, "Submitted metadata remains frozen");
        var submittedPage = await student.GetStringAsync($"/Student/Submissions/{id}");
        Check(!submittedPage.Contains("Upload Resource") && !submittedPage.Contains($"/ProjectFiles/Delete/{report.Id}"), "Submitted resource UI read-only");

        var validationId = await Draft("Resource validation");
        foreach (var extension in new[] { ".exe", ".dll", ".bat", ".cmd", ".ps1", ".sh", ".com", ".scr", ".msi", ".aspx", ".cshtml", ".php" })
            Check((await UploadTestFileAsync(student, validationId, "dangerous" + extension)).StatusCode == HttpStatusCode.BadRequest, "Executable/script extension blocked " + extension);
        foreach (var (name, bytes, type) in new[]
        {
            ("empty.pdf", Array.Empty<byte>(), "0"), ("bad.exe", TestPdf, "0"), ("bad.ps1", TestPdf, "0"),
            ("bad.cshtml", TestPdf, "0"), ("bad.html", TestPdf, "0"), ("renamed.pdf", Encoding.ASCII.GetBytes("MZ executable"), "0"),
            ("oversize.pdf", new byte[1024 * 1024 + 1], "0"), ("type.pdf", TestPdf, "999"),
            ("wrong.docx", TestArchive(), "0"), ("bad.zip", TestPdf, "0"), ("binary.txt", new byte[] {0,1,2}, "0")
        }) Check((await UploadTestFileAsync(student, validationId, name, bytes, type)).StatusCode == HttpStatusCode.BadRequest, "Reject invalid upload " + name);
        Check(!await db.ProjectFiles.AnyAsync(f => f.ProjectSubmissionId == validationId), "Invalid uploads leave no metadata");
        Check((await UploadTestFileAsync(student, validationId, "../../Escaped.pdf")).StatusCode == HttpStatusCode.Redirect, "Traversal-style original filename is sanitized");
        var sanitized = await db.ProjectFiles.AsNoTracking().SingleAsync(f => f.ProjectSubmissionId == validationId);
        Check(sanitized.OriginalFileName == "Escaped.pdf" && File.Exists(Path.Combine(storageRoot, sanitized.StoredFileName))
            && !File.Exists(Path.Combine(storageRoot, "Escaped.pdf")), "Physical filename is random and independent of untrusted path");
        var deletePage = await student.GetStringAsync($"/Student/Submissions/{validationId}/Delete");
        Check((await student.PostAsync($"/Student/Submissions/{validationId}/Delete", Form(deletePage, new() { ["EditToken"] = HiddenValue(deletePage, "EditToken") }))).StatusCode == HttpStatusCode.Conflict,
            "Draft deletion requires resource cleanup first");
        Check((await Delete(sanitized.Id)).StatusCode == HttpStatusCode.Redirect, "Sanitized resource removed");
        Check((await UploadTestFileAsync(student, validationId, "Paper.docx", TestArchive("word/document.xml"), "1")).StatusCode == HttpStatusCode.Redirect, "Valid DOCX upload");
        Check((await UploadTestFileAsync(student, validationId, "Paper.docx", TestArchive("word/document.xml"), "1")).StatusCode == HttpStatusCode.Redirect, "Duplicate original filename accepted without overwrite");
        var duplicates = await db.ProjectFiles.AsNoTracking().Where(f => f.ProjectSubmissionId == validationId).ToListAsync();
        Check(duplicates.Count == 2 && duplicates.Select(f => f.StoredFileName).Distinct().Count() == 2, "Duplicate filenames have distinct storage keys");
        File.Delete(Path.Combine(storageRoot, duplicates[0].StoredFileName));
        var missing = await student.GetAsync($"/ProjectFiles/Download/{duplicates[0].Id}");
        Check(missing.StatusCode == HttpStatusCode.NotFound && !(await missing.Content.ReadAsStringAsync()).Contains(storageRoot), "Missing physical file handled without path exposure");
        Check((await Delete(duplicates[0].Id)).StatusCode == HttpStatusCode.Redirect, "Missing physical file metadata can be removed safely");
        var concurrent = await Task.WhenAll(UploadTestFileAsync(student, validationId), UploadTestFileAsync(student, validationId), UploadTestFileAsync(student, validationId));
        Check(concurrent.Count(r => r.StatusCode == HttpStatusCode.Redirect) == 2 && concurrent.Count(r => r.StatusCode == HttpStatusCode.BadRequest) == 1
            && await db.ProjectFiles.CountAsync(f => f.ProjectSubmissionId == validationId) == 3, "Concurrent uploads cannot exceed file limit");

        var mismatchId = await Draft("Department mismatch");
        Check((await UploadTestFileAsync(student, mismatchId)).StatusCode == HttpStatusCode.Redirect, "Create department mismatch fixture resource");
        var mismatchFile = await db.ProjectFiles.AsNoTracking().SingleAsync(f => f.ProjectSubmissionId == mismatchId);
        var bus = await db.Departments.Where(d => d.StudentEmailKeyword == "BUS").Select(d => d.Id).SingleAsync();
        await db.ProjectSubmissions.Where(s => s.Id == mismatchId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DepartmentId, bus));
        Check((await UploadTestFileAsync(student, mismatchId)).StatusCode == HttpStatusCode.NotFound
            && (await student.GetAsync($"/ProjectFiles/Download/{mismatchFile.Id}")).StatusCode == HttpStatusCode.NotFound
            && (await Delete(mismatchFile.Id)).StatusCode == HttpStatusCode.NotFound, "Owner with department mismatch denied every resource operation");
        await db.ProjectSubmissions.Where(s => s.Id == mismatchId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DepartmentId, owner.DepartmentId!.Value));

        await FileFailureChecks(factory, owner.Id, mismatchId, storageRoot);
        try { await storage.OpenReadAsync("../../appsettings.json"); Check(false, "Traversal key must fail"); }
        catch (InvalidDataException) { Check(true, "Storage rejects path traversal keys"); }
        try
        {
            _ = new LocalFileStorageService(Options.Create(new FileStorageOptions { RootPath = Path.Combine(storageRoot, "wwwroot", "uploads") }),
                new EmailTestEnvironment { ContentRootPath = storageRoot, WebRootPath = Path.Combine(storageRoot, "wwwroot") });
            Check(false, "Web root storage must fail");
        }
        catch (InvalidOperationException) { Check(true, "Storage rejects a directory within web root"); }
        using var anonymous = Client(factory);
        Check((await anonymous.GetAsync($"/ProjectFiles/Download/{report.Id}")).StatusCode == HttpStatusCode.Redirect, "Anonymous resource download requires login");
        foreach (var role in ApplicationRoles.Staff)
        {
            using var staff = Client(factory);
            await LoginAsync(staff, role + "@smiu.edu.pk", password);
            var page = await staff.GetStringAsync("/");
            var get = await staff.GetAsync($"/ProjectFiles/Download/{report.Id}");
            Check(get.StatusCode == HttpStatusCode.Redirect && get.Headers.Location!.OriginalString.Contains("AccessDenied"), role + " cannot download Student resource");
            foreach (var action in new[] { "Upload", "Delete" })
            {
                var denied = await staff.PostAsync($"/ProjectFiles/{action}/{id}", Form(page, new()));
                Check(denied.StatusCode == HttpStatusCode.Redirect && denied.Headers.Location!.OriginalString.Contains("AccessDenied"), role + " cannot " + action + " Student resource");
            }
        }

        var raceId = await Draft("Upload submit race");
        Check((await UploadTestFileAsync(student, raceId)).StatusCode == HttpStatusCode.Redirect, "Prepare upload/submit race");
        var racePage = await student.GetStringAsync($"/Student/Submissions/{raceId}/Edit");
        var raceValues = SubmissionFields("Upload submit race", "Submit");
        raceValues["Abstract"] = "Complete"; raceValues["Keywords"] = "race"; raceValues["EditToken"] = HiddenValue(racePage, "EditToken");
        var race = await Task.WhenAll(UploadTestFileAsync(student, raceId), student.PostAsync($"/Student/Submissions/{raceId}/Edit", Form(racePage, raceValues)));
        Check(race.Count(r => r.StatusCode == HttpStatusCode.Redirect) == 1 && race.Count(r => r.StatusCode == HttpStatusCode.Conflict) == 1, "Upload/submit race permits one mutation; stale submission or frozen upload rejected");
        var raceSubmission = await db.ProjectSubmissions.AsNoTracking().SingleAsync(s => s.Id == raceId);
        Check(await db.ProjectFiles.CountAsync(f => f.ProjectSubmissionId == raceId) == (raceSubmission.Status == SubmissionStatus.Submitted ? 1 : 2), "Submitted package never includes a concurrent later upload");

        var deleteRaceId = await Draft("Delete submit race");
        Check((await UploadTestFileAsync(student, deleteRaceId)).StatusCode == HttpStatusCode.Redirect, "Prepare deletion/submit race");
        var deleteRaceFile = await db.ProjectFiles.AsNoTracking().SingleAsync(f => f.ProjectSubmissionId == deleteRaceId);
        racePage = await student.GetStringAsync($"/Student/Submissions/{deleteRaceId}/Edit");
        raceValues["EditToken"] = HiddenValue(racePage, "EditToken");
        race = await Task.WhenAll(Delete(deleteRaceFile.Id), student.PostAsync($"/Student/Submissions/{deleteRaceId}/Edit", Form(racePage, raceValues)));
        Check(race.Count(r => r.StatusCode == HttpStatusCode.Redirect) == 1 && race.Count(r => r.StatusCode == HttpStatusCode.Conflict) == 1, "Last-resource deletion cannot race past submission freeze");
        raceSubmission = await db.ProjectSubmissions.AsNoTracking().SingleAsync(s => s.Id == deleteRaceId);
        Check(raceSubmission.Status != SubmissionStatus.Submitted || await db.ProjectFiles.AnyAsync(f => f.ProjectSubmissionId == deleteRaceId), "Concurrent deletion cannot leave newly Submitted package empty");
    }

    static async Task FileFailureChecks(WebApplicationFactory<Program> factory, string ownerId, int id, string root)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var realStorage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        var storageSettings = scope.ServiceProvider.GetRequiredService<IOptions<FileStorageOptions>>().Value;
        var settings = new TestOperationalSettings(storageSettings.MaxFileSizeMB, storageSettings.MaxFilesPerSubmission);
        var before = await db.ProjectFiles.CountAsync();
        UploadResourceViewModel Upload() => new() { File = new FormFile(new MemoryStream(TestPdf), 0, TestPdf.Length, "File", "Failure.pdf"), ResourceType = ProjectResourceType.Report };
        var broken = new FaultingStorage(realStorage) { FailStore = true };
        var service = new ProjectFileService(db, broken, new SubmissionLock(db), settings, NullLogger<ProjectFileService>.Instance);
        try { await service.UploadAsync(id, ownerId, Upload()); Check(false, "Storage failure must fail"); }
        catch (ResourceOperationException ex) { Check(ex.Status == 503 && await db.ProjectFiles.CountAsync() == before, "Physical storage failure creates no ProjectFile row"); }
        var interceptor = new ResourceSaveFailure();
        await using (var failingDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(db.Database.GetConnectionString()).AddInterceptors(interceptor).Options))
        {
            broken = new FaultingStorage(realStorage);
            service = new ProjectFileService(failingDb, broken, new SubmissionLock(failingDb), settings, NullLogger<ProjectFileService>.Instance);
            try { await service.UploadAsync(id, ownerId, Upload()); Check(false, "DB failure must fail"); }
            catch (ResourceOperationException ex) { Check(ex.Status == 503 && broken.LastKey is not null && !File.Exists(Path.Combine(root, broken.LastKey))
                && await db.ProjectFiles.CountAsync() == before, "DB upload failure cleans up newly stored file"); }
        }
        var existing = await db.ProjectFiles.AsNoTracking().FirstAsync(f => f.ProjectSubmissionId == id);
        await using (var failingDb = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(db.Database.GetConnectionString()).AddInterceptors(interceptor).Options))
        {
            service = new ProjectFileService(failingDb, realStorage, new SubmissionLock(failingDb), settings, NullLogger<ProjectFileService>.Instance);
            try { await service.DeleteAsync(existing.Id, ownerId); Check(false, "DB deletion failure must fail"); }
            catch (ResourceOperationException ex) { Check(ex.Status == 503 && File.Exists(Path.Combine(root, existing.StoredFileName))
                && await db.ProjectFiles.AnyAsync(f => f.Id == existing.Id), "DB deletion failure preserves record and file"); }
        }
        broken = new FaultingStorage(realStorage) { FailDelete = true };
        service = new ProjectFileService(db, broken, new SubmissionLock(db), settings, NullLogger<ProjectFileService>.Instance);
        try { await service.DeleteAsync(existing.Id, ownerId); Check(false, "Physical deletion failure must not report success"); }
        catch (ResourceOperationException ex) { Check(ex.Status == 503 && !await db.ProjectFiles.AnyAsync(f => f.Id == existing.Id)
            && File.Exists(Path.Combine(root, existing.StoredFileName)), "Physical delete failure reports inaccessible orphan for cleanup"); }
        await realStorage.DeleteAsync(existing.StoredFileName);
    }
}

internal sealed class TestOperationalSettings(int maxFileSizeMB, int maxFilesPerSubmission) : IOperationalSettingsService
{
    public Task<OperationalSettingsViewModel> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(new OperationalSettingsViewModel
    {
        MaxFileSizeMB = maxFileSizeMB, MaxFilesPerSubmission = maxFilesPerSubmission
    });
    public Task UpdateAsync(OperationalSettingsViewModel value, string updatedBy, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FaultingStorage(IFileStorageService inner) : IFileStorageService
{
    public bool FailStore { get; init; }
    public bool FailDelete { get; init; }
    public string? LastKey { get; private set; }
    public async Task<string> StoreAsync(Stream content, string extension, CancellationToken cancellationToken = default)
    {
        if (FailStore) throw new IOException("simulated storage failure");
        return LastKey = await inner.StoreAsync(content, extension, cancellationToken);
    }
    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default) => inner.OpenReadAsync(key, cancellationToken);
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => FailDelete ? Task.FromException(new IOException("simulated cleanup failure")) : inner.DeleteAsync(key, cancellationToken);
}
internal sealed class ResourceSaveFailure : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<ProjectFile>().Any(e => e.State is EntityState.Added or EntityState.Deleted))
            throw new DbUpdateException("simulated ProjectFile persistence failure");
        return ValueTask.FromResult(result);
    }
}
