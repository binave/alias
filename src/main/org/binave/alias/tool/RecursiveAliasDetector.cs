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
using org.binave.alias.api;
using org.binave.alias.entity;
using org.binave.alias.data;

namespace org.binave.alias.tool;

/// <summary>递归别名检测器</summary>
/// <remarks>
/// 通过 ALIAS_DEPTH 环境变量跟踪别名调用深度，防止无限递归。
/// 每次 alias.exe 执行时递增深度计数器，超过 ALIAS_MAX_DEPTH 时报错。
/// </remarks>
internal static class RecursiveAliasDetector {
    private const string AliasDepthEnvVar = "ALIAS_DEPTH";
    private const string AliasMaxDepthEnvVar = "ALIAS_MAX_DEPTH";
    private const int DefaultMaxAliasDepth = 9;


    /// <summary>检查并递增别名深度</summary>
    /// <param name="exceeded">输出：是否超过最大深度限制</param>
    /// <param name="atThreshold">输出：是否达到深度阈值（用于触发回退搜索）</param>
    /// <returns>如果超过限制返回错误消息，否则返回 null</returns>
    /// <remarks>
    /// 每次 alias.exe 执行时调用：
    /// - 检查当前深度是否超过限制
    /// - 检查是否达到阈值（用于回退搜索）
    /// - 递增深度计数器
    /// </remarks>
    public static string? CheckAndIncrement() {
        string? depthStr = Environment.GetEnvironmentVariable(AliasDepthEnvVar);
        int depth = 0;

        if (depthStr != null && int.TryParse(depthStr, out int d)) {
            depth = d;
            int maxDepth = int.TryParse(
                Environment.GetEnvironmentVariable(AliasMaxDepthEnvVar),
                out int tmpMaxDepth
            ) && tmpMaxDepth > 0 ? tmpMaxDepth : DefaultMaxAliasDepth;
            if (depth > maxDepth) {
                return $"maximum recursion depth ({maxDepth}) exceeded";
            }
        }

        // 递增深度
        NativeApi.SetEnvironmentVariable(AliasDepthEnvVar, (depth + 1).ToString());
        return null;
    }

    /// <summary>执行回退搜索并更新缓存</summary>
    /// <param name="configPath">配置文件路径</param>
    /// <param name="cacheKey">缓存键</param>
    /// <param name="cached">缓存条目</param>
    /// <returns>(成功标志, 错误消息, 解析后的路径)</returns>
    /// <remarks>
    /// 当递归深度达到阈值时，跳过缓存路径之前的目录继续搜索。
    /// 如果找到新路径，更新缓存；如果失败，返回错误信息。
    /// </remarks>
    public static string? FallbackSearch(string configPath, string cacheKey, CacheEntry cached) {
        var config = ConfigParser.Parse(configPath, cacheKey);
        if (config == null || string.IsNullOrEmpty(config.AliasCommand)) {
            return null;
        }

        string targetPath = PathResolver.ExtractExecutablePath(config.AliasCommand);
        if (string.IsNullOrEmpty(targetPath)) {
            return $"invalid command path in {configPath}:{config.LineNumber}";
        }
        targetPath = Environment.ExpandEnvironmentVariables(targetPath);

        // 判断是简单文件名还是绝对路径
        if (PathResolver.IsSimpleFileName(targetPath)) {
            // 简单文件名：跳过缓存路径之前的目录继续搜索
            string? resolved = PathResolver.SearchInPathInternal(targetPath, cached.Value);
            if (resolved != null) {
                // 更新缓存为新找到的路径
                CacheManager.UpdateCache(cacheKey, resolved, cached.Args, cached.Env,
                    cached.ExecMode, cached.Prefix, cached.PrefixCondition,
                    cached.CharsetConv, cached.CharsetConvCondition);
                return null;

            } else {
                return $"'{targetPath}' not found in PATH after cached location";
            }
        } else {
            // 绝对路径：报错显示路径和行号
            return $"absolute path cannot be resolved: {targetPath}\n              {configPath}:{config.LineNumber}";
        }
    }

}
