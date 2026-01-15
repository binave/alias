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
using System.Runtime.InteropServices;
using System.Text;
using org.binave.alias.api;
using org.binave.alias.tool;

namespace org.binave.alias.core;

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
            Console.Error.WriteLine($"[ERROR] alias failed to create process. Error code: {error}");
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

    /// <summary>执行命令并为每行输出添加前缀，解析字符集转换配置</summary>
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
            Console.Error.WriteLine($"[ERROR] Invalid CHARSET_CONV format: {charsetConv}");
            fromEncoding = toEncoding = Encoding.GetEncoding(Console.OutputEncoding.CodePage);
            return false;
        }

        try {
            fromEncoding = Encoding.GetEncoding(parts[0].Trim());
            toEncoding = Encoding.GetEncoding(parts[1].Trim());
            return true;
        } catch {
            Console.Error.WriteLine($"[ERROR] Invalid encoding in CHARSET_CONV: {charsetConv}");
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
            Console.Error.WriteLine($"[ERROR] alias failed to create process. Error code: {Marshal.GetLastWin32Error()}");
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
    /// <param name="prefix">可选的前缀格式（支持 %F %T %PID 等）</param>
    /// <param name="charsetConv">可选的字符集转换配置（如 UTF-8,GBK）</param>
    /// <returns>进程退出代码</returns>
    private static int ExecuteWithOutputProcessing(string commandLine, string? prefix, string? charsetConv) {
        ParseCharsetConv(charsetConv, out var fromEncoding, out var toEncoding);

        var result = CreatePipedProcess(commandLine);
        if (result == null) return Marshal.GetLastWin32Error();

        var (hReadPipe, pi) = result.Value;
        int targetPid = pi.dwProcessId;  // 子进程 PID
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
                        string line = $"{(isDynamic ? PrefixFormatter.Format(prefix, targetPid) : prefix)}{lineBuffer}\r\n";
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
                string line = $"{(isDynamic ? PrefixFormatter.Format(prefix, targetPid) : prefix)}{lineBuffer}\r\n";
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
