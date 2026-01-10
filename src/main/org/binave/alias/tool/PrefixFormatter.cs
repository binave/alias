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
using System.Text;

namespace org.binave.alias.tool;

/// <summary>前缀格式化器 - 支持类似 Linux date 命令的格式</summary>
internal static class PrefixFormatter {
    /// <summary>格式化前缀字符串</summary>
    /// <param name="format">格式字符串，支持 %Y %m %d %H %M %S %F %T %PID 等</param>
    /// <param name="pid">子进程 PID（用于 %PID 格式符）</param>
    /// <returns>格式化后的字符串</returns>
    public static string Format(string format, int pid = 0) {
        var now = DateTime.Now;
        var sb = new StringBuilder(format.Length * 2);

        for (int i = 0; i < format.Length; i++) {
            if (format[i] == '%' && i + 1 < format.Length) {
                // 检查 %PID（3字符格式符）
                if (i + 4 <= format.Length && format.Substring(i + 1, 3) == "PID") {
                    sb.Append(pid);
                    i += 3;  // 跳过 PID，循环会再 +1
                    continue;
                }

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
