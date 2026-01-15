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
using System.Text.RegularExpressions;
using org.binave.alias.entity;

namespace org.binave.alias.data;

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

    public static readonly string ALIAS_HEAD = @"# alias configuration file
# e.g.
#   alias bfg='""C:\Program Files\java*\bin\java.exe"" -jar D:\bfg-*\bfg-*.jar'
#   alias git='C:\Git*\bin\git.exe'
";

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
            File.WriteAllText(configPath, ALIAS_HEAD);
            Console.Error.WriteLine($"[ERROR] Created empty config file: {configPath}");
            return true;
        } catch {
            Console.Error.WriteLine($"[ERROR] Failed to create config file: {configPath}");
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
            int lineNumber = 0;
            foreach (string rawLine in File.ReadLines(configPath)) {
                lineNumber++;
                string line = rawLine.Trim();

                // 跳过空行和注释
                if (string.IsNullOrEmpty(line) || line.StartsWith('#')) {
                    continue;
                }

                var aliasMatch = AliasRegex.Match(line); // 检查 alias 定义
                if (aliasMatch.Success) {
                    if (aliasMatch.Groups[1].Value.Equals(aliasName, StringComparison.OrdinalIgnoreCase)) {
                        config.AliasCommand = StripQuotes(aliasMatch.Groups[2].Value);
                        config.LineNumber = lineNumber; // 记录行号
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
            Console.Error.WriteLine($"[ERROR] Error reading alias configuration file: {ex.Message}");
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


    /// <summary>获取配置文件中所有别名定义（包含行号）</summary>
    /// <param name="configPath">配置文件路径</param>
    /// <returns>别名信息列表（名称、命令、行号）</returns>
    public static List<(string Name, string Command, int LineNumber)> GetAllAliasesWithLineNumbers(string configPath) {
        var aliases = new List<(string Name, string Command, int LineNumber)>();

        if (!File.Exists(configPath)) {
            return aliases;
        }

        try {
            int lineNumber = 0;
            foreach (string rawLine in File.ReadLines(configPath)) {
                lineNumber++;
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
                    aliases.Add((name, command, lineNumber));
                }
            }
        } catch {
            // 忽略错误
        }

        return aliases;
    }
}
