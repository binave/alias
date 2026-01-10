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
using System.Runtime.InteropServices;
using org.binave.alias.api;
using org.binave.alias.core;
using org.binave.alias.data;
using org.binave.alias.tool;

namespace org.binave.alias;

/// <summary>帮助信息显示</summary>
/// <remarks>
/// 提供程序的使用说明、配置示例和参数说明。
/// 当用户调用 alias.exe --help 时显示。
/// </remarks>
internal static class HelpDisplay {
    /// <summary>显示帮助信息到控制台</summary>
    public static void Show() {
        Console.WriteLine(@"
alias - Windows command line passthrough utility

SYNOPSIS
    alias [[OPTIONS]] [[ARGS]]...
    alias <name> [[ARGS]]...
    alias <name>=<command>

DESCRIPTION
    A passthrough utility that reads configuration from %USERPROFILE%\.alias
    to alias commands and set environment variables.

    The program matches its own filename (without extension) to alias definitions
    in the configuration file and executes the corresponding command with all
    arguments passed through.

OPTIONS
    -h, --help, /?
        Display this help message.

    [-p [-t]]
        Print cached results for aliases and paths. Use -t to show timestamps.

    -r
        Refresh cache (delete and rebuild). Requires admin privileges.

    -e
        Edit the configuration file. Uses ALIAS_EDITOR or notepad.exe.

ENVIRONMENT
    ALIAS_EDITOR
        Editor program for -e flag. Default: notepad.exe

    ALIAS_MAX_DEPTH
        Maximum recursion depth for alias chains. When an alias points to
        another alias (via symlink to alias.exe), this limits how deep the
        chain can go. Default: 9

CONFIGURATION
    Configuration file: %USERPROFILE%\.alias
    Use Linux alias format with support for wildcard searches.
    e.g.
        PREFIX=""%F %T %N [%PID] ""
        alias bfg='""C:\Program Files\java*\bin\java.exe"" -jar D:\bfg-*\bfg-*.jar'
        alias git='C:\Git*\bin\git.exe'

    Keywords:
        PREFIX=['/<regex>/ && ]""<text>""[']
            Add prefixes during output, supporting date format specifiers.
            e.g.
                PREFIX=""# %F %T %N [%PID] ""
                PREFIX='/\-t / && ""NAME: %F %T %PID ""'

            Format specifiers:
                %Y - Year (4 digits)    %y - Year (2 digits)
                %m - Month              %d - Day
                %H - Hour (24h)         %I - Hour (12h)
                %M - Minute             %S - Second
                %F - Date (YYYY-MM-DD)  %T - Time (HH:MM:SS)
                %N - Milliseconds       %PID - target process ID
                %n - Newline            %t - Tab
                %% - Literal %

        CHARSET_CONV=['/<regex>/ && ]""<from>,<to>""[']
            Specify character set conversion for command output.
            e.g.
                CHARSET_CONV=""UTF-8,GBK""
                CHARSET_CONV='/diff/ && ""UTF-8,GBK""'

        EXCL_ARG=<indexes>
            Disable wildcard parsing for arguments at specified indexes.
            Multiple numbers should be separated by ',' commas.

        EXEC=<bool|seconds>
            Delay process replacement by the specified number of seconds.
            EXEC is mutually exclusive with PREFIX and CHARSET_CONV.

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

        // 执行 doskey.exe /macros 并添加前缀
        ProcessExecutor.ExecuteWithPrefix("doskey.exe /macros", "doskey ");
    }

    /// <summary>编辑配置文件</summary>
    public static void EditConfig() {
        string configPath = ConfigParser.GetConfigPath();

        // 如果配置文件不存在，创建空文件
        if (!File.Exists(configPath)) {
            try {
                File.WriteAllText(configPath, ConfigParser.ALIAS_HEAD);
            } catch (Exception ex) {
                Console.Error.WriteLine($"Failed to create config file: {ex.Message}");
                return;
            }
        }

        // 获取编辑器：优先使用 ALIAS_EDITOR 环境变量，否则使用 notepad.exe
        string editor = Environment.GetEnvironmentVariable("ALIAS_EDITOR") ?? "notepad.exe";
        ProcessExecutor.Execute($"{editor} \"{configPath}\"", -1);
    }

    /// <summary>刷新缓存（删除数据库并重新生成全量缓存，同步软链接）</summary>
    public static void RefreshCache() {
        // 获取 alias.exe 所在目录
        string? exePath = Environment.ProcessPath;
        string? exeDir = exePath != null ? Path.GetDirectoryName(exePath) : null;
        string? exeName = exePath != null ? Path.GetFileName(exePath) : null;

        // 检查是否有管理员权限
        if (!NativeApi.IsRunningAsAdmin()) {
            Console.Error.WriteLine("alias: requires admin privileges or Developer Mode enabled.");
            Console.Error.WriteLine("       Run as Administrator or enable Developer Mode in Windows Settings.");
            return;
        }

        // 删除旧数据库
        CacheManager.DeleteDatabase();

        // 配置文件路径
        string configPath = ConfigParser.GetConfigPath();

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
            Console.Error.WriteLine($"      [symlink] {aliasKey}.exe failed: error {error}");
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
