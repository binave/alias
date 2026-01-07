// Copyright (c) 2026 bin jin.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

#region Data Models

/// <summary>配置设置 - 存储从 .alias 解析的配置</summary>
/// <remarks>
/// 这个类封装了所有从配置文件中读取的配置项：
/// - AliasCommand: 别名对应的实际命令
/// - EnvironmentVars: 需要设置的环境变量
/// - ExecMode: 进程替换模式（-1=禁用，0=立即退出，>0=延迟秒数后退出）
/// - ExclArg: 排除通配符处理的参数索引集合
/// - CharsetConv: 字符集转换设置
/// </remarks>
internal sealed class ConfigSettings {
    /// <summary>别名对应的实际命令字符串</summary>
    public string? AliasCommand { get; set; }

    /// <summary>需要设置的环境变量字典（变量名 -> 变量值）</summary>
    public Dictionary<string, string> EnvironmentVars { get; } = new();

    /// <summary>exec 模式设置（-1=禁用，0=立即退出，>0=延迟秒数后退出）</summary>
    public int ExecMode { get; set; } = -1;

    /// <summary>排除通配符处理的参数索引集合（如 EXCL_ARG=2 表示第2个参数不处理通配符）</summary>
    public HashSet<int> ExclArgs { get; } = new();

    /// <summary>字符集转换配置（如 UTF-8,GBK）</summary>
    public string? CharsetConv { get; set; }

    /// <summary>字符集转换条件正则表达式</summary>
    public string? CharsetConvCondition { get; set; }

    /// <summary>输出前缀格式（支持 %F %T 等 date 格式）</summary>
    public string? Prefix { get; set; }

    /// <summary>前缀条件正则表达式（匹配参数时才启用前缀）</summary>
    public string? PrefixCondition { get; set; }
}

/// <summary>缓存条目 - 存储在 SQLite 数据库中的缓存记录</summary>
/// <remarks>
/// 对应数据库表结构：
/// CREATE TABLE cache (key TEXT PRIMARY KEY, value TEXT NOT NULL, args TEXT, env TEXT, exec_mode INTEGER, prefix TEXT, prefix_cond TEXT, charset_conv TEXT, charset_conv_cond TEXT, update_at INTEGER NOT NULL) WITHOUT ROWID;
/// </remarks>
internal sealed class CacheEntry {
    /// <summary>缓存键（别名名称，小写）</summary>
    public string Key { get; set; } = "";

    /// <summary>缓存值（解析后的可执行文件完整路径）</summary>
    public string Value { get; set; } = "";

    /// <summary>解析后的参数（通配符已展开）</summary>
    public string? Args { get; set; }

    /// <summary>环境变量（使用 \0 分隔的键值对字符串：KEY=VALUE\0KEY=VALUE\0...）</summary>
    public string? Env { get; set; }

    /// <summary>exec 模式设置（-1=禁用，0=立即退出，>0=延迟秒数后退出）</summary>
    public int ExecMode { get; set; } = -1;

    /// <summary>输出前缀格式</summary>
    public string? Prefix { get; set; }

    /// <summary>前缀条件正则表达式</summary>
    public string? PrefixCondition { get; set; }

    /// <summary>字符集转换配置（如 UTF-8,GBK）</summary>
    public string? CharsetConv { get; set; }

    /// <summary>字符集转换条件正则表达式</summary>
    public string? CharsetConvCondition { get; set; }

    /// <summary>更新时间（Unix 时间戳，秒）</summary>
    public long UpdateAt { get; set; }
}

#endregion

#region Native Interop

/// <summary>Windows API 互操作</summary>
/// <remarks>
/// 这个类封装了调用 Windows 底层 API 所需的结构体和函数，
/// 用于实现低级别的进程创建、控制台操作等功能。
/// </remarks>
internal static class NativeApi {
    /// <summary>无限等待常量 (0xFFFFFFFF) - 用于 WaitForSingleObject</summary>
    public const uint Infinite = 0xFFFFFFFF;

    /// <summary>进程启动信息结构体</summary>
    /// <remarks>
    /// 包含新进程的主窗口特性、标准句柄等信息。
    /// 关键字段：
    /// - dwFlags=0x100 时使用 STARTF_USESTDHANDLES 标志，表示使用 hStdInput/Output/Error
    /// - hStdInput/Output/Error: 重定向的标准输入/输出/错误句柄
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    /// <summary>进程信息结构体</summary>
    /// <remarks>
    /// CreateProcess 返回的新进程和主线程的句柄及 ID。
    /// - hProcess: 进程句柄，用于等待进程结束
    /// - hThread: 主线程句柄
    /// - dwProcessId: 进程 ID
    /// - dwThreadId: 线程 ID
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    /// <summary>创建新进程及其主线程</summary>
    /// <param name="lpApplicationName">应用程序名称（可为 null，使用命令行）</param>
    /// <param name="lpCommandLine">命令行字符串</param>
    /// <param name="lpProcessAttributes">进程安全属性（为 IntPtr.Zero 表示默认）</param>
    /// <param name="lpThreadAttributes">线程安全属性（为 IntPtr.Zero 表示默认）</param>
    /// <param name="bInheritHandles">是否继承调用进程的句柄（true 表示继承标准输入/输出）</param>
    /// <param name="dwCreationFlags">创建标志（0 表示默认）</param>
    /// <param name="lpEnvironment">环境块（为 IntPtr.Zero 表示继承父进程环境）</param>
    /// <param name="lpCurrentDirectory">当前目录（为 null 表示继承父进程目录）</param>
    /// <param name="lpStartupInfo">启动信息（包含重定向的标准句柄）</param>
    /// <param name="lpProcessInformation">输出的进程信息（句柄和 ID）</param>
    /// <returns>成功返回 true，失败返回 false</returns>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcess(
        string lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string lpCurrentDirectory,
        ref StartupInfo lpStartupInfo, out ProcessInformation lpProcessInformation);

    /// <summary>等待指定对象进入信号状态</summary>
    /// <param name="hHandle">要等待的对象句柄（如进程句柄）</param>
    /// <param name="dwMilliseconds">等待超时时间（毫秒，Infinite 表示无限等待）</param>
    /// <returns>等待状态（0 表示对象已信号）</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    /// <summary>关闭打开的对象句柄</summary>
    /// <param name="hObject">要关闭的句柄（如进程句柄或线程句柄）</param>
    /// <returns>成功返回 true，失败返回 false</returns>
    /// <remarks>
    /// 必须关闭 CreateProcess 返回的进程和线程句柄以避免资源泄漏。
    /// </remarks>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    /// <summary>获取指定进程的退出代码</summary>
    /// <param name="hProcess">进程句柄</param>
    /// <param name="lpExitCode">输出的退出代码（进程返回值）</param>
    /// <returns>成功返回 true，失败返回 false</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    /// <summary>设置当前进程的环境变量</summary>
    /// <param name="lpName">环境变量名</param>
    /// <param name="lpValue">环境变量值（为 null 表示删除变量）</param>
    /// <returns>成功返回 true，失败返回 false</returns>
    [DllImport("kernel32.dll")]
    public static extern bool SetEnvironmentVariable(string lpName, string lpValue);

    /// <summary>安全属性结构体（用于管道创建）</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityAttributes {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    /// <summary>创建匿名管道</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SecurityAttributes lpPipeAttributes, uint nSize);

    /// <summary>从文件或管道读取数据</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    /// <summary>设置句柄信息</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    /// <summary>句柄标志：可继承</summary>
    public const uint HANDLE_FLAG_INHERIT = 0x00000001;

    /// <summary>启动标志：使用标准句柄</summary>
    public const int STARTF_USESTDHANDLES = 0x00000100;

    /// <summary>创建符号链接</summary>
    /// <param name="lpSymlinkFileName">符号链接路径</param>
    /// <param name="lpTargetFileName">目标路径</param>
    /// <param name="dwFlags">0=文件, 1=目录</param>
    /// <returns>成功返回 true</returns>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, uint dwFlags);

    /// <summary>获取文件属性</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetFileAttributes(string lpFileName);

    /// <summary>文件属性：重解析点（符号链接）</summary>
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

    /// <summary>无效文件属性</summary>
    public const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
}

#endregion

#region Config Parser

