@echo off
setlocal
set "VC_VARS_PATH=%ProgramFiles(x86)%\Microsoft Visual Studio"
set SQLITE_URL=https://sqlite.org/2025/sqlite-amalgamation-3510100.zip

pushd "%~dp0"

set SQLITE_SRC=
call :search-sqlite-amalgamation SQLITE_SRC

if not defined SQLITE_SRC PowerShell.exe ^
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
    -ArgumentList nul %SQLITE_URL% .

call :search-sqlite-amalgamation SQLITE_SRC
if not defined SQLITE_SRC (
    >&2 echo sqlite-amalgamation source directory not found
    exit /b 1
)

if not exist "%VC_VARS_PATH%\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" (
    >&2 echo Visual Studio Build Tools not found in "%VC_VARS_PATH%"
    exit /b 1
)

:: Initialize Visual Studio build environment
>nul call "%VC_VARS_PATH%\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"

:: ============================================================================
:: Build SQLite static library
:: ============================================================================

:: Check if SQLite static library needs to be built
if not exist "%SQLITE_SRC%\e_sqlite3.lib" (
    echo Building SQLite static library...

    :: Compile static library
    pushd "%SQLITE_SRC%"

    :: Compile SQLite with static CRT (/MT)
    cl /c ^
        /O2 ^
        /MT ^
        /DSQLITE_ENABLE_FTS5 ^
        /DSQLITE_ENABLE_RTREE ^
        /DSQLITE_ENABLE_JSON1 ^
        /DSQLITE_THREADSAFE=1 ^
        sqlite3.c || (
        >&2 echo Failed to compile SQLite
            exit /b 1
        )

    :: Create static library (named e_sqlite3.lib to match SQLitePCLRaw P/Invoke name)
    lib /OUT:e_sqlite3.lib sqlite3.obj || (
        >&2 echo Failed to create SQLite static library
        exit /b 1
    )

    popd
    echo SQLite static library built successfully.

) else (
    echo SQLite static library already exists, skipping build.
)

:: ============================================================================
:: Build .NET NativeAOT application
:: ============================================================================
dotnet publish src\main\alias.csproj -c Release -o bin\publish || exit /b %errorlevel%

:: ============================================================================
:: Verify output is native code (not IL/CLR)
:: ============================================================================
dumpbin /dependents bin\publish\alias.exe | findstr /i /r ^
        /c:"\.CLR_UEF" ^
        /c:"clrjit\.dll" ^
        /c:"coreclr\.dll" ^
        /c:"mscoree\.dll" && (
            >&2 echo [ERROR] 'alias.exe' is IL/CLR
            exit /b 1
        )

popd

doskey.exe vcpkg-shell=
echo Build completed successfully.

endlocal
goto :eof

:search-sqlite-amalgamation
    for /f "usebackq delims=" %%a in (`
        2^>nul dir /b "sqlite-amalgamation-*"
    `) do if exist "%%a\sqlite3.c" set %~1=%%a
    goto :eof