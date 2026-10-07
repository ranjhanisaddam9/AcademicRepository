using System.IO.Compression;
using System.Text;

namespace AcademicRepository.Services;

public static class ResourceFileValidator
{
    public static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>
    {
        [".pdf"] = "application/pdf", [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".ppt"] = "application/vnd.ms-powerpoint", [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".xls"] = "application/vnd.ms-excel", [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".csv"] = "text/csv", [".txt"] = "text/plain", [".zip"] = "application/zip",
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png"
    };
    public static string SafeOriginalName(string name) => Path.GetFileName(name.Replace('\\', '/'));

    public static async Task<bool> MatchesAsync(Stream stream, string extension)
    {
        var header = new byte[8];
        var read = await stream.ReadAtLeastAsync(header, 8, throwOnEndOfStream: false);
        stream.Position = 0;
        bool Starts(byte[] signature) => read >= signature.Length && header.AsSpan(0, signature.Length).SequenceEqual(signature);
        switch (extension)
        {
            case ".pdf": return Starts("%PDF-"u8.ToArray());
            case ".jpg": case ".jpeg": return Starts([255, 216, 255]);
            case ".png": return Starts([137, 80, 78, 71, 13, 10, 26, 10]);
            case ".doc": case ".xls": case ".ppt": return Starts([208, 207, 17, 224, 161, 177, 26, 225]);
            case ".zip": case ".docx": case ".pptx": case ".xlsx":
                if (!Starts([80, 75, 3, 4]) && !Starts([80, 75, 5, 6])) return false;
                try
                {
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                    if (archive.Entries.Count > 10000) return false;
                    var required = extension switch { ".docx" => "word/document.xml", ".pptx" => "ppt/presentation.xml", ".xlsx" => "xl/workbook.xml", _ => null };
                    return required is null || (archive.GetEntry("[Content_Types].xml") is not null && archive.GetEntry(required) is not null);
                }
                catch (InvalidDataException) { return false; }
                finally { stream.Position = 0; }
            case ".csv": case ".txt":
                try
                {
                    using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                    var buffer = new char[8192];
                    int count;
                    while ((count = await reader.ReadAsync(buffer)) > 0)
                        if (buffer.AsSpan(0, count).Contains('\0')) return false;
                    return true;
                }
                catch (DecoderFallbackException) { return false; }
                finally { stream.Position = 0; }
            default: return false;
        }
    }
}