/// <summary>配置文件解析器</summary>
/// <remarks>
/// 解析 %USERPROFILE%\.alias 配置文件，支持 Linux shell 兼容的语法。
/// 支持的配置项：
/// - alias name='command': 定义命令别名
/// - export VAR=value: 设置全局环境变量
/// - EXEC=true: 启用进程替换模式
/// - CHARSET_CONV=UTF-8,GBK: 字符集转换
/// - EXCL_ARG=1,2: 忽略参数通配符处理
/// - PREFIX='/-[Tt]/ && "%F %T "': 增加时间戳前缀，可支持正则表达式判断
/// </remarks>
internal static class ConfigParser {
    /// <summary>匹配 alias 语法的正则表达式：alias name=value</summary>
    private static readonly Regex AliasRegex = new(@"^\s*alias\s+([\w.-]+)\s*=\s*(.+)$", RegexOptions.Compiled);

    /// <summary>匹配 export 语法的正则表达式：export VAR=value</summary>
    private static readonly Regex ExportRegex = new(@"^\s*export\s+([\w]+)\s*=\s*(.+)$", RegexOptions.Compiled);

    /// <summary>匹配键值对语法的正则表达式：KEY=value</summary>
    private static readonly Regex KeyValueRegex = new(@"^\s*([\w]+)\s*=\s*(.+)$", RegexOptions.Compiled);

    /// <summary>获取配置文件路径</summary>
    /// <returns>%USERPROFILE%\.alias</returns>
    public static string GetConfigPath() {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".alias");
    }

    /// <summary>确保配置文件存在，不存在则创建空文件</summary>
    /// <param name="configPath">配置文件路径</param>
    /// <returns>文件是否存在（包括新创建的）</returns>
    public static bool EnsureConfigExists(string configPath) {
        if (File.Exists(configPath)) {
            return true;
        }

        try {
            File.WriteAllText(configPath, @"# alias configuration file

# alias bfg='""C:\Program Files\java*\bin\java.exe"" -jar D:\bfg-*\bfg-*.jar'
# alias git='C:\Git*\bin\git.exe'
");
            Console.Error.WriteLine($"Created empty config file: {configPath}");
            return true;
        } catch {
            Console.Error.WriteLine($"Failed to create config file: {configPath}");
            return false;
        }
    }

    /// <summary>解析配置文件并提取指定别名的配置</summary>
    /// <param name="configPath">配置文件路径</param>
    /// <param name="aliasName">要查找的别名名称（程序文件名）</param>
    /// <returns>解析成功返回配置对象，失败或未找到别名返回 null</returns>
    /// <remarks>
    /// 解析逻辑：
    /// 1. 按行读取配置文件
    /// 2. 跳过空行和注释（# 开头）
    /// 3. 提取所有 export 语句（设置环境变量）
    /// 4. 提取键值对配置（EXEC, CHARSET_CONV, EXCL_ARG, PREFIX）
    /// 5. 非关键词的 VAR=value 作为临时环境变量，只对下一行 alias 有效
    /// 6. 找到匹配的 alias 定义后停止解析
    /// </remarks>
    public static ConfigSettings? Parse(string configPath, string aliasName) {
        var config = new ConfigSettings();

        if (!File.Exists(configPath)) {
            return null;
        }

        // 临时环境变量（只对下一行 alias 有效）
        var tempEnvVars = new Dictionary<string, string>();

        try {
            foreach (string rawLine in File.ReadLines(configPath)) {
                string line = rawLine.Trim();

                // 跳过空行和注释
                if (string.IsNullOrEmpty(line) || line.StartsWith('#')) {
                    continue;
                }

                var aliasMatch = AliasRegex.Match(line); // 检查 alias 定义
                if (aliasMatch.Success) {
                    if (aliasMatch.Groups[1].Value.Equals(aliasName, StringComparison.OrdinalIgnoreCase)) {
                        config.AliasCommand = StripQuotes(aliasMatch.Groups[2].Value);
                        // 合并临时环境变量到配置中
                        foreach (var kv in tempEnvVars) {
                            config.EnvironmentVars[kv.Key] = kv.Value;
                        }
                        break; // 找到匹配的别名后停止
                    }

                    // 遇到其他 alias，清空临时环境变量和临时关键词设置
                    tempEnvVars.Clear();
                    config.ExecMode = -1;
                    config.ExclArgs.Clear();
                    config.Prefix = null;
                    config.CharsetConv = null;
                    continue;
                }

                // 检查 export 语句（全局环境变量）
                var exportMatch = ExportRegex.Match(line);
                if (exportMatch.Success) {
                    config.EnvironmentVars[exportMatch.Groups[1].Value] = StripQuotes(exportMatch.Groups[2].Value);
                    continue;
                }

                var kvMatch = KeyValueRegex.Match(line); // 检查键值对
                if (kvMatch.Success) {
                    string key = kvMatch.Groups[1].Value;
                    string value = kvMatch.Groups[2].Value.Trim();

                    switch (key.ToUpperInvariant()) { // 检查是否是关键词
                        case "EXEC":
                            // true: 立即退出，数字 > 0: 延迟指定秒数后退出，其他: 忽略
                            if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) {
                                config.ExecMode = 0;
                            } else if (int.TryParse(value, out int seconds) && seconds > 0) {
                                config.ExecMode = seconds;
                            }
                            // 其他值保持默认 -1（禁用）
                            break;
                        case "EXCL_ARG":
                            // 解析排除通配符处理的参数索引（支持逗号分隔多个值）
                            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries)) {
                                if (int.TryParse(part.Trim(), out int argIndex) && argIndex > 0) {
                                    config.ExclArgs.Add(argIndex);
                                }
                            }
                            break;
                        case "CHARSET_CONV":
                            // 支持格式: "UTF-8,GBK" 或 '/regex/ && "UTF-8,GBK"'
                            ParseConditionalValue(value, out string? charsetConv, out string? charsetConvCond);
                            config.CharsetConv = charsetConv;
                            config.CharsetConvCondition = charsetConvCond;
                            break;
                        case "PREFIX":
                            // 支持格式: "format" 或 '/regex/ && "format"'
                            ParseConditionalValue(value, out string? prefix, out string? prefixCond);
                            config.Prefix = prefix;
                            config.PrefixCondition = prefixCond;
                            break;
                        default:
                            // 非关键词的 VAR=value，作为临时环境变量
                            tempEnvVars[key] = StripQuotes(value);
                            break;
                    }
                }
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"Error reading alias configuration file: {ex.Message}");
            return null;
        }

        return config;
    }

    /// <summary>去除值两端的引号</summary>
    /// <param name="value">可能包含引号的字符串</param>
    /// <returns>去除引号后的字符串</returns>
    /// <remarks>
    /// 支持：
    /// - 单引号：'value' -> value
    /// - 双引号："value" -> value
    /// - 无引号：value -> value
    /// </remarks>
    private static string StripQuotes(string value) {
        value = value.Trim();
        if (value.Length >= 2) {
            if ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"')) {
                return value[1..^1];
            }
        }
        return value;
    }

    /// <summary>解析带条件的配置值</summary>
    /// <param name="value">原始值，如 "value" 或 '/regex/ && "value"'</param>
    /// <param name="result">输出：实际值</param>
    /// <param name="condition">输出：条件正则表达式（可为 null）</param>
    private static void ParseConditionalValue(string value, out string? result, out string? condition) {
        result = null;
        condition = null;

        value = value.Trim();
        if (string.IsNullOrEmpty(value)) return;

        // 先去掉最外层引号
        string inner = StripQuotes(value);

        // 查找 /regex/ && "value" 模式
        if (inner.StartsWith('/')) {
            int regexEnd = inner.IndexOf("/ &&");
            if (regexEnd > 1) {
                // 提取正则表达式（去掉 / 和 /）
                condition = inner[1..regexEnd];
                // 提取实际值（&& 后面的部分）
                string valuePart = inner[(regexEnd + 4)..].Trim();
                result = StripQuotes(valuePart);
                return;
            }
        }

        // 无条件，整个值作为结果
        result = inner;
    }

    /// <summary>获取配置文件中所有别名定义</summary>
    /// <param name="configPath">配置文件路径</param>
    /// <returns>别名名称和命令的字典</returns>
    public static Dictionary<string, string> GetAllAliases(string configPath) {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(configPath)) {
            return aliases;
        }

        try {
            foreach (string rawLine in File.ReadLines(configPath)) {
                string line = rawLine.Trim();

                // 跳过空行和注释
                if (string.IsNullOrEmpty(line) || line.StartsWith('#')) {
                    continue;
                }

                // 检查 alias 定义
                var aliasMatch = AliasRegex.Match(line);
                if (aliasMatch.Success) {
                    string name = aliasMatch.Groups[1].Value;
                    string command = StripQuotes(aliasMatch.Groups[2].Value);
                    aliases[name] = command;
                }
            }
        } catch {
            // 忽略错误
        }

        return aliases;
    }
}

