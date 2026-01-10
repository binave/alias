# Alias for Windows

一个 Windows 命令行别名工具，通过配置文件管理命令别名、环境变量和输出格式。<br/>

🚀 开发缘由
- 在 Windows 操作系统中，为 .exe 文件创建软链接（symbolic link）时，常常会因找不到所需的 .dll 文件而失败。而 Linux 和 macOS 则不存在此问题。

✨ 解决的问题
- **参数固化**：将常用命令与其固定参数直接集成，无需每次手动输入。
- **简化 PATH**：大幅缩短 PATH 环境变量长度；重装系统后只需添加一个路径即可使用所有工具。
- **版本管理**：当命令行工具更新版本时，无需手动修改每个引用路径中的版本号。
- **Linux 风格支持**：支持类似 Linux 的 alias 配置格式，便于兼容 WSL 的 shell 环境。
- **IDE 兼容性**：部分 IDE 不支持通过 doskey 设置的命令别名或批处理脚本（.bat），但支持直接填写 .exe 路径。
- **增强功能**：为 ping 等命令自动添加时动态间戳，便于日志追踪。
- **编码兼容**：处理部分跨平台命令行工具在非英文输出时出现的乱码问题。

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)

## 功能特性

- **别名管理**：通过配置文件定义命令别名，简化常用命令。配置文件符合 linux alias 命令格式。
- **环境变量设置**：为每个别名配置专属环境变量
- **通配符支持**：支持通配符路径自动解析（`C:\Tool*\app*.exe`）
- **高性能缓存**：使用 SQLite 缓存已解析的路径，大幅提升性能
- **输出前缀**：支持自定义输出前缀，包括动态时间戳格式。支持参数匹配
- **字符集转换**：支持命令输出的字符集转换（如 UTF-8 转 GBK）。支持参数匹配
- **Exec 模式**：支持进程替换模式，可选择立即退出或延迟退出
- **软链接支持**：自动为别名创建软链接，便于直接调用
- **递归保护**：检测并防止别名无限递归调用

## 安装

### 从源码构建

