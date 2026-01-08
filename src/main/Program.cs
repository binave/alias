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
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using org.binave.alias;
using org.binave.alias.api;
using org.binave.alias.core;
using org.binave.alias.data;
using org.binave.alias.tool;

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
        // 检查递归深度限制
        string? depthError = RecursiveAliasDetector.CheckDepth();
        if (depthError != null) {
            Console.Error.WriteLine(depthError);
            return 1;
        }
        // 递增深度计数器
        RecursiveAliasDetector.IncrementDepth();

        // 获取程序文件名
        string exeName = GetExecutableName();

        // alias.exe 特殊命令处理
        if (exeName.Equals("alias", StringComparison.OrdinalIgnoreCase)) {
            // 帮助检查（优先处理，不需要配置文件）
            if (args.Length > 0 && args[0] is "-h" or "--help" or "/?") {
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

        // 解析参数中的通配符
        string rawArgs = PathResolver.ExtractArguments(config.AliasCommand!);
        string? resolvedArgs = PathResolver.ResolveArgumentWildcards(rawArgs, config.ExclArgs);

        // 更新缓存
        string? envDelimited = CacheManager.SerializeEnv(config.EnvironmentVars);
        CacheManager.UpdateCache(cacheKey, resolved, resolvedArgs, envDelimited, config.ExecMode, config.Prefix, config.PrefixCondition, config.CharsetConv, config.CharsetConvCondition);

        // 创建软链接（如果不存在）
        string? selfPath = Environment.ProcessPath;
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

}