#endregion

#region Cache Manager

/// <summary>缓存管理器</summary>
/// <remarks>
/// 使用 SQLite 嵌入式数据库缓存已解析的路径以提升性能。
/// 数据库文件: %USERPROFILE%\.alias.db
/// 缓存失效条件：
/// 1. 缓存数据库文件不存在
/// 2. 配置文件修改时间晚于缓存行修改时间 "update_at"
/// 3. 缓存的文件路径 "value" 不存在
/// </remarks>
internal static class CacheManager {
    /// <summary>获取数据库文件路径</summary>
    /// <returns>%USERPROFILE%\.alias.db</returns>
    private static string GetDatabasePath() {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".alias.db");
    }

    /// <summary>获取数据库连接字符串</summary>
    private static string GetConnectionString() {
        return $"Data Source={GetDatabasePath()};Mode=ReadWriteCreate;Cache=Shared";
    }

    /// <summary>获取文件的真实修改时间（解析软链接）</summary>
    private static DateTime GetRealLastWriteTimeUtc(string path) {
        var fileInfo = new FileInfo(path);
        // 如果是软链接，获取目标文件的修改时间
        if (fileInfo.LinkTarget != null) {
            string? target = fileInfo.ResolveLinkTarget(true)?.FullName;
            if (target != null && File.Exists(target)) {
                return File.GetLastWriteTimeUtc(target);
            }
        }
        return File.GetLastWriteTimeUtc(path);
    }

    /// <summary>检查缓存是否有效（比较 .alias 和 .alias.db 修改时间）</summary>
    public static bool IsCacheValid(string configPath) {
        string dbPath = GetDatabasePath();
        if (!File.Exists(dbPath)) return false;
        return GetRealLastWriteTimeUtc(dbPath) >= GetRealLastWriteTimeUtc(configPath);
    }

    /// <summary>初始化数据库，创建表（如果不存在）</summary>
    private static void InitializeDatabase(SqliteConnection conn) {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS cache (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                args TEXT,
                env TEXT,
                exec_mode INTEGER DEFAULT -1,
                prefix TEXT,
                prefix_cond TEXT,
                charset_conv TEXT,
                charset_conv_cond TEXT,
                update_at INTEGER NOT NULL
            ) WITHOUT ROWID;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>将环境变量字典序列化为 \0 分隔的字符串</summary>
    /// <param name="env">环境变量字典</param>
    /// <returns>格式: KEY=VALUE\0KEY=VALUE\0...</returns>
    public static string SerializeEnv(Dictionary<string, string> env) {
        if (env == null || env.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var kv in env) {
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        }
        return sb.ToString();
    }

    /// <summary>将 \0 分隔的字符串反序列化为环境变量字典</summary>
    /// <param name="env">格式: KEY=VALUE\0KEY=VALUE\0...</param>
    /// <returns>环境变量字典</returns>
    public static Dictionary<string, string> DeserializeEnv(string? env) {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(env)) return result;

        var pairs = env.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs) {
            int eq = pair.IndexOf('=');
            if (eq > 0) {
                result[pair[..eq]] = pair[(eq + 1)..];
            }
        }
        return result;
    }

    /// <summary>从缓存数据库中获取缓存条目（快速路径，不检查有效性）</summary>
    /// <param name="key">缓存键（别名名称，小写）</param>
    /// <returns>缓存条目，未找到返回 null</returns>
    public static CacheEntry? GetCachedEntry(string key) {
        string dbPath = GetDatabasePath();
        if (!File.Exists(dbPath)) return null;

        try {
            using var conn = new SqliteConnection(GetConnectionString());
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value, args, env, exec_mode, prefix, prefix_cond, charset_conv, charset_conv_cond FROM cache WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);

            using var reader = cmd.ExecuteReader();
            if (reader.Read()) {
                var entry = new CacheEntry {
                    Key = reader.GetString(0),
                    Value = reader.GetString(1),
                    Args = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Env = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ExecMode = reader.IsDBNull(4) ? -1 : reader.GetInt32(4),
                    Prefix = reader.IsDBNull(5) ? null : reader.GetString(5),
                    PrefixCondition = reader.IsDBNull(6) ? null : reader.GetString(6),
                    CharsetConv = reader.IsDBNull(7) ? null : reader.GetString(7),
                    CharsetConvCondition = reader.IsDBNull(8) ? null : reader.GetString(8)
                };

                // 检查缓存的文件是否仍然存在
                if (!File.Exists(entry.Value)) {
                    return null;
                }

                return entry;
            }
        } catch (SqliteException) {
            // 数据库可能损坏，尝试删除并重建
            try {
                File.Delete(dbPath);
            } catch {
                // 忽略删除错误
            }
        } catch {
            // 忽略其他缓存读取错误
        }
        return null;
    }

    /// <summary>更新缓存数据库，添加或更新指定键的值</summary>
    /// <param name="key">缓存键（别名命令字符串）</param>
    /// <param name="value">要缓存的值（解析后的完整路径）</param>
    /// <param name="args">解析后的参数（通配符已展开）</param>
    /// <param name="envDelimited">环境变量（\0 分隔格式）</param>
    /// <param name="execMode">exec 模式</param>
    /// <param name="prefix">输出前缀格式</param>
    /// <param name="prefixCond">前缀条件正则表达式</param>
    /// <param name="charsetConv">字符集转换配置</param>
    /// <param name="charsetConvCond">字符集转换条件正则表达式</param>
    public static void UpdateCache(string key, string value, string? args, string? envDelimited, int execMode = -1, string? prefix = null, string? prefixCond = null, string? charsetConv = null, string? charsetConvCond = null) {
        try {
            using var conn = new SqliteConnection(GetConnectionString());
            conn.Open();
            InitializeDatabase(conn);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO cache (key, value, args, env, exec_mode, prefix, prefix_cond, charset_conv, charset_conv_cond, update_at)
                VALUES (@key, @value, @args, @env, @execMode, @prefix, @prefixCond, @charsetConv, @charsetConvCond, @updateAt)";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.Parameters.AddWithValue("@args", (object?)args ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@env", (object?)envDelimited ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@execMode", execMode);
            cmd.Parameters.AddWithValue("@prefix", (object?)prefix ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@prefixCond", (object?)prefixCond ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@charsetConv", (object?)charsetConv ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@charsetConvCond", (object?)charsetConvCond ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@updateAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        } catch {
            // 忽略缓存写入错误
        }
    }

    /// <summary>获取所有缓存条目（用于 -p 参数）</summary>
    /// <returns>所有缓存条目列表</returns>
    public static List<CacheEntry> GetAllEntries() {
        var entries = new List<CacheEntry>();
        string dbPath = GetDatabasePath();
        if (!File.Exists(dbPath)) return entries;

        try {
            using var conn = new SqliteConnection(GetConnectionString());
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value, args, update_at FROM cache ORDER BY key";

            using var reader = cmd.ExecuteReader();
            while (reader.Read()) {
                entries.Add(new CacheEntry {
                    Key = reader.GetString(0),
                    Value = reader.GetString(1),
                    Args = reader.IsDBNull(2) ? null : reader.GetString(2),
                    UpdateAt = reader.GetInt64(3)
                });
            }
        } catch {
            // 忽略错误
        }
        return entries;
    }

    /// <summary>删除缓存数据库文件（用于 -r 参数）</summary>
    public static void DeleteDatabase() {
        try {
            string dbPath = GetDatabasePath();
            if (File.Exists(dbPath)) {
                File.Delete(dbPath);
            }
        } catch {
            // 忽略删除错误
        }
    }
}

#endregion

#region Path Resolver

