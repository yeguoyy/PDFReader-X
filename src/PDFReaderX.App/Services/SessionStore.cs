using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PDFReaderX.App.Controls;
using PDFReaderX.App.ViewModels;
using PDFReaderX.Core.Services;

namespace PDFReaderX.App.Services;

/// <summary>
/// 退出自动会话：把批注、书签与阅读位置保存到本地会话目录（不内嵌 PDF），
/// 下次打开同一 PDF 时自动恢复。
/// </summary>
public static class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string SessionDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PDFReaderX",
        "sessions");

    private static string IndexPath => Path.Combine(SessionDir, "index.json");

    /// <summary>保存当前会话并更新索引，返回会话文件路径。</summary>
    public static string Save(
        InfiniteCanvas canvas,
        PdfRenderService document,
        IReadOnlyList<BookmarkViewModel> bookmarks)
    {
        Directory.CreateDirectory(SessionDir);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.FilePath)))[..16];
        var name = Sanitize(Path.GetFileNameWithoutExtension(document.FilePath));
        var sessionFile = Path.Combine(SessionDir, name + "-" + hash + ".pdfrx");
        PdfrxStore.SaveAsync(sessionFile, canvas, document, bookmarks, embedPdf: false).GetAwaiter().GetResult();
        Record(document.FilePath, sessionFile);
        return sessionFile;
    }

    /// <summary>查找源 PDF 对应的会话文件，不存在返回 null。</summary>
    public static string? FindSession(string sourcePath)
    {
        if (!File.Exists(IndexPath))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(sourcePath);
            var records = JsonSerializer.Deserialize<List<SessionRecord>>(File.ReadAllText(IndexPath), JsonOptions);
            var match = records?.FirstOrDefault(record =>
                Path.GetFullPath(record.SourcePath).Equals(fullPath, StringComparison.OrdinalIgnoreCase));
            return match is not null && File.Exists(match.SessionFile) ? match.SessionFile : null;
        }
        catch
        {
            return null;
        }
    }

    private static void Record(string sourcePath, string sessionFile)
    {
        try
        {
            Directory.CreateDirectory(SessionDir);
            var records = new List<SessionRecord>();
            if (File.Exists(IndexPath))
            {
                records = JsonSerializer.Deserialize<List<SessionRecord>>(File.ReadAllText(IndexPath), JsonOptions) ?? new List<SessionRecord>();
            }
            records.RemoveAll(record =>
                Path.GetFullPath(record.SourcePath).Equals(Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase));
            records.Add(new SessionRecord { SourcePath = sourcePath, SessionFile = sessionFile, ModifiedAt = DateTime.Now });
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(records, JsonOptions));
        }
        catch
        {
            // 索引写入失败不影响退出
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private sealed class SessionRecord
    {
        public string SourcePath { get; set; } = "";
        public string SessionFile { get; set; } = "";
        public DateTime ModifiedAt { get; set; }
    }
}
