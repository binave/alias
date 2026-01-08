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

namespace org.binave.alias.api;

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

    /// <summary>打开进程令牌</summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    /// <summary>获取令牌信息</summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, out int TokenInformation, int TokenInformationLength, out int ReturnLength);

    /// <summary>获取当前进程句柄</summary>
    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    /// <summary>令牌访问权限：查询</summary>
    public const uint TOKEN_QUERY = 0x0008;

    /// <summary>令牌信息类：提升状态</summary>
    public const int TokenElevation = 20;

    /// <summary>检查当前进程是否以管理员权限运行</summary>
    /// <returns>如果是管理员返回 true，否则返回 false</returns>
    public static bool IsRunningAsAdmin() {
        IntPtr tokenHandle = IntPtr.Zero;
        try {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out tokenHandle)) {
                return false;
            }
            if (!GetTokenInformation(tokenHandle, TokenElevation, out int elevation, sizeof(int), out _)) {
                return false;
            }
            return elevation != 0;
        } finally {
            if (tokenHandle != IntPtr.Zero) {
                CloseHandle(tokenHandle);
            }
        }
    }
}