/// <summary>路径解析器</summary>
/// <remarks>
/// 将别名命令中的可执行文件路径解析为完整的绝对路径。
/// 支持多种路径格式：
/// - 绝对路径：C:\Program Files\app.exe
/// - 相对路径：.\app.exe 或 app.exe
/// - 通配符路径：C:\Tool*\app*.exe（匹配最新的文件）
/// - PATH 环境变量：node.exe（从系统 PATH 查找）
/// - 环境变量展开：%APPDATA%\app.exe
/// </remarks>
internal static class PathResolver {
    /// <summary>从命令字符串中提取可执行文件路径</summary>
    /// <param name="command">完整的命令字符串（可能包含路径和参数）</param>
    /// <returns>可执行文件的路径部分</returns>
    /// <remarks>
    /// 示例：
    /// - "C:\Program Files\app.exe --arg" -> "C:\Program Files\app.exe"
    /// - "app.exe arg1 arg2" -> "app.exe"
    /// - "./app.exe" -> "./app.exe"
    /// </remarks>
    public static string ExtractExecutablePath(string command) {
        command = command.Trim();

        // 处理带引号的路径
        if (command.StartsWith('"')) {
            int endQuote = command.IndexOf('"', 1);
            if (endQuote > 0) {
                return command[1..endQuote];
            }
        }

        // 提取第一个空格之前的部分
        int spaceIndex = command.IndexOf(' ');
        return spaceIndex > 0 ? command[..spaceIndex] : command;
    }

    /// <summary>提取命令字符串中的参数部分</summary>
    /// <param name="command">完整的命令字符串</param>
    /// <returns>参数部分（不含可执行文件路径）</returns>
    /// <remarks>
    /// 示例：
    /// - "C:\app.exe --arg1 --arg2" -> "--arg1 --arg2"
    /// - "app.exe" -> ""
    /// - ""C:\Program Files\app.exe" --arg" -> "--arg"
    /// </remarks>
    public static string ExtractArguments(string command) {
        command = command.Trim();

        if (command.StartsWith('"')) {
            int endQuote = command.IndexOf('"', 1);
            if (endQuote > 0 && endQuote + 1 < command.Length) {
                return command[(endQuote + 1)..].Trim();
            }
            return "";
        }

        int spaceIndex = command.IndexOf(' ');
        return spaceIndex > 0 ? command[(spaceIndex + 1)..].Trim() : "";
    }

    /// <summary>判断路径是否为简单文件名（无目录结构）</summary>
    private static bool IsSimpleFileName(string path) {
        return !path.Contains('\\') && !path.Contains('/') && !path.StartsWith('.');
    }

    /// <summary>从 PATH 环境变量中搜索可执行文件</summary>
    /// <param name="fileName">文件名（如 git.exe 或 git）</param>
    /// <returns>找到的完整路径，未找到返回 null</returns>
    public static string? SearchInPath(string fileName) {
        // 获取 PATH 环境变量
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        // 确定要尝试的扩展名列表
        var extensions = new List<string>();
        if (Path.HasExtension(fileName)) {
            // 已有扩展名，直接使用
            extensions.Add("");
        } else {
            // 无扩展名，使用 PATHEXT
            string? pathExt = Environment.GetEnvironmentVariable("PATHEXT");
            if (!string.IsNullOrEmpty(pathExt)) {
                extensions.AddRange(pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries));
            } else {
                extensions.AddRange(new[] { ".exe", ".cmd", ".bat", ".com" });
            }
        }

        foreach (string dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
            foreach (string ext in extensions) {
                string fullPath = Path.Combine(dir.Trim(), fileName + ext);
                if (File.Exists(fullPath)) {
                    return fullPath;
                }
            }
        }
        return null;
    }

    /// <summary>解析参数中的通配符路径</summary>
    /// <param name="args">原始参数字符串</param>
    /// <param name="exclArgs">排除通配符处理的参数索引集合（1-based）</param>
    /// <returns>解析后的参数字符串</returns>
    public static string? ResolveArgumentWildcards(string args, HashSet<int>? exclArgs) {
        if (string.IsNullOrEmpty(args)) return null;

        // 解析参数为列表
        var argList = ParseArguments(args);
        if (argList.Count == 0) return null;

        for (int i = 0; i < argList.Count; i++) {
            int argIndex = i + 1; // 1-based index

            // 跳过排除的参数
            if (exclArgs != null && exclArgs.Contains(argIndex)) {
                continue;
            }

            string arg = argList[i];
            // 展开环境变量
            string expanded = Environment.ExpandEnvironmentVariables(arg);

            // 检查是否包含通配符且看起来像路径
            if ((expanded.Contains('*') || expanded.Contains('?')) &&
                (expanded.Contains('\\') || expanded.Contains('/'))) {
                string? resolved = ResolveWildcardPath(expanded);
                if (resolved != null) {
                    argList[i] = resolved;
                }
            } else if (expanded != arg) {
                argList[i] = expanded;
            }
        }

        // 重新构建参数字符串
        var sb = new StringBuilder();
        foreach (string arg in argList) {
            if (sb.Length > 0) sb.Append(' ');
            if (arg.Contains(' ') && !arg.StartsWith('"')) {
                sb.Append('"').Append(arg).Append('"');
            } else {
                sb.Append(arg);
            }
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>解析参数字符串为列表（处理引号）</summary>
    private static List<string> ParseArguments(string args) {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < args.Length; i++) {
            char c = args[i];
            if (c == '"') {
                inQuotes = !inQuotes;
                current.Append(c);
            } else if (c == ' ' && !inQuotes) {
                if (current.Length > 0) {
                    result.Add(StripQuotes(current.ToString()));
                    current.Clear();
                }
            } else {
                current.Append(c);
            }
        }
        if (current.Length > 0) {
            result.Add(StripQuotes(current.ToString()));
        }
        return result;
    }

    /// <summary>去除字符串两端的引号</summary>
    private static string StripQuotes(string value) {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') {
            return value[1..^1];
        }
        return value;
    }

    /// <summary>解析通配符路径，返回匹配的最新文件</summary>
    /// <param name="pattern">通配符模式（如 C:\Tools\7zip-*\7z.exe）</param>
    /// <returns>匹配的最新文件的完整路径，无匹配返回 null</returns>
    /// <remarks>
    /// 通配符规则：
    /// - *：匹配任意字符（0或多个）
    /// - ?：匹配单个字符
    /// 支持路径中任意位置的通配符，如：
    /// - "C:\Tools\7zip-*\7z.exe" 匹配 "C:\Tools\7zip-19.00\7z.exe"
    /// - "C:\Tools\node*\node.exe" 匹配 "C:\Tools\node-v18.0.0\node.exe"
    /// </remarks>
    public static string? ResolveWildcardPath(string pattern) {
        try {
            // 分割路径为各个部分
            string[] parts = pattern.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;

            // 找到根路径（驱动器号或 UNC 路径）
            string root;
            int startIndex;
            if (pattern.Length >= 2 && pattern[1] == ':') {
                // 驱动器路径 (C:\...)
                root = parts[0] + "\\";
                startIndex = 1;
            } else if (pattern.StartsWith("\\\\")) {
                // UNC 路径 (\\server\share\...)
                root = "\\\\" + parts[0] + "\\" + parts[1] + "\\";
                startIndex = 2;
            } else {
                // 相对路径
                root = Directory.GetCurrentDirectory();
                startIndex = 0;
            }

            // 递归解析每个路径部分
            return ResolveWildcardRecursive(root, parts, startIndex);
        } catch {
            return null;
        }
    }

    /// <summary>递归解析通配符路径</summary>
    private static string? ResolveWildcardRecursive(string currentPath, string[] parts, int index) {
        if (index >= parts.Length) {
            // 已到达路径末尾，检查文件是否存在
            return File.Exists(currentPath) ? currentPath : null;
        }

        string part = parts[index];
        bool isLastPart = index == parts.Length - 1;

        if (part.Contains('*') || part.Contains('?')) {
            // 当前部分包含通配符
            if (isLastPart) {
                // 最后一部分是文件名，搜索文件
                if (!Directory.Exists(currentPath)) return null;
                var matches = Directory.GetFiles(currentPath, part)
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();
                return matches.FirstOrDefault()?.FullName;
            } else {
                // 中间部分是目录，搜索目录
                if (!Directory.Exists(currentPath)) return null;
                var matchedDirs = Directory.GetDirectories(currentPath, part)
                    .Select(d => new DirectoryInfo(d))
                    .OrderByDescending(d => d.LastWriteTime)
                    .ToList();

                // 尝试每个匹配的目录（优先最新的）
                foreach (var dir in matchedDirs) {
                    var result = ResolveWildcardRecursive(dir.FullName, parts, index + 1);
                    if (result != null) return result;
                }
                return null;
            }
        } else {
            // 当前部分没有通配符，直接拼接
            string nextPath = Path.Combine(currentPath, part);
            if (isLastPart) {
                return File.Exists(nextPath) ? nextPath : null;
            } else {
                return ResolveWildcardRecursive(nextPath, parts, index + 1);
            }
        }
    }
}

