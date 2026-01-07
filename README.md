# Alias for windows

---------
# Licensing
alias is licensed under the Apache License, Version 2.0. See
[LICENSE](https://github.com/binave/alias/blob/master/LICENSE) for the full
license text.


import:
* [sqlite](https://sqlite.org)
* [SQLitePCL.raw](https://github.com/ericsink/SQLitePCL.raw)
---------


```text
alias.exe - Windows command line passthrough utility

USAGE:
    alias [[OPTIONS]] [ARGS]...
    alias <name> [ARGS]...
    alias <name>=<path> [ARGS]...

OPTIONS:
    -h, --help, /?  Display this help message
    -p  [-t]        Print cached results for aliases and paths
    -r              Refresh cache
    -e              Edit the %USERPROFILE%\.alias file (opens with notepad.exe
                    by default). You can specify the editor by setting the
                    ALIAS_EDITOR variable.

DESCRIPTION:
    A passthrough utility that reads configuration from %USERPROFILE%\.alias
    to alias commands and set environment variables.

    The program matches its own filename (without extension) to alias definitions
    in the configuration file and executes the corresponding command with all
    arguments passed through.

CONFIGURATION:
    Use %USERPROFILE%\.alias as the configuration file, employing the Linux alias
    format with support for wildcard searches.
        e.g.
            PREFIX=""%F %T %N ""
            alias bfg='""C:\Program Files\java*\bin\java.exe"" -jar D:\bfg-*\bfg-*.jar'
            alias git='C:\Git*\bin\git.exe'

    The configuration file supports the following keywords to define alias behavior:

        PREFIX=['/<regex>/ && ]""<text>""[']
            Add prefixes during output, supporting date format specifiers. Regular
            expressions can be added to determine whether prefixes are generated.
            e.g.
                PREFIX=""# %F %T %N ""
                PREFIX='/-t/ && ""@ %F %T ""'

        CHARSET_CONV=['/<regex>/ && ]""<from>,<to>""[']
            Specify character set conversion for command output.
            e.g.
                CHARSET_CONV=""UTF-8,GBK""
                CHARSET_CONV='/-s/ && ""UTF-8,GBK""'

        EXEC=<bool|seconds>
            Delay process replacement by the specified number of seconds.

        EXCL_ARG=<indexes>
            Disable wildcard parsing for arguments at the specified comma-separated
            indexes.


```

