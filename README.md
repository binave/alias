# Alias for Windows

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)
- [简体中文](README.zh-CN.md)

A powerful Windows command line alias utility for managing command aliases, environment variables,<br/> and output formatting through a configuration file.,<br/>

🚀 Motivation
- On Windows, creating symbolic links (symlinks) for .exe files often fails because the required .dll dependencies cannot be found. This issue does not occur on Linux or macOS.
- The PATH environment variable is too long, and repeatedly modifying it is cumbersome.

✨ Problems Solved
- **Hardcoded Arguments**: Common commands are bundled with their fixed arguments, eliminating the need to type them repeatedly.
- **Simplified PATH**: Drastically reduces the length of the PATH environment variable. After reinstalling your system, you only need to add a single directory to PATH.
- **Version Management**: When command-line tools are updated, you no longer need to manually update version numbers embedded in file paths.
- **Linux-style Aliases**: Supports Linux-like alias configuration syntax for seamless compatibility with WSL shell environments.
- **IDE Compatibility**: Some IDEs do not support aliases defined via doskey or batch scripts (.bat), but they do accept direct .exe paths.
- **Enhanced Utilities**: Automatically adds dynamic timestamps to commands like ping for better logging and debugging.
- **Encoding Fix**: Handles garbled output from certain cross-platform CLI tools when they output non-English text.

## Features

- **Alias Management**: Define command aliases through configuration files to simplify common commands
- **Environment Variables**: Configure dedicated environment variables for each alias
- **Wildcard Support**: Automatic wildcard path resolution (`C:\Tool*\app*.exe`)
- **High-Performance Caching**: Use SQLite to cache resolved paths for significantly improved performance
- **Output Prefix**: Support custom output prefixes, including dynamic timestamp formats, Supports parameter matching (macOS use `[command] |` [xlib](https://github.com/binave/xcmd/blob/develop/xlib) `prefix "%T %F "`)
- **Charset Conversion**: Support command output charset conversion (e.g., UTF-8 to GBK), Supports parameter matching
- **Exec Mode**: Support process replacement mode with immediate or delayed exit options
- **Symlink Support**: Automatically create symlinks for aliases for direct invocation
- **Recursion Protection**: Detect and prevent infinite recursive alias calls

## Installation

### Building from Source

- Project dependencies:
    * dotnet sdk 8+
    * Visual Studio 2022 build tools
        * You can use [xlib.cmd](https://github.com/binave/xcmd/blob/develop/xlib.cmd) `vsi core 2022 -i` to automatically install Visual Studio 2022 without IDE. <br/>Or modify `VC_VARS_PATH` in [build.cmd](build.cmd) to the vs2002 installation directory.

- Build the project: Simply run build.cmd.

After building, the executable file will be located in the `bin/publish/` directory.

### Manual Installation

1. Copy `alias.exe` to any directory in the system's PATH environment variable
2. Ensure the directory has write permissions (for creating symlinks)

## Quick Start

### 1. Create Configuration File

The configuration file is located at `%USERPROFILE%\.alias` and will be created automatically on first run.

```bash
# Run alias.exe to create configuration file and open it
alias -e
```

### 2. Configure Aliases

Edit the configuration file and add alias definitions:

```bash
# Use wildcards (automatically match latest version)
alias java='"C:\Program Files\java*\bin\java.exe"'

# Disable color to fix diff garbled output
GIT_CONFIG_PARAMETERS="'color.diff=never'"
CHARSET_CONV='/diff/ && "UTF-8,GBK"'
alias git='"D:\Tools\MinGit-*-busybox-*-bit\cmd\git.exe"'

# Alias with arguments
alias node='"C:\Program Files\nodejs\node.exe" --use-openssl-ca'

# Add a dynamic timestamp prefix to ping operations, along with the process ID.
PREFIX='/[^\.]\.[^\.]/ && "PING: %F %T %N %PID "'
alias ping=ping.exe

# Set environment variable, exit alias process after 5 seconds while keeping idea process
JAVA_HOME="C:\Program Files\java\jdk-17"
# EXEC is mutually exclusive with PREFIX and CHARSET_CONV.
EXEC=5
alias idea='D:\ideaIC-20*.win\bin\idea.bat'
```

### 3. Use Aliases

```bash
# Call through alias, will create target symlink
alias git clone https://github.com/binave/alias.git

# Use alias directly
git clone https://github.com/binave/alias.git
```

## Configuration

The configuration file uses Linux shell-like syntax to define aliases:

```bash
# Temporary alias definition, actually calls doskey, only valid for current session
alias <name>='<command>'

# Global environment variables (effective for all subsequent aliases)
export VAR1=1

# Temporary environment variable (only effective for next alias)
VAR=value
alias name='command'

# Prefix configuration (with conditional judgment)
PREFIX='/\-t / && "# %F %T "' # Add timestamp prefix only when argument contains '-t'
alias name='command -t arg1'
PREFIX='# %F %T %N '          # Add prefix to all output
alias name='command'

# Charset conversion, convert UTF-8 output to GBK only when argument contains 'diff'
CHARSET_CONV='/diff/ && "UTF-8,GBK"'
alias name='command diff'

# Exec mode
EXEC=true                     # Exit parent process immediately, keep child process
alias name='command'
EXEC=5                        # Exit parent process after 5 seconds, keep child process
alias name='command'

# Exclude argument wildcard parsing
EXCL_ARG=1,2                  # Don't parse wildcards for arguments 1 and 2
alias name='command arg*1 arg*2'
```

### Prefix Format

Prefix supports the following placeholders (similar to Linux date command format):

| Placeholder | Description | Example |
|-------------|-------------|---------|
| `%PID` | target process ID | 12345 |
| `%F` | Full date | 2026-01-01 |
| `%T` | Full time | 12:30:45 |
| `%Y` | Year (4 digits) | 2026 |
| `%y` | Year (2 digits) | 26 |
| `%m` | Month | 01 |
| `%d` | Day | 02 |
| `%H` | Hour (24-hour format) | 15 |
| `%I` | Hour (12-hour format) | 03 |
| `%M` | Minute | 30 |
| `%S` | Second | 45 |
| `%N` | Millisecond | 123 |
| `%n` | Newline | |
| `%t` | Tab | |
| `%%` | Percent sign | % |

## Command Line Options

```bash
# Display help information
alias -h
alias --help
alias /?

# Print cache contents (show resolved paths)
alias
alias -p
alias -p -t    # Show update timestamps

# Refresh cache (delete and rebuild)
alias -r       # Requires admin privileges or developer mode

# Edit configuration file
alias -e       # Use editor specified by ALIAS_EDITOR environment variable, default is notepad.exe

# Define temporary alias (pass through to doskey.exe)
alias name='command $*'

# Call specific alias
alias <name> [args...]
```

## Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `ALIAS_EDITOR` | Configuration file editor path | notepad.exe |
| `ALIAS_MAX_DEPTH` | Maximum recursion depth (prevent infinite recursion) | 9 |

## Dependencies

- [sqlite](https://sqlite.org) - Embedded database
- [SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw) - SQLite native binding

## License

This project is licensed under the Apache License 2.0. See [LICENSE](LICENSE) file for details.

## Credits

`Forked` from [Scoop/shim](https://github.com/ScoopInstaller/Shim/blob/b0bdac7f4f72dce44e4af1c774243905b5548e1d/src/shim.cs), this project has been fully refactored and now differs significantly from its source in structure and functionality.