#endregion

#region Command Builder

/// <summary>命令行构建器</summary>
/// <remarks>
/// 将可执行文件路径、别名参数和用户传入的参数组合成完整的命令行字符串。
/// 格式："可执行路径" [别名参数] [用户参数...]
/// </remarks>
internal static class CommandBuilder {
    /// <summary>构建完整命令行字符串</summary>
    /// <param name="targetPath">解析后的可执行文件完整路径</param>
    /// <param name="cachedArgs">缓存的参数（已解析通配符）</param>
    /// <param name="args">用户传入的命令行参数</param>
    /// <returns>完整的命令行字符串</returns>
    /// <remarks>
    /// 构建规则：
    /// 1. 可执行文件路径始终用双引号包裹
    /// 2. 缓存的参数直接追加（已解析通配符）
    /// 3. 用户参数中的空格需要用双引号包裹
    ///
    /// 示例：
    /// targetPath: C:\Program Files\app.exe
    /// cachedArgs: --config D:\resolved\path\config.ini
    /// args: ["file.txt", "output file.txt"]
    /// 结果: "C:\Program Files\app.exe" --config D:\resolved\path\config.ini file.txt "output file.txt"
    /// </remarks>
    public static string Build(string targetPath, string? cachedArgs, string[] args) {
        var sb = new StringBuilder();
        sb.Append('"').Append(targetPath).Append('"');

        // 添加缓存的参数（已解析通配符）
        if (!string.IsNullOrEmpty(cachedArgs)) {
            sb.Append(' ').Append(cachedArgs);
        }

        // 添加传入的参数
        foreach (string arg in args) {
            sb.Append(' ');
            if (arg.Contains(' ') && !arg.StartsWith('"')) {
                sb.Append('"').Append(arg).Append('"');
            } else {
                sb.Append(arg);
            }
        }

        return sb.ToString();
    }
}

#endregion

#region Prefix Formatter

/// <summary>前缀格式化器 - 支持类似 Linux date 命令的格式</summary>
internal static class PrefixFormatter {
    /// <summary>格式化前缀字符串</summary>
    /// <param name="format">格式字符串，支持 %Y %m %d %H %M %S %F %T 等</param>
    /// <returns>格式化后的字符串</returns>
    public static string Format(string format) {
        var now = DateTime.Now;
        var sb = new StringBuilder(format.Length * 2);

        for (int i = 0; i < format.Length; i++) {
            if (format[i] == '%' && i + 1 < format.Length) {
                char spec = format[++i];
                sb.Append(spec switch {
                    'Y' => now.Year.ToString("D4"),           // 年份 4位
                    'y' => (now.Year % 100).ToString("D2"),   // 年份 2位
                    'm' => now.Month.ToString("D2"),          // 月份
                    'd' => now.Day.ToString("D2"),            // 日
                    'H' => now.Hour.ToString("D2"),           // 时 24小时制
                    'I' => (now.Hour % 12 == 0 ? 12 : now.Hour % 12).ToString("D2"), // 时 12小时制
                    'M' => now.Minute.ToString("D2"),         // 分
                    'S' => now.Second.ToString("D2"),         // 秒
                    'F' => now.ToString("yyyy-MM-dd"),        // 完整日期
                    'T' => now.ToString("HH:mm:ss"),          // 完整时间
                    'N' => now.ToString("fff"),               // 毫秒
                    'n' => "\n",                              // 换行
                    't' => "\t",                              // 制表符
                    '%' => "%",                               // 百分号
                    _ => "%" + spec                           // 未知格式保留原样
                });
            } else {
                sb.Append(format[i]);
            }
        }
        return sb.ToString();
    }
}

#endregion

#region Process Executor

/// <summary>进程执行器</summary>
/// <remarks>
/// 使用 Windows API 创建子进程并执行命令。
/// 特性：
/// - 继承父进程的标准输入/输出/错误流
/// - 支持普通模式和 exec 模式
/// - 返回子进程的退出代码
/// </remarks>
internal static class ProcessExecutor {
    /// <summary>执行目标进程</summary>
    /// <param name="commandLine">完整的命令行字符串</param>
    /// <param name="execMode">exec 模式设置（-1=禁用，0=立即退出，>0=延迟秒数后退出）</param>
    /// <returns>
    /// 普通模式：返回子进程的退出代码
    /// exec 模式：总是返回 0（父进程退出）
    /// 失败：返回 Windows 错误代码
    /// </returns>
    /// <remarks>
    /// 执行流程：
    /// 1. 获取当前进程的标准句柄
    /// 2. 配置启动信息（继承标准句柄）
    /// 3. 调用 CreateProcess 创建子进程
    /// 4. 根据 execMode 决定行为：
    ///    - -1（禁用）：等待子进程结束，返回退出代码
    ///    - 0（立即）：立即关闭句柄，父进程退出
    ///    - >0（延迟）：等待指定秒数后退出
    /// 5. 清理进程和线程句柄
    /// </remarks>
    public static int Execute(string commandLine, int execMode) {
        // 使用隐式句柄继承 (bInheritHandles=true)，不显式指定 STANDARD_RIGHTS
        // 这通常能更好地处理控制台模式继承
        var si = new NativeApi.StartupInfo {
            cb = Marshal.SizeOf<NativeApi.StartupInfo>()
        };

        if (!NativeApi.CreateProcess(
            null!, commandLine,
            IntPtr.Zero, IntPtr.Zero,
            true, 0,
            IntPtr.Zero, null!,
            ref si, out var pi)) {
            int error = Marshal.GetLastWin32Error();
            Console.Error.WriteLine($"alias failed to create process. Error code: {error}");
            return error;
        }

        // exec 模式处理
        if (execMode >= 0) {
            if (execMode > 0) {
                // 延迟指定秒数后退出
                NativeApi.WaitForSingleObject(pi.hProcess, (uint)(execMode * 1000));
            }
            // 立即退出或延迟后退出
            NativeApi.CloseHandle(pi.hProcess);
            NativeApi.CloseHandle(pi.hThread);
            return 0;
        }

        // 普通模式：等待子进程结束
        NativeApi.WaitForSingleObject(pi.hProcess, NativeApi.Infinite);
        NativeApi.GetExitCodeProcess(pi.hProcess, out uint exitCode);

        NativeApi.CloseHandle(pi.hProcess);
        NativeApi.CloseHandle(pi.hThread);

        return (int)exitCode;
    }

    /// <summary>执行命令并为每行输出添加前缀</summary>
    /// <summary>解析字符集转换配置</summary>
    /// <param name="charsetConv">字符集转换配置（如 UTF-8,GBK）</param>
    /// <param name="fromEncoding">输出：源编码</param>
    /// <param name="toEncoding">输出：目标编码</param>
    /// <returns>解析是否成功</returns>
    private static bool ParseCharsetConv(string? charsetConv, out Encoding fromEncoding, out Encoding toEncoding) {
        if (string.IsNullOrEmpty(charsetConv)) {
            fromEncoding = toEncoding = Encoding.GetEncoding(Console.OutputEncoding.CodePage);
            return true;
        }

        var parts = charsetConv.Split(',');
        if (parts.Length != 2) {
            Console.Error.WriteLine($"Invalid CHARSET_CONV format: {charsetConv}");
            fromEncoding = toEncoding = Encoding.GetEncoding(Console.OutputEncoding.CodePage);
            return false;
        }

        try {
            fromEncoding = Encoding.GetEncoding(parts[0].Trim());
            toEncoding = Encoding.GetEncoding(parts[1].Trim());
            return true;
        } catch {
            Console.Error.WriteLine($"Invalid encoding in CHARSET_CONV: {charsetConv}");
            fromEncoding = toEncoding = Encoding.GetEncoding(Console.OutputEncoding.CodePage);
            return false;
        }
    }

    /// <summary>创建管道并启动子进程</summary>
    /// <returns>成功返回 (读管道句柄, 进程信息)，失败返回 null</returns>
    private static (IntPtr hReadPipe, NativeApi.ProcessInformation pi)? CreatePipedProcess(string commandLine) {
        var sa = new NativeApi.SecurityAttributes {
            nLength = Marshal.SizeOf<NativeApi.SecurityAttributes>(),
            bInheritHandle = true
        };

        if (!NativeApi.CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref sa, 0)) {
            return null;
        }

        NativeApi.SetHandleInformation(hReadPipe, NativeApi.HANDLE_FLAG_INHERIT, 0);

