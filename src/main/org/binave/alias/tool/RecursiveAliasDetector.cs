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

    /// <summary>获取最大别名深度（从环境变量或默认值）</summary>
    private static int GetMaxDepth() {
        string? maxDepthStr = Environment.GetEnvironmentVariable(AliasMaxDepthEnvVar);
        return int.TryParse(maxDepthStr, out int maxDepth) && maxDepth > 0 ? maxDepth : DefaultMaxAliasDepth;
    }

    /// <summary>检查别名深度是否超过限制</summary>
    /// <returns>如果超过限制返回错误消息，否则返回 null</returns>
    public static string? CheckDepth() {
        string? depthStr = Environment.GetEnvironmentVariable(AliasDepthEnvVar);
        if (depthStr == null) return null;
        int maxDepth = GetMaxDepth();
        if (int.TryParse(depthStr, out int depth) && depth > maxDepth) {
            return $"alias: maximum recursion depth ({maxDepth}) exceeded";
        }
        return null;
    }

    /// <summary>递增别名深度</summary>
    /// <remarks>
    /// 每次 alias.exe 执行时调用：
    /// - 如果 ALIAS_DEPTH 不存在，设置为 0
    /// - 如果 ALIAS_DEPTH 存在，递增 1
    /// </remarks>
    public static void IncrementDepth() {
        string? depthStr = Environment.GetEnvironmentVariable(AliasDepthEnvVar);
        int depth = int.TryParse(depthStr, out int d) ? d + 1 : 0;
        NativeApi.SetEnvironmentVariable(AliasDepthEnvVar, depth.ToString());
    }
}