- 项目依赖：
    * dotnet sdk 8+
    * Visual Studio 2022 build tools
        * 可以使用 [xlib.cmd](https://github.com/binave/xcmd/blob/develop/xlib.cmd) vsi core -i 自动安装没有 IDE 的 Visual Studio 2022。<br/>或者修改 [build.cmd](build.cmd) 中 `VC_VARS_PATH` 的值为 vs2002 安装目录。

- 构建项目：直接执行 build.cmd 即可。

构建完成后，可执行文件将位于 `bin/publish/` 目录下。

### 手动安装

1. 将 `alias.exe` 复制到系统的 PATH 环境变量中的任意目录
2. 确保该目录有写入权限（用于创建软链接）

## 快速开始

### 1. 创建配置文件

配置文件位于 `%USERPROFILE%\.alias`，首次运行时会自动创建。

```bash
# 运行 alias.exe 创建配置文件并打开
alias.exe -e
```

### 2. 配置别名

编辑配置文件，添加别名定义：

```bash
# 使用通配符（自动匹配最新版本）
alias java='"C:\Program Files\java*\bin\java.exe"'

# 关闭颜色，解决 diff 乱码
GIT_CONFIG_PARAMETERS="'color.diff=never'"
CHARSET_CONV='/diff/ && "UTF-8,GBK"'
alias git='"D:\Tools\MinGit-*-busybox-*-bit\cmd\git.exe"'

# 带参数的别名
alias node='"C:\Program Files\nodejs\node.exe" --use-openssl-ca'

# ping 时增加时间戳前缀和进程id
PREFIX='/[^\.]\.[^\.]/ && "PING: %F %T %N %PID "'
alias ping=ping.exe

# 设置环境变量，5 秒后退出 alias 进程保留 idea 进程
JAVA_HOME="C:\Program Files\java\jdk-17"
# EXEC 与 PREFIX 和 CHARSET_CONV 互斥
EXEC=5
alias idea='D:\ideaIC-20*.win\bin\idea.bat'
```

### 3. 使用别名

```bash
# 通过 alias.exe 调用，将会建立目标软连接。
alias.exe git clone https://github.com/binave/alias.git

# 直接使用别名
git clone https://github.com/binave/alias.git
```

## 配置说明

配置文件使用类 Linux shell 语法定义别名：

```bash
# 临时别名定义，实际上是调用了 doskey，仅在当前会话有效。
alias <name>='<command>'

# 全局环境变量（对所有之后的别名生效）
export VAR1=1

# 临时环境变量（仅对下一个别名生效）
VAR=value
alias name='command'

# 前缀配置（带条件判断）
PREFIX='/\-t / && "# %F %T "' # 仅当参数包含 -t 时添加时间戳前缀
alias name='command -t arg1'
PREFIX='# %F %T %N '          # 所有输出都添加前缀
alias name='command'

# 字符集转换，仅当参数包含 diff 时将 UTF-8 输出转换为 GBK
CHARSET_CONV='/diff/ && "UTF-8,GBK"'
alias name='command diff'

# Exec 模式
EXEC=true                     # 立即退出父进程，保留子进程
alias name='command'
EXEC=5                        # 5 秒后退出父进程，保留子进程
alias name='command'

# 排除参数通配符处理
EXCL_ARG=1,2                  # 第1、2个参数不进行通配符解析
alias name='command arg*1 arg*2'
```

### 前缀格式说明

前缀支持以下占位符（类似 Linux date 命令格式）：

| 占位符 | 说明 | 示例 |
|--------|------|------|
| `%PID` | 目标进程ID | 12345 |
| `%F` | 完整日期 | 2026-01-01 |
| `%T` | 完整时间 | 12:30:45 |
| `%Y` | 年份（4位） | 2026 |
| `%y` | 年份（2位） | 26 |
| `%m` | 月份 | 01 |
| `%d` | 日 | 02 |
| `%H` | 小时（24小时制） | 15 |
| `%I` | 小时（12小时制） | 03 |
| `%M` | 分钟 | 30 |
| `%S` | 秒 | 45 |
| `%N` | 毫秒 | 123 |
| `%n` | 换行符 | |
| `%t` | 制表符 | |
| `%%` | 百分号 | % |

## 命令行选项

```bash
# 显示帮助信息
alias.exe -h
alias.exe --help
alias.exe /?

# 打印缓存内容（显示已解析的路径）
alias.exe
alias.exe -p
alias.exe -p -t    # 显示更新时间

# 刷新缓存（删除并重建）
alias.exe -r       # 需要管理员权限或开发者模式

# 编辑配置文件
alias.exe -e       # 使用 ALIAS_EDITOR 环境变量指定的编辑器，默认为 notepad.exe

# 定义临时别名（透传给 doskey.exe）
alias.exe name='command $*'

# 调用指定别名
alias.exe <name> [args...]
```

## 环境变量

| 变量名 | 说明 | 默认值 |
|--------|------|--------|
| `ALIAS_EDITOR` | 配置文件编辑器路径 | notepad.exe |
| `ALIAS_MAX_DEPTH` | 最大递归深度（防止无限递归） | 9 |

## 依赖

- [sqlite](https://sqlite.org) - 嵌入式数据库
- [SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw) - SQLite Native 绑定

## 许可证

本项目采用 Apache License 2.0 许可证。详见 [LICENSE](LICENSE) 文件。

## 致谢

本项目从 [Scoop/shim](https://github.com/ScoopInstaller/Shim/blob/b0bdac7f4f72dce44e4af1c774243905b5548e1d/src/shim.cs) 早期版本分叉而来，已经过全面重构，其结构和功能与源项目相比已存在显著差异。