        var si = new NativeApi.StartupInfo {
            cb = Marshal.SizeOf<NativeApi.StartupInfo>(),
            dwFlags = NativeApi.STARTF_USESTDHANDLES,
            hStdOutput = hWritePipe,
            hStdError = hWritePipe
        };

        if (!NativeApi.CreateProcess(null!, commandLine, IntPtr.Zero, IntPtr.Zero, true, 0, IntPtr.Zero, null!, ref si, out var pi)) {
            NativeApi.CloseHandle(hReadPipe);
            NativeApi.CloseHandle(hWritePipe);
            Console.Error.WriteLine($"alias failed to create process. Error code: {Marshal.GetLastWin32Error()}");
            return null;
        }

        NativeApi.CloseHandle(hWritePipe);
        return (hReadPipe, pi);
    }

    /// <summary>等待进程结束并清理资源</summary>
    private static int WaitAndCleanup(IntPtr hReadPipe, NativeApi.ProcessInformation pi) {
        NativeApi.CloseHandle(hReadPipe);
        NativeApi.WaitForSingleObject(pi.hProcess, NativeApi.Infinite);
        NativeApi.GetExitCodeProcess(pi.hProcess, out uint exitCode);
        NativeApi.CloseHandle(pi.hProcess);
        NativeApi.CloseHandle(pi.hThread);
        return (int)exitCode;
    }

    /// <summary>执行命令并处理输出（支持前缀和字符集转换）</summary>
    /// <param name="commandLine">完整的命令行字符串</param>
    /// <param name="prefix">可选的前缀格式（支持 %F %T 等）</param>
    /// <param name="charsetConv">可选的字符集转换配置（如 UTF-8,GBK）</param>
    /// <returns>进程退出代码</returns>
    public static int ExecuteWithOutputProcessing(string commandLine, string? prefix, string? charsetConv) {
        ParseCharsetConv(charsetConv, out var fromEncoding, out var toEncoding);

        var result = CreatePipedProcess(commandLine);
        if (result == null) return Marshal.GetLastWin32Error();

        var (hReadPipe, pi) = result.Value;
        var buffer = new byte[4096];
        bool needConvert = fromEncoding != toEncoding;

        if (prefix != null) {
            // 有前缀：逐行处理
            bool isDynamic = prefix.Contains('%');
            var lineBuffer = new StringBuilder();

            while (NativeApi.ReadFile(hReadPipe, buffer, (uint)buffer.Length, out uint bytesRead, IntPtr.Zero) && bytesRead > 0) {
                string chunk = fromEncoding.GetString(buffer, 0, (int)bytesRead);
                foreach (char c in chunk) {
                    if (c == '\n') {
                        string line = $"{(isDynamic ? PrefixFormatter.Format(prefix) : prefix)}{lineBuffer}\r\n";
                        if (needConvert) {
                            var bytes = toEncoding.GetBytes(line);
                            Console.OpenStandardOutput().Write(bytes, 0, bytes.Length);
                        } else {
                            Console.Write(line);
                        }
                        lineBuffer.Clear();
                    } else if (c != '\r') {
                        lineBuffer.Append(c);
                    }
                }
            }

            if (lineBuffer.Length > 0) {
                string line = $"{(isDynamic ? PrefixFormatter.Format(prefix) : prefix)}{lineBuffer}\r\n";
                if (needConvert) {
                    var bytes = toEncoding.GetBytes(line);
                    Console.OpenStandardOutput().Write(bytes, 0, bytes.Length);
                } else {
                    Console.Write(line);
                }
            }
        } else {
            // 无前缀：直接转换输出
            var outputStream = Console.OpenStandardOutput();
            while (NativeApi.ReadFile(hReadPipe, buffer, (uint)buffer.Length, out uint bytesRead, IntPtr.Zero) && bytesRead > 0) {
                string text = fromEncoding.GetString(buffer, 0, (int)bytesRead);
                var converted = toEncoding.GetBytes(text);
                outputStream.Write(converted, 0, converted.Length);
            }
            outputStream.Flush();
        }

        return WaitAndCleanup(hReadPipe, pi);
    }

    /// <summary>执行命令并添加前缀（兼容旧接口）</summary>
    public static int ExecuteWithPrefix(string commandLine, string prefixFormat, string? charsetConv = null) {
        return ExecuteWithOutputProcessing(commandLine, prefixFormat, charsetConv);
    }

    /// <summary>执行命令并进行字符集转换（兼容旧接口）</summary>
    public static int ExecuteWithCharsetConv(string commandLine, string charsetConv) {
        return ExecuteWithOutputProcessing(commandLine, null, charsetConv);
    }
}

#endregion

#region Help Display

/// <summary>帮助信息显示</summary>
/// <remarks>
/// 提供程序的使用说明、配置示例和参数说明。
/// 当用户调用 alias.exe --help 时显示。
/// </remarks>
internal static class HelpDisplay {
    /// <summary>显示帮助信息到控制台</summary>
    public static void Show() {
        Console.WriteLine(@"
alias.exe - Windows command line passthrough utility

USAGE:
    alias [[OPTIONS]] [ARGS]...
    alias <name> [ARGS]...
    alias <name>=<path> [ARGS]...

OPTIONS:
    -h, --help, /?  Display this help message
    -p  [-t]        Print cached results for aliases and paths
    -r              Refresh cache
    -e              Edit the %USERPROFILE%\.alias file (opens with notepad.exe
                    by default). You can specify the editor by setting the
                    ALIAS_EDITOR variable.

DESCRIPTION:
    A passthrough utility that reads configuration from %USERPROFILE%\.alias
    to alias commands and set environment variables.

    The program matches its own filename (without extension) to alias definitions
    in the configuration file and executes the corresponding command with all
    arguments passed through.

CONFIGURATION:
    Use %USERPROFILE%\.alias as the configuration file, employing the Linux alias
    format with support for wildcard searches.
        e.g.
            PREFIX=""%F %T %N ""
            alias bfg='""C:\Program Files\java*\bin\java.exe"" -jar D:\bfg-*\bfg-*.jar'
            alias git='C:\Git*\bin\git.exe'

    The configuration file supports the following keywords to define alias behavior:

        PREFIX=['/<regex>/ && ]""<text>""[']
            Add prefixes during output, supporting date format specifiers. Regular
            expressions can be added to determine whether prefixes are generated.
            e.g.
                PREFIX=""# %F %T %N ""
                PREFIX='/-t/ && ""@ %F %T ""'

        CHARSET_CONV=['/<regex>/ && ]""<from>,<to>""[']
            Specify character set conversion for command output.
            e.g.
                CHARSET_CONV=""UTF-8,GBK""
                CHARSET_CONV='/-s/ && ""UTF-8,GBK""'

        EXEC=<bool|seconds>
            Delay process replacement by the specified number of seconds.

        EXCL_ARG=<indexes>
            Disable wildcard parsing for arguments at the specified comma-separated
            indexes.

        ");
    }

    /// <summary>打印缓存内容并执行 doskey.exe /macros</summary>
    /// <param name="showTime">是否显示更新时间</param>
    public static void PrintCache(bool showTime = false) {
        // 确保配置文件存在
        ConfigParser.EnsureConfigExists(ConfigParser.GetConfigPath());

        var entries = CacheManager.GetAllEntries();
        if (entries.Count == 0) {
            Console.WriteLine("Cache is empty.");
        } else {
            foreach (var entry in entries) {
                // 如果路径包含空格，需要用引号包裹
                string valuePart = entry.Value.Contains(' ') ? $"\"{entry.Value}\"" : entry.Value;
                string line;
                if (string.IsNullOrEmpty(entry.Args)) {
                    line = $"alias {entry.Key}='{valuePart}'";
                } else {
                    line = $"alias {entry.Key}='{valuePart} {entry.Args}'";
                }

                if (showTime) {
                    var updateTime = DateTimeOffset.FromUnixTimeSeconds(entry.UpdateAt).LocalDateTime;
                    line += $"\t# {updateTime:yyyy-MM-dd HH:mm:ss}";
                }

                Console.WriteLine(line);
            }
        }
        Console.WriteLine();

        // 执行 doskey.exe /macros 并添加前缀
        ProcessExecutor.ExecuteWithPrefix("doskey.exe /macros", "doskey ");
    }

    /// <summary>编辑配置文件</summary>
    public static void EditConfig() {
        string configPath = ConfigParser.GetConfigPath();
        ConfigParser.EnsureConfigExists(configPath);

        // 获取编辑器：优先使用 ALIAS_EDITOR 环境变量，否则使用 notepad.exe
        string editor = Environment.GetEnvironmentVariable("ALIAS_EDITOR") ?? "notepad.exe";
        ProcessExecutor.Execute($"{editor} \"{configPath}\"", -1);
    }

    /// <summary>刷新缓存（删除数据库并重新生成全量缓存，同步软链接）</summary>
    public static void RefreshCache() {
        // 删除旧数据库
        CacheManager.DeleteDatabase();

        // 配置文件路径
        string configPath = ConfigParser.GetConfigPath();

        // 获取 alias.exe 所在目录
        string? exePath = Environment.ProcessPath;
        string? exeDir = exePath != null ? Path.GetDirectoryName(exePath) : null;
        string? exeName = exePath != null ? Path.GetFileName(exePath) : null;

        if (!ConfigParser.EnsureConfigExists(configPath)) {
            Console.WriteLine("Cache cleared.");
            // 清理无效软链接
            if (exeDir != null && exeName != null) {
                CleanupSymlinks(exeDir, exeName, new HashSet<string>());
            }
            return;
        }

        // 获取所有别名
        var aliases = ConfigParser.GetAllAliases(configPath);
        if (aliases.Count == 0) {
            Console.WriteLine("Cache cleared. No aliases found in config.");
            // 清理无效软链接
            if (exeDir != null && exeName != null) {
                CleanupSymlinks(exeDir, exeName, new HashSet<string>());
            }
            return;
        }

        Console.WriteLine($"Rebuilding cache for {aliases.Count} aliases...");
        int success = 0;
        int failed = 0;
        var validKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, command) in aliases) {
            // 解析配置
            var config = ConfigParser.Parse(configPath, name);
            string keyLower = name.ToLowerInvariant();

            // 解析目标路径
            string targetPath = PathResolver.ExtractExecutablePath(command);
            targetPath = Environment.ExpandEnvironmentVariables(targetPath);

            string? resolved = null;
            bool isSimpleName = !targetPath.Contains('\\') && !targetPath.Contains('/') && !targetPath.StartsWith('.');
            bool isAbsolutePath = Path.IsPathRooted(targetPath);

            if (isSimpleName) {
                // 简单文件名，仅从 PATH 搜索
                resolved = PathResolver.SearchInPath(targetPath);
            } else if (isAbsolutePath) {
                // 绝对路径（支持通配符）
                if (targetPath.Contains('*') || targetPath.Contains('?')) {
                    resolved = PathResolver.ResolveWildcardPath(targetPath);
                } else if (File.Exists(targetPath)) {
                    resolved = targetPath;
                }
            }

            if (resolved != null) {
                // 解析参数中的通配符
                string rawArgs = PathResolver.ExtractArguments(command);
                string? resolvedArgs = PathResolver.ResolveArgumentWildcards(rawArgs, config?.ExclArgs);

                // 更新缓存
                string? envDelimited = config != null ? CacheManager.SerializeEnv(config.EnvironmentVars) : null;
                CacheManager.UpdateCache(keyLower, resolved, resolvedArgs, envDelimited, config?.ExecMode ?? -1, config?.Prefix, config?.PrefixCondition, config?.CharsetConv, config?.CharsetConvCondition);

                if (string.IsNullOrEmpty(resolvedArgs)) {
                    Console.WriteLine($"  {name} -> {resolved}");
                } else {
                    Console.WriteLine($"  {name} -> {resolved} {resolvedArgs}");
                }
                success++;
                validKeys.Add(keyLower);

                // 创建软链接（如果不存在）
                if (exeDir != null && exeName != null) {
                    CreateSymlinkIfNeeded(exeDir, exeName, keyLower);
                }
            } else {
                Console.WriteLine($"  {name} -> [not found]");
                failed++;
            }
        }

