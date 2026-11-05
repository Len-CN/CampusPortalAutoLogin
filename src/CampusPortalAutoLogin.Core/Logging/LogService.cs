using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CampusPortalAutoLogin.Core.Models;
namespace CampusPortalAutoLogin.Core.Logging;
public sealed class LogRedactor
{
    private readonly object credentialGate = new();
    private string[] credentials = [];
    public void SetCredentials(string username, string password)
    {
        // 保留本进程使用过的凭据，保护尚未完成的旧请求及历史日志展示。
        lock (credentialGate) credentials = credentials.Concat(new[] { username, password }
            .Where(s => !string.IsNullOrEmpty(s)).SelectMany(s => new[] { s, Uri.EscapeDataString(s), Uri.EscapeDataString(Uri.EscapeDataString(s)) }))
            .Distinct().OrderByDescending(s => s.Length).ToArray();
    }
    public void AddCredentials(string username, string password)
    {
        SetCredentials(username, password);
    }
    public string Redact(string input)
    {
        var result = input ?? "";
        foreach (var value in credentials) result = result.Replace(value, "[已隐藏]", StringComparison.Ordinal);
        result = Regex.Replace(result, @"(?im)(?:set-cookie|cookie|authorization)\s*:\s*[^\r\n]*", "[敏感头已隐藏]");
        result = Regex.Replace(result, @"https?://[^\s<>""']+", m => Uri.TryCreate(m.Value, UriKind.Absolute, out var uri) ? new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Path) + "?[已隐藏]" : "[地址已隐藏]", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"(?i)(password|pwd|operatorPwd|loginToken|token|userIndex|queryString|wlanuserip|mac|session|username|userId)[""']?\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s&,;]+)", "$1=[已隐藏]");
        result = Regex.Replace(result, @"(?i)\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b|\b[0-9a-f]{12}\b", "[MAC已隐藏]");
        return result.Replace("\r", " ").Replace("\n", " ");
    }
}
public sealed class LogService(string directory) : ILogSink
{
    private readonly object gate = new();
    private string LogPath => Path.Combine(directory, "latest.jsonl");
    public LogRedactor Redactor { get; } = new();
    public string DirectoryPath => directory;
    public event Action<LogEntry>? EntryAdded;
    public event Action? Cleared;
    public string? LastWriteError { get; private set; }
    public void Write(LogLevel level, string source, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, Redactor.Redact(source), Redactor.Redact(message));
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(LogPath, JsonSerializer.Serialize(entry) + Environment.NewLine);
                LastWriteError = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LastWriteError = "日志写入失败：" + e.GetType().Name; }
        }
        EntryAdded?.Invoke(entry);
    }
    public IReadOnlyList<LogEntry> ReadRecent()
    {
        lock (gate)
        {
            if (!Directory.Exists(directory)) return [];
            var entries = new Queue<LogEntry>();
            foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl").OrderByDescending(p => p).Take(7).Reverse())
                foreach (var line in File.ReadLines(file))
                {
                    try { var e = JsonSerializer.Deserialize<LogEntry>(line); if (e is not null) entries.Enqueue(e with { Message = Redactor.Redact(e.Message) }); }
                    catch (JsonException) { }
                    while (entries.Count > 2000) entries.Dequeue();
                }
            return entries.ToArray();
        }
    }
    public void Clear()
    {
        try
        {
            lock (gate) { if (Directory.Exists(directory)) foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl")) File.Delete(file); }
        }
        finally { Cleared?.Invoke(); } // 部分文件被占用时，也让界面重新读取实际剩余记录。
    }
}
