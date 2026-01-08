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

using System.Text;

namespace org.binave.alias.tool;

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