        // 清理无效软链接
        if (exeDir != null && exeName != null) {
            CleanupSymlinks(exeDir, exeName, validKeys);
        }

        Console.WriteLine($"Cache rebuilt: {success} cached, {failed} not found.");
    }

    /// <summary>创建软链接（如果不存在）</summary>
    /// <param name="exeDir">alias.exe 所在目录</param>
    /// <param name="exeName">alias.exe 文件名</param>
    /// <param name="aliasKey">别名 key（小写）</param>
    public static void CreateSymlinkIfNeeded(string exeDir, string exeName, string aliasKey) {
        // 跳过 alias 本身
        if (aliasKey.Equals("alias", StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        string linkPath = Path.Combine(exeDir, aliasKey + ".exe");

        // 如果文件已存在，跳过
        if (File.Exists(linkPath)) {
            return;
        }

        // 创建软链接（使用相对路径）
        if (NativeApi.CreateSymbolicLink(linkPath, exeName, 0)) {
            Console.WriteLine($"      [symlink] {aliasKey}.exe -> {exeName}");
        } else {
            int error = Marshal.GetLastWin32Error();
            // 1314 = ERROR_PRIVILEGE_NOT_HELD (需要管理员权限或开发者模式)
            if (error == 1314) {
                Console.Error.WriteLine($"      [symlink] {aliasKey}.exe failed: requires admin or Developer Mode");
            } else {
                Console.Error.WriteLine($"      [symlink] {aliasKey}.exe failed: error {error}");
            }
        }
    }

    /// <summary>清理无效的软链接</summary>
    /// <param name="exeDir">alias.exe 所在目录</param>
    /// <param name="exeName">alias.exe 文件名</param>
    /// <param name="validKeys">有效的别名 key 集合</param>
    private static void CleanupSymlinks(string exeDir, string exeName, HashSet<string> validKeys) {
        try {
            foreach (string filePath in Directory.GetFiles(exeDir, "*.exe")) {
                string fileName = Path.GetFileName(filePath);

                // 跳过 alias.exe 本身
                if (fileName.Equals(exeName, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                // 检查是否是符号链接
                uint attrs = NativeApi.GetFileAttributes(filePath);
                if (attrs == NativeApi.INVALID_FILE_ATTRIBUTES) {
                    continue;
                }
                if ((attrs & NativeApi.FILE_ATTRIBUTE_REPARSE_POINT) == 0) {
                    continue; // 不是符号链接，跳过
                }

                // 检查符号链接是否指向 alias.exe
                try {
                    var linkTarget = new FileInfo(filePath).LinkTarget;
                    if (linkTarget == null) {
                        continue;
                    }

                    // 检查目标是否是 alias.exe（相对或绝对路径）
                    string targetName = Path.GetFileName(linkTarget);
                    if (!targetName.Equals(exeName, StringComparison.OrdinalIgnoreCase)) {
                        continue; // 不是指向 alias.exe，跳过
                    }

                    // 检查 key 是否有效
                    string key = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
                    if (!validKeys.Contains(key)) {
                        // 删除无效软链接
                        File.Delete(filePath);
                        Console.WriteLine($"      [removed] {fileName}");
                    }
                } catch {
                    // 忽略读取链接目标失败
                }
            }
        } catch {
            // 忽略目录扫描错误
        }
    }
}

#endregion

#region Main Program

/// <summary>程序入口</summary>
/// <remarks>
/// 主程序类，负责协调整个命令行工具的执行流程。
///
/// 使用方式：
/// - 在 %USERPROFILE%\.alias 中定义对应的 alias code=''
/// - 运行重命名后的程序，它会执行对应的命令
/// </remarks>
internal static class Program {
    /// <summary>程序主入口点</summary>
    /// <param name="args">命令行参数</param>
    /// <returns>退出代码（0=成功，非0=失败）</returns>
    private static int Main(string[] args) {
        // 注册代码页编码（NativeAOT 需要）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // 初始化 SQLite（NativeAOT 静态链接需要手动设置 provider）
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());

        return Run(args);
    }

    /// <summary>主逻辑</summary>
    private static int Run(string[] args) {
        // 获取程序文件名
        string exeName = GetExecutableName();

        // alias.exe 特殊命令处理
        if (exeName.Equals("alias", StringComparison.OrdinalIgnoreCase)) {
            // 帮助检查（优先处理，不需要配置文件）
            if (args.Length > 0 && IsHelpArg(args[0])) {
                HelpDisplay.Show();
                return 0;
            }
            // 无参数或 -p: 打印缓存，-t 显示时间
            if (args.Length == 0 || (args.Length > 0 && args[0] == "-p")) {
                bool showTime = args.Contains("-t");
                HelpDisplay.PrintCache(showTime);
                return 0;
            }
            // -r: 刷新缓存
            if (args.Length > 0 && args[0] == "-r") {
                HelpDisplay.RefreshCache();
                return 0;
            }
            // -e: 编辑配置文件
            if (args.Length > 0 && args[0] == "-e") {
                HelpDisplay.EditConfig();
                return 0;
            }
            // alias <名称>=<命令>: 透传给 doskey.exe，添加 $*
            if (args.Length > 0 && args[0].Contains('=')) {
                string doskeyArg = args[0];
                // 如果命令部分不以 $* 结尾，添加 $*
                if (!doskeyArg.EndsWith("$*")) {
                    doskeyArg += " $*";
                }
                return ProcessExecutor.Execute($"doskey.exe {doskeyArg}", -1);
            }
            // alias <名称> [参数...]: 调用指定别名
            if (args.Length > 0 && !args[0].StartsWith("-")) {
                exeName = args[0];
                args = args.Length > 1 ? args[1..] : Array.Empty<string>();
            }
        }

        // 配置文件路径
        string configPath = ConfigParser.GetConfigPath();

        if (!ConfigParser.EnsureConfigExists(configPath)) {
            return 1;
        }

        string cacheKey = exeName.ToLowerInvariant();

        // 快速路径：缓存有效时直接使用缓存
        if (CacheManager.IsCacheValid(configPath)) {
            var cached = CacheManager.GetCachedEntry(cacheKey);
            if (cached != null) {
                // 设置环境变量
                foreach (var (key, value) in CacheManager.DeserializeEnv(cached.Env)) {
                    NativeApi.SetEnvironmentVariable(key, Environment.ExpandEnvironmentVariables(value));
                }

                // 构建并执行命令
                string cmdLine = CommandBuilder.Build(cached.Value, cached.Args, args);

                // 检查前缀和字符集转换条件
                string? cachedEffectivePrefix = GetEffectiveValue(cached.Prefix, cached.PrefixCondition, args);
                string? cachedEffectiveCharsetConv = GetEffectiveValue(cached.CharsetConv, cached.CharsetConvCondition, args);
                if (!string.IsNullOrEmpty(cachedEffectivePrefix)) {
                    return ProcessExecutor.ExecuteWithPrefix(cmdLine, cachedEffectivePrefix, cachedEffectiveCharsetConv);
                }
                if (!string.IsNullOrEmpty(cachedEffectiveCharsetConv)) {
                    return ProcessExecutor.ExecuteWithCharsetConv(cmdLine, cachedEffectiveCharsetConv);
                }
                return ProcessExecutor.Execute(cmdLine, cached.ExecMode);
            }
        }

        // 慢速路径：解析配置文件
        var config = ConfigParser.Parse(configPath, exeName);
        if (config == null || string.IsNullOrEmpty(config.AliasCommand)) {
            Console.Error.WriteLine($"No alias found for '{exeName}' in {configPath}");
            return 1;
        }

        // 设置环境变量
        foreach (var (key, value) in config.EnvironmentVars) {
            NativeApi.SetEnvironmentVariable(key, Environment.ExpandEnvironmentVariables(value));
        }

        // 解析目标路径
        string targetPath = PathResolver.ExtractExecutablePath(config.AliasCommand!);
        targetPath = Environment.ExpandEnvironmentVariables(targetPath);

        string? resolved = null;
        bool isSimpleName = !targetPath.Contains('\\') && !targetPath.Contains('/') && !targetPath.StartsWith('.');
        bool isAbsolutePath = Path.IsPathRooted(targetPath);

        if (isSimpleName) {
            // 简单文件名，仅从 PATH 搜索
            resolved = PathResolver.SearchInPath(targetPath);
        } else if (isAbsolutePath) {
            // 绝对路径（支持通配符）
            if (targetPath.Contains('*') || targetPath.Contains('?')) {
                resolved = PathResolver.ResolveWildcardPath(targetPath);
            } else if (File.Exists(targetPath)) {
                resolved = targetPath;
            }
        }

        if (resolved == null) {
            Console.Error.WriteLine($"'{targetPath}' is not recognized as an internal or external command,");
            Console.Error.WriteLine("operable program or batch file.");
            return 1;
        }

        // 循环调用检测：检查解析后的路径是否指向 alias.exe 自身
        string? selfPath = Environment.ProcessPath;
        if (selfPath != null) {
            try {
                string resolvedFull = Path.GetFullPath(resolved);
                string selfFull = Path.GetFullPath(selfPath);
                if (resolvedFull.Equals(selfFull, StringComparison.OrdinalIgnoreCase)) {
                    Console.Error.WriteLine($"alias: recursive alias detected for '{exeName}' -> '{resolved}'");
                    return 1;
                }
            } catch {
                // 忽略路径比较错误
            }
        }

        // 解析参数中的通配符
        string rawArgs = PathResolver.ExtractArguments(config.AliasCommand!);
        string? resolvedArgs = PathResolver.ResolveArgumentWildcards(rawArgs, config.ExclArgs);

        // 更新缓存
        string? envDelimited = CacheManager.SerializeEnv(config.EnvironmentVars);
        CacheManager.UpdateCache(cacheKey, resolved, resolvedArgs, envDelimited, config.ExecMode, config.Prefix, config.PrefixCondition, config.CharsetConv, config.CharsetConvCondition);

        // 创建软链接（如果不存在）
        if (selfPath != null) {
            string? exeDir = Path.GetDirectoryName(selfPath);
            string? selfName = Path.GetFileName(selfPath);
            if (exeDir != null && selfName != null) {
                HelpDisplay.CreateSymlinkIfNeeded(exeDir, selfName, cacheKey);
            }
        }

        // 构建并执行命令
        string commandLine = CommandBuilder.Build(resolved, resolvedArgs, args);

        // 检查前缀和字符集转换条件
        string? effectivePrefix = GetEffectiveValue(config.Prefix, config.PrefixCondition, args);
        string? effectiveCharsetConv = GetEffectiveValue(config.CharsetConv, config.CharsetConvCondition, args);
        if (!string.IsNullOrEmpty(effectivePrefix)) {
            return ProcessExecutor.ExecuteWithPrefix(commandLine, effectivePrefix, effectiveCharsetConv);
        }
        if (!string.IsNullOrEmpty(effectiveCharsetConv)) {
            return ProcessExecutor.ExecuteWithCharsetConv(commandLine, effectiveCharsetConv);
        }
        return ProcessExecutor.Execute(commandLine, config.ExecMode);
    }

    /// <summary>获取可执行文件的名称（不含扩展名）</summary>
    /// <returns>程序文件名</returns>
    /// <remarks>
    /// 支持单文件发布（Single File Publishing）场景。
    /// 使用 Environment.ProcessPath 获取可执行文件路径，
    /// 如果不可用则回退到 AppContext.BaseDirectory。
    /// </remarks>
    private static string GetExecutableName() {
        // 支持单文件发布
        string path = Environment.ProcessPath ?? AppContext.BaseDirectory;
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>根据条件获取有效的配置值</summary>
    /// <param name="value">配置值（如前缀格式或字符集转换配置）</param>
    /// <param name="condition">条件正则表达式（可为 null）</param>
    /// <param name="args">命令行参数</param>
    /// <returns>如果条件匹配或无条件，返回配置值；否则返回 null</returns>
    private static string? GetEffectiveValue(string? value, string? condition, string[] args) {
        if (string.IsNullOrEmpty(value)) return null;

        // 无条件，直接返回
        if (string.IsNullOrEmpty(condition)) return value;

        // 有条件，检查参数是否匹配
        string argsStr = string.Join(" ", args);
        try {
            if (Regex.IsMatch(argsStr, condition, RegexOptions.IgnoreCase)) {
                return value;
            }
        } catch (Exception ex) {
            // 正则表达式无效，输出错误并不应用配置
            Console.Error.WriteLine($"Invalid regex pattern '{condition}': {ex.Message}");
            return null;
        }
        return null;
    }

    /// <summary>判断参数是否为帮助选项</summary>
    /// <param name="arg">要检查的参数</param>
    /// <returns>如果是帮助选项返回 true，否则返回 false</returns>
    /// <remarks>
    /// 支持的帮助选项格式：
    /// - -h (Unix 风格短选项)
    /// - --help (Unix 风格长选项)
    /// - /? (Windows 风格)
    /// </remarks>
    private static bool IsHelpArg(string arg) =>
        arg is "-h" or "--help" or "/?";
}

#endregion
