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
using System.Text;

namespace org.binave.alias.tool;

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
    public static bool IsSimpleFileName(string path) {
        return !path.Contains('\\') && !path.Contains('/') && !path.StartsWith('.');
    }

    /// <summary>从 PATH 环境变量中搜索可执行文件</summary>
    /// <param name="fileName">文件名（如 git.exe 或 git）</param>
    /// <returns>找到的完整路径，未找到返回 null</returns>
    public static string? SearchInPath(string fileName) {
        return SearchInPathInternal(fileName, null);
    }

    /// <summary>从 PATH 环境变量中搜索可执行文件（内部实现）</summary>
    /// <param name="fileName">文件名（如 git.exe 或 git）</param>
    /// <param name="skipBeforePath">跳过此路径所在目录之前的所有目录（可为 null）</param>
    /// <returns>找到的完整路径，未找到返回 null</returns>
    public static string? SearchInPathInternal(string fileName, string? skipBeforePath) {
        // 获取 PATH 环境变量
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        // 获取 alias.exe 所在目录（用于跳过同名文件）
        string? aliasExeDir = null;
        string? aliasExePath = Environment.ProcessPath;
        if (aliasExePath != null) {
            aliasExeDir = Path.GetDirectoryName(aliasExePath);
        }

        // 确定要跳过的目录（skipBeforePath 所在目录）
        string? skipBeforeDir = null;
        if (!string.IsNullOrEmpty(skipBeforePath)) {
            skipBeforeDir = Path.GetDirectoryName(skipBeforePath);
        }

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
                extensions.AddRange([".exe", ".cmd", ".bat", ".com"]);
            }
        }

        bool foundSkipDir = skipBeforeDir == null; // 如果没有指定跳过目录，则不跳过

        foreach (string dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
            string trimmedDir = dir.Trim();

            // 检查是否到达要跳过的目录
            if (!foundSkipDir) {
                if (trimmedDir.Equals(skipBeforeDir, StringComparison.OrdinalIgnoreCase)) {
                    foundSkipDir = true;
                }
                continue; // 跳过此目录之前的所有目录（包括此目录本身）
            }

            foreach (string ext in extensions) {
                string fullPath = Path.Combine(trimmedDir, fileName + ext);
                if (File.Exists(fullPath)) {
                    // 如果在 alias.exe 所在目录找到同名文件，跳过继续搜索
                    if (aliasExeDir != null &&
                        trimmedDir.Equals(aliasExeDir, StringComparison.OrdinalIgnoreCase)) {
                        // 检查文件名是否与搜索的文件名相同（不含扩展名）
                        string foundName = Path.GetFileNameWithoutExtension(fullPath);
                        string searchName = Path.GetFileNameWithoutExtension(fileName);
                        if (string.IsNullOrEmpty(searchName)) searchName = fileName;
                        if (foundName.Equals(searchName, StringComparison.OrdinalIgnoreCase)) {
                            continue; // 跳过，继续搜索下一个
                        }
                    }
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
