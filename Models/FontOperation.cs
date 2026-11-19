using System.Text.Json.Serialization;

namespace FontBook.Models;

/// <summary>精确的字体注册表原值；仅用于可验证的恢复。</summary>
public sealed class RegistryEntry
{
    public string Hive { get; set; } = "";
    public string RegistryPath { get; set; } = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
    public string ValueName { get; set; } = "";
    public string ValueData { get; set; } = "";
    public string ValueKind { get; set; } = "String";
    public bool Removed { get; set; }
    public bool RemovalAttempted { get; set; }
    public bool Restored { get; set; }
}

/// <summary>字体事务日志。额外字段为可选，旧 JSON 可安全读取；旧记录默认只读。</summary>
public sealed class FontOperation
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Family { get; set; } = "";
    public string Time { get; set; } = "";
    public List<string> Files { get; set; } = new();
    public List<RegistryEntry> Registry { get; set; } = new();
    public List<string> DeletedFiles { get; set; } = new();
    public string BackupDir { get; set; } = "";
    public bool Undone { get; set; }
    public int FormatVersion { get; set; }
    public string Status { get; set; } = "";
    public string LastError { get; set; } = "";
    public string Integrity { get; set; } = "";
    public List<FontOperationFile> FileRecords { get; set; } = new();

    [JsonIgnore]
    public string Summary => Kind == "uninstall"
        ? $"卸载「{Family ?? ""}」：{DeletedFiles?.Count ?? 0} 个文件"
        : $"停用「{Family ?? ""}」：{Registry?.Count ?? 0} 条注册项";
}

public sealed class FontOperationFile
{
    public string Path { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public bool DeleteAttempted { get; set; }
    public bool Deleted { get; set; }
    public bool Restored { get; set; }
}

/// <summary>历史列表展示模型：Unsafe/Legacy 记录可以查看但不能执行。</summary>
public sealed class FontOperationHistoryEntry
{
    public FontOperation Operation { get; init; } = new();
    public bool CanUndo { get; init; }
    public string SafetyMessage { get; init; } = "";
    public string LogPath { get; init; } = "";
}
