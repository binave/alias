@echo off
setlocal
set "_VC_VARS_PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\2022"
@REM set "_VC_VARS_PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\18"
set _SQLITE_URL=https://sqlite.org/2025/sqlite-amalgamation-3510100.zip

set _ARCH=
if "%~1" neq "" for %%a in (
    x86 x86_amd64 x86_x64 x86_arm x86_arm64
    amd64 x64 amd64_x86 x64_x86 amd64_arm x64_arm amd64_arm64 x64_arm64
    arm64 arm64_amd64 arm64_x64 arm64_x86 arm64_arm
) do if /i "%~1"=="%%a" set _ARCH=%%a

if "%~1" neq "" if not defined _ARCH (
    >&2 echo Platform '%~1' not support.
    exit /b 1
)

if not defined _ARCH for /f "usebackq tokens=2*" %%a in (`
    reg.exe query "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" ^
        /v PROCESSOR_ARCHITECTURE
`) do if "%%a"=="REG_SZ" call :toLower %%b _ARCH

if not defined _ARCH (
    >&2 echo Unknown error
    exit /b 1
)

set _SQLITE_OUT=target\sqlite3\%_ARCH%

pushd "%~dp0"

set _SQLITE_SRC=
call :search-sqlite-amalgamation _SQLITE_SRC

if not defined _SQLITE_SRC PowerShell.exe ^
    -NoLogo ^
    -NonInteractive ^
    -ExecutionPolicy Unrestricted ^
    -Command ^
    "& {" ^
    "   param($url, $outputDir)" ^
    "   Add-Type -AssemblyName System.IO.Compression;" ^
    "   Add-Type -AssemblyName System.IO.Compression.FileSystem;" ^
    "   try {" ^
    "       $response=Invoke-WebRequest -Uri $url -UseBasicParsing;" ^
    "       $memStream=[System.IO.MemoryStream]::new($response.Content);" ^
    "       $archive=[System.IO.Compression.ZipArchive]::new($memStream);" ^
    "       [System.IO.Compression.ZipFileExtensions]::ExtractToDirectory($archive, $outputDir);" ^
    "       Write-Host '[INFO] Download and extraction successful!' -ForegroundColor Green" ^
    "   } catch {" ^
    "       Write-Error ('[ERROR] Failed: ' + $_.Exception.Message)" ^
    "   } finally {" ^
    "       if ($archive) { $archive.Dispose() };" ^
    "       if ($memStream) { $memStream.Dispose() };" ^
    "   }" ^
    "}" ^
    -ArgumentList nul %_SQLITE_URL% . || exit /b %errorlevel%

call :search-sqlite-amalgamation _SQLITE_SRC
if not defined _SQLITE_SRC (
    >&2 echo sqlite-amalgamation source directory not found
    exit /b 1
)

if not exist "%_VC_VARS_PATH%\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" (
    >&2 echo Visual Studio Build Tools not found in "%_VC_VARS_PATH%"
    exit /b 1
)

:: Initialize Visual Studio build environment
call "%_VC_VARS_PATH%\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" %_ARCH% || exit /b %errorlevel%

