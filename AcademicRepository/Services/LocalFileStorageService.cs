using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace AcademicRepository.Services;

public sealed class FileStorageOptions
{
    [Required] public string RootPath { get; set; } = "App_Data/ProjectFiles";
    [Range(1, 200)] public int MaxFileSizeMB { get; set; } = 50;
    [Range(1, 100)] public int MaxFilesPerSubmission { get; set; } = 10;
    public long MaxBytes => MaxFileSizeMB * 1024L * 1024;
}

public interface IFileStorageService
{
    Task<string> StoreAsync(Stream content, string extension, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public sealed partial class LocalFileStorageService : IFileStorageService
{
    private readonly string root;
    private readonly long maxBytes;
    public LocalFileStorageService(IOptions<FileStorageOptions> options, IWebHostEnvironment environment)
    {
        var settings = options.Value;
        root = Path.GetFullPath(settings.RootPath, environment.ContentRootPath);
        var webRoot = Path.GetFullPath(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"));
        if (root.Equals(webRoot, StringComparison.OrdinalIgnoreCase) || root.StartsWith(webRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("FileStorage:RootPath must be outside the web root.");
        // The request/feature limit is dynamically administered in OperationalSettingsService.
        // Keep this storage boundary at the non-editable product ceiling (100 MB).
        maxBytes = 100L * 1024 * 1024;
        Directory.CreateDirectory(root);
        // Reject symlink/junction ancestors so private storage cannot resolve into a served directory.
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("Storage directory must not use symbolic links or junctions.");
    }

    [GeneratedRegex(@"\A[0-9a-f]{32}\.[a-z0-9]{1,5}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
    private string Resolve(string key)
    {
        if (!KeyPattern().IsMatch(key)) throw new InvalidDataException("Invalid storage key.");
        var path = Path.Combine(root, key);
        if (System.IO.File.Exists(path) && System.IO.File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Storage links are not supported.");
        return path;
    }
    public async Task<string> StoreAsync(Stream content, string extension, CancellationToken cancellationToken = default)
    {
        if (!ResourceFileValidator.ContentTypes.ContainsKey(extension)) throw new InvalidDataException("Unsupported file extension.");
        var key = Guid.NewGuid().ToString("N") + extension;
        var path = Resolve(key);
        try
        {
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long length = 0;
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) != 0)
            {
                length += read;
                if (length > maxBytes) throw new InvalidDataException("File exceeds the configured size limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (length == 0) throw new InvalidDataException("Empty files are not allowed.");
            return key;
        }
        catch { System.IO.File.Delete(path); throw; }
    }
    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(new FileStream(Resolve(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true));
    }
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        System.IO.File.Delete(Resolve(key));
        return Task.CompletedTask;
    }
}
