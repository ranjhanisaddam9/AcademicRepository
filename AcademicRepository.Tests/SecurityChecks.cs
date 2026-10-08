using System.Text;
using AcademicRepository.Services;
using Microsoft.Extensions.Options;

internal static partial class IntegrationChecks
{
    private static async Task SecurityChecks()
    {
        Check(ResourceFileValidator.SafeOriginalName("../../private/report.pdf") == "report.pdf",
            "Unix traversal segments are removed from display filenames");
        Check(ResourceFileValidator.SafeOriginalName(@"..\..\private\report.pdf") == "report.pdf",
            "Windows traversal segments are removed from display filenames");
        foreach (var extension in new[] { ".exe", ".js", ".html", ".cshtml", ".svg", ".bat", ".ps1" })
            Check(!ResourceFileValidator.ContentTypes.ContainsKey(extension), "Dangerous or active extension is not upload-allowlisted: " + extension);

        await using var validPdf = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7\n"));
        Check(await ResourceFileValidator.MatchesAsync(validPdf, ".pdf"), "PDF content signature is accepted independently of browser MIME type");
        await using var disguisedExecutable = new MemoryStream(Encoding.ASCII.GetBytes("MZ executable payload"));
        Check(!await ResourceFileValidator.MatchesAsync(disguisedExecutable, ".pdf"), "Executable bytes renamed with an allowed extension are rejected");

        var testRoot = Path.Combine(Path.GetTempPath(), "AcademicRepository_Security_" + Guid.NewGuid().ToString("N"));
        var webRoot = Path.Combine(testRoot, "wwwroot");
        Directory.CreateDirectory(webRoot);
        try
        {
            var environment = new EmailTestEnvironment { ContentRootPath = testRoot, WebRootPath = webRoot };
            var storage = new LocalFileStorageService(Options.Create(new FileStorageOptions
            {
                RootPath = Path.Combine(testRoot, "private-files")
            }), environment);
            await using var upload = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7\n"));
            var key = await storage.StoreAsync(upload, ".pdf");
            Check(System.Text.RegularExpressions.Regex.IsMatch(key, "^[0-9a-f]{32}\\.pdf$")
                && !key.Contains("report", StringComparison.OrdinalIgnoreCase), "Stored file name is random and unrelated to the client filename");
            Check(!Path.GetFullPath(Path.Combine(testRoot, "private-files")).StartsWith(
                Path.GetFullPath(webRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Private storage resolves outside wwwroot");
            foreach (var maliciousKey in new[] { "../report.pdf", @"..\..\report.pdf", @"C:\private\report.pdf", "/etc/passwd" })
            {
                try { await storage.OpenReadAsync(maliciousKey); Check(false, "Storage rejects traversal key " + maliciousKey); }
                catch (InvalidDataException) { Check(true, "Storage rejects traversal key " + maliciousKey); }
            }
            await storage.DeleteAsync(key);
        }
        finally
        {
            if (testRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(testRoot).StartsWith("AcademicRepository_Security_", StringComparison.Ordinal)
                && Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }

        using var csv = new StringWriter();
        await CsvWriter.WriteRowAsync(csv, ["=HYPERLINK(\"https://example.invalid\")", "plain"]);
        Check(csv.ToString().StartsWith("\"'=HYPERLINK", StringComparison.Ordinal), "CSV values with formula prefixes are neutralized before quoting");
    }
}