:: ============================================================================
:: Build SQLite static library
:: ============================================================================

    set _ARCH_FLAGS=
    set _MACHINE_TYPE=
    if "%_ARCH:~-5%"=="amd64" (
        set _ARCH_FLAGS=/D_WIN64
        set _MACHINE_TYPE=x64
    ) else if "%_ARCH:~-3%"=="x64" (
        set _ARCH_FLAGS=/D_WIN64
        set _MACHINE_TYPE=x64
    ) else if "%_ARCH:~-3%"=="x86" (
        @REM set _ARCH_FLAGS=/arch:SSE2
        set _MACHINE_TYPE=x86
    ) else if "%_ARCH:~-5%"=="arm64" (
        set _ARCH_FLAGS=/D_ARM64_WINAPI_PARTITION_DESKTOP_SDK_AVAILABLE=1
        set _MACHINE_TYPE=arm64
    ) else (
        >&2 echo '%_ARCH%' not support
        exit /b 1
    )

    2>nul mkdir %_SQLITE_OUT%

    set _COMMON_FLAGS=/O2 /MT /GL /GF /Gy /GR- /EHsc /DNDEBUG /D_CRT_SECURE_NO_WARNINGS
    set _SQLITE_FLAGS=^
        /DSQLITE_THREADSAFE=1 ^
        /DSQLITE_DEFAULT_MEMSTATUS=0 ^
        /DSQLITE_OMIT_DEPRECATED ^
        /DSQLITE_OMIT_SHARED_CACHE ^
        /DSQLITE_USE_ALLOCA ^
        /DSQLITE_OMIT_AUTOVACUUM ^
        /DSQLITE_OMIT_UTF16 ^
        /DSQLITE_OMIT_DATETIME_FUNCS ^
        /DSQLITE_OMIT_BUILTIN_TEST ^
        /DSQLITE_OMIT_AUTHORIZATION ^
        /DSQLITE_OMIT_EXPLAIN ^
        /DSQLITE_OMIT_INTEGRITY_CHECK ^
        /DSQLITE_OMIT_TRACE ^
        /DSQLITE_OMIT_WAL ^
        /DSQLITE_OMIT_FOREIGN_KEY ^
        /DSQLITE_OMIT_CHECK ^
        /DSQLITE_OMIT_OR_OPTIMIZATION ^
        /DSQLITE_MAX_ATTACHED=0 ^
        /DSQLITE_DEFAULT_CACHE_SIZE=100 ^
        /DSQLITE_DEFAULT_PAGE_SIZE=1024 ^
        /DSQLITE_DEFAULT_SYNCHRONOUS=1 ^
        /DSQLITE_DEFAULT_WAL_SYNCHRONOUS=1 ^
        /DSQLITE_ENABLE_DBSTAT_VTAB ^
        /DSQLITE_ENABLE_STAT4 ^
        /DSQLITE_ENABLE_MEMORY_MANAGEMENT


:: Check if SQLite static library needs to be built
if not exist "%_SQLITE_OUT%\e_sqlite3.lib" (
    pushd "%_SQLITE_SRC%"

    echo Building SQLite static library...
    :: Compile static library
    cl %_COMMON_FLAGS% %_SQLITE_FLAGS% %_ARCH_FLAGS% ^
        /Fo"..\%_SQLITE_OUT%\sqlite3.obj" ^
        /c sqlite3.c || (
        >&2 echo Failed to compile SQLite
            exit /b 1
        )

    call :headers-machine ..\%_SQLITE_OUT%\sqlite3.obj

    :: Create static library (named e_sqlite3.lib to match SQLitePCLRaw P/Invoke name)
    lib /OUT:..\%_SQLITE_OUT%\e_sqlite3.lib ..\%_SQLITE_OUT%\sqlite3.obj || (
        >&2 echo Failed to create SQLite static library
        exit /b 1
    )

    call :headers-machine ..\%_SQLITE_OUT%\e_sqlite3.lib

    popd
    echo SQLite static library built successfully.

) else (
    echo SQLite static library already exists, skipping build.
)

:: ============================================================================
:: Build .NET NativeAOT application
:: ============================================================================

dotnet publish src\main\alias.csproj -c Release -r win-%_MACHINE_TYPE% -o target\publish\%_ARCH% || exit /b %errorlevel%

:: ============================================================================
:: Verify output is native code (not IL/CLR)
:: ============================================================================
for %%a in (
    target\publish\%_ARCH%\alias.exe
) do for /f "usebackq delims=" %%b in (`
    set /a %%~za / 1024
`) do (
    call :headers-machine %%a
    echo file '%%~a' size: %%b KB
) & dumpbin.exe /dependents %%~fa | findstr /i /r ^
        /c:"\.CLR_UEF" ^
        /c:"clrjit\.dll" ^
        /c:"coreclr\.dll" ^
        /c:"mscoree\.dll" && echo '%%a' is IL/CLR

popd

doskey.exe vcpkg-shell=
echo Build completed successfully.
echo,

endlocal
goto :eof

:search-sqlite-amalgamation
    for /f "usebackq delims=" %%a in (`
        2^>nul dir /b "sqlite-amalgamation-*"
    `) do if exist "%%a\sqlite3.c" set %~1=%%a
    goto :eof

:headers-machine
    for /f "usebackq delims=" %%a in (`
        dumpbin.exe /headers "%~f1" ^| find "machine"
    `) do echo %~f1: %%a
    goto :eof

:toLower
    for /f "usebackq delims=" %%a in (`
        powershell.exe -Command "'%~1'.ToLower()"
    `) do set %~2=%%a
    goto :eof
