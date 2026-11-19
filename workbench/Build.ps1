#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$NativeOnly,
    [string]$Zig,
    [string]$SafetyPackage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$useZig = $PSBoundParameters.ContainsKey('Zig')
$includeSafety = $PSBoundParameters.ContainsKey('SafetyPackage')
if ($Check -and $NativeOnly) { throw '-Check and -NativeOnly are mutually exclusive.' }
if ($NativeOnly -and $includeSafety) { throw '-SafetyPackage requires a full workbench build.' }
if ($useZig -and [string]::IsNullOrWhiteSpace($Zig)) { throw '-Zig requires an executable path.' }
if ($includeSafety -and [string]::IsNullOrWhiteSpace($SafetyPackage)) { throw '-SafetyPackage requires a directory.' }

function Invoke-Checked([string]$Exe, [string[]]$Arguments, [string]$Label) {
    Write-Host "==> $Label"
    # The pipeline also waits for Windows GUI-subsystem executables such as Ui.Checks.exe.
    & $Exe @Arguments | ForEach-Object { $_ }
    $code = $LASTEXITCODE
    if ($null -eq $code -or $code -ne 0) { throw "$Label failed (exit code: $code)." }
}

function Get-PlainItem([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Refusing reparse point: $Path"
    }
    $item
}

function Ensure-Directory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { [void][IO.Directory]::CreateDirectory($Path) }
    if ((Get-PlainItem $Path) -isnot [IO.DirectoryInfo]) { throw "Expected directory: $Path" }
}

function Assert-File([string]$Path) {
    $item = Get-PlainItem $Path
    if ($item -isnot [IO.FileInfo] -or $item.Length -le 0) { throw "Expected non-empty file: $Path" }
}

function Copy-PackageFiles([string]$Source, [string]$Destination, [string[]]$Names) {
    if ((Get-PlainItem $Source) -isnot [IO.DirectoryInfo]) { throw "Expected directory: $Source" }
    foreach ($name in $Names) {
        $file = Join-Path $Source $name
        Assert-File $file
        [IO.File]::Copy($file, (Join-Path $Destination $name))
    }
}

function Assert-Sha256([string]$Path, [string]$Expected) {
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actual -ne $Expected) { throw "SHA256 mismatch for '$Path': expected $Expected, got $actual." }
}

function Import-SafetyPackage([IO.DirectoryInfo]$Package, [string]$Destination) {
    # Pin the independently validated 20261025-015745 candidate, not a user-supplied trust root.
    $expectedManifest = 'BEC614062B02414D64246221FEBCDD7833C51C740E5B15302F54CB83C276AA4A'
    $manifest = Join-Path $Package.FullName 'SHA256.json'
    Assert-File $manifest
    $stream = [IO.File]::OpenRead($manifest)
    try {
        $hash = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash
        if ($hash -ne $expectedManifest) { throw "Safety manifest SHA256 mismatch: $manifest ($hash)" }
        $stream.Position = 0
        $reader = [IO.StreamReader]::new($stream)
        try { $entries = @(ConvertFrom-Json -InputObject ($reader.ReadToEnd())) }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }

    # Only these top-level files from the pinned manifest may enter the package.
    $runtimeFiles = @(
        'BUILD.json', 'FontBook.exe', 'FontBook.dll', 'FontBook.deps.json', 'FontBook.runtimeconfig.json'
        'SharpGen.Runtime.dll', 'SharpGen.Runtime.COM.dll', 'Vortice.Direct2D1.dll', 'Vortice.DirectX.dll'
        'Vortice.DXGI.dll', 'Vortice.Mathematics.dll', 'THIRD-PARTY-NOTICES.md'
    )
    New-Item -ItemType Directory -Path $Destination | Out-Null
    foreach ($name in $runtimeFiles) {
        $rows = @($entries | Where-Object { $_.file -ceq $name })
        if ($rows.Count -ne 1 -or $rows[0].sha256 -notmatch '^[0-9a-fA-F]{64}$') {
            throw "Safety manifest must contain exactly one valid entry for '$name'."
        }
        $source = Get-PlainItem (Join-Path $Package.FullName $name)
        if ($source -isnot [IO.FileInfo] -or $source.Length -le 0 -or $source.Length -ne $rows[0].bytes) {
            throw "Safety file does not match manifest size: $($source.FullName)"
        }
        Assert-Sha256 $source.FullName $rows[0].sha256
        $target = Join-Path $Destination $name
        [IO.File]::Copy($source.FullName, $target)
        Assert-Sha256 $target $rows[0].sha256
    }
    Write-Host "Imported $($runtimeFiles.Count) verified safety files."
}

function Publish-Workbench([string]$Stage, [string]$Artifacts, [string]$RunId) {
    $final = Join-Path $Artifacts 'workbench-win-x64'
    $previous = Join-Path $Artifacts 'previous'
    $backup = Join-Path $previous "workbench-win-x64-$RunId"
    $prefix = (Get-PlainItem $Artifacts).FullName + [IO.Path]::DirectorySeparatorChar
    foreach ($path in $Stage, $final, $backup) {
        $fullPath = [IO.Path]::GetFullPath($path)
        if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Publish path is outside artifacts: $fullPath"
        }
    }
    $null = Get-PlainItem (Split-Path $Stage -Parent)
    if ((Get-PlainItem $Stage) -isnot [IO.DirectoryInfo]) { throw "Invalid staging directory: $Stage" }

    $archived = $false
    if (Test-Path -LiteralPath $final) {
        if ((Get-PlainItem $final) -isnot [IO.DirectoryInfo]) { throw "Invalid output directory: $final" }
        Ensure-Directory $previous
        [IO.Directory]::Move($final, $backup)
        $archived = $true
        Write-Host "Previous package: $backup"
    }
    # Same-volume rename publishes only complete packages and never merges into an existing directory.
    try { [IO.Directory]::Move($Stage, $final) }
    catch {
        if ($archived -and -not (Test-Path -LiteralPath $final)) {
            try { [IO.Directory]::Move($backup, $final) }
            catch { Write-Warning "Rollback failed; previous package is at '$backup': $_" }
        }
        throw
    }
    Write-Host "Workbench package: $final"
}

$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$artifacts = Join-Path $repo 'artifacts'
$build = Join-Path $artifacts 'build'
$cache = Join-Path $artifacts 'cache'
$native = Join-Path $PSScriptRoot 'native'
$nativeTests = Join-Path $PSScriptRoot 'tests\native'
$desktopBase = Join-Path $PSScriptRoot 'desktop'
$uiBase = Join-Path $PSScriptRoot 'tests\Ui.Checks'
$desktopProject = Join-Path $desktopBase 'FontWorkbench.csproj'
$uiProject = Join-Path $uiBase 'Ui.Checks.csproj'
$nugetConfig = Join-Path $PSScriptRoot 'NuGet.Config'
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')
$runNativeTests = $NativeOnly -or $Check

$safety = $null
if ($includeSafety) {
    $safety = Get-PlainItem $SafetyPackage
    if ($safety -isnot [IO.DirectoryInfo] -or $safety.Name -cne '20261025-015745-win-x64-framework-dependent') {
        throw 'Expected the verified 20261025-015745-win-x64-framework-dependent safety candidate.'
    }
}

$cachePaths = @{
    TEMP = Join-Path $cache 'temp'
    TMP = Join-Path $cache 'temp'
    TMPDIR = Join-Path $cache 'temp'
    DOTNET_CLI_HOME = Join-Path $cache 'dotnet-home'
    NUGET_PACKAGES = Join-Path $cache 'nuget-packages'
    NUGET_HTTP_CACHE_PATH = Join-Path $cache 'nuget-http'
    ZIG_GLOBAL_CACHE_DIR = Join-Path $cache 'zig-global'
    ZIG_LOCAL_CACHE_DIR = Join-Path $cache 'zig-local'
}
$settings = $cachePaths + @{
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_NOLOGO = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
}

$beforeEnv = @{}
Push-Location -LiteralPath $repo
try {
    foreach ($path in @($artifacts, $build, $cache) + @($cachePaths.Values | Select-Object -Unique)) {
        Ensure-Directory $path
    }
    foreach ($name in $settings.Keys) {
        $beforeEnv[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
    }

    $coreDll = Join-Path $build 'FontBookCore.dll'
    $coreLib = Join-Path $build 'FontBookCore.lib'
    $coreExp = Join-Path $build 'FontBookCore.exp'
    $searchExe = Join-Path $build 'search_checks.exe'
    $engineExe = Join-Path $build 'engine_checks.exe'
    $glyphExe = Join-Path $build 'glyph_metadata_checks.exe'
    $sources = @('catalog.cpp', 'selection.cpp', 'render.cpp', 'search.cpp', 'glyph_metadata.cpp', 'font_details.cpp' | ForEach-Object { Join-Path $native $_ })
    $searchSources = @((Join-Path $nativeTests 'search_checks.cpp'), (Join-Path $native 'search.cpp'))
    $engineSource = Join-Path $nativeTests 'engine_checks.cpp'
    $editorSource = Join-Path $nativeTests 'editor_layout_checks.cpp'
    $detailSource = Join-Path $nativeTests 'font_details_checks.cpp'
    $editorExe = Join-Path $build 'editor_layout_checks.exe'
    $detailExe = Join-Path $build 'font_details_checks.exe'
    $glyphSource = Join-Path $nativeTests 'glyph_metadata_checks.cpp'
    foreach ($path in $sources + @(Join-Path $native 'glyph_metadata.h')) { Assert-File $path }
    if ($runNativeTests) {
        foreach ($path in @($searchSources[0], $engineSource, $glyphSource, $editorSource, $detailSource)) { Assert-File $path }
    }

    $cl = $null
    if (-not $useZig) {
        $cl = Get-Command cl.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if ($cl) {
        if ($env:VSCMD_ARG_TGT_ARCH -notin 'x64', 'amd64') {
            throw 'Initialize a Visual Studio x64 developer shell or pass -Zig explicitly.'
        }
        $compiler = $cl.Source
        $flags = @('/nologo', '/std:c++17', '/EHsc', '/W4', '/WX', '/utf-8', '/O2', '/MT', '/permissive-', '/I', $native)
        $link = @('/link', '/MACHINE:X64')
        $coreArgs = $flags + @('/LD', '/DFB_BUILD', "/Fe:$coreDll") + $sources + $link +
            @("/IMPLIB:$coreLib", 'dwrite.lib', 'ole32.lib', 'uuid.lib')
        $searchArgs = $flags + @("/Fe:$searchExe") + $searchSources + $link
        $engineArgs = $flags + @("/Fe:$engineExe", $engineSource) + $link + @($coreLib)
        $editorArgs = $flags + @("/Fe:$editorExe", $editorSource) + $link + @($coreLib)
        $detailArgs = $flags + @("/Fe:$detailExe", $detailSource) + $link + @($coreLib)
        # The standalone test selects the parser-only mode in its own translation unit.
        $glyphArgs = $flags + @("/Fe:$glyphExe", $glyphSource) + $link
    }
    else {
        if (-not $useZig) { $Zig = Join-Path $artifacts 'toolchain\zig-x86_64-windows-0.14.1\zig.exe' }
        if (-not (Test-Path -LiteralPath $Zig -PathType Leaf)) {
            throw "Zig executable not found at '$Zig'. Initialize the Visual Studio x64 environment or pass -Zig."
        }
        $compiler = (Resolve-Path -LiteralPath $Zig).Path
        $version = (Invoke-Checked $compiler @('version') 'Check Zig version' | Out-String).Trim()
        if ($version -ne '0.14.1') { throw "Expected Zig 0.14.1, got '$version'." }
        $flags = @('c++', '-target', 'x86_64-windows-gnu', '-std=c++17', '-O2', '-Wall', '-Wextra', '-Werror', '-static', '-I', $native)
        $coreArgs = $flags + @('-shared', '-DFB_BUILD', '-o', $coreDll, "-Wl,--out-implib,$coreLib") +
            $sources + @('-ldwrite', '-lole32', '-luuid')
        $searchArgs = $flags + @('-o', $searchExe) + $searchSources
        $engineArgs = $flags + @('-o', $engineExe, $engineSource, $coreLib)
        $editorArgs = $flags + @('-o', $editorExe, $editorSource, $coreLib)
        $detailArgs = $flags + @('-o', $detailExe, $detailSource, $coreLib)
        $glyphArgs = $flags + @('-o', $glyphExe, $glyphSource)
    }
    Write-Host "Native compiler: $compiler"

    Push-Location -LiteralPath $build
    try {
        $outputs = @($coreDll, $coreLib, $coreExp)
        if ($runNativeTests) { $outputs += @($searchExe, $engineExe, $glyphExe, $editorExe, $detailExe) }
        foreach ($path in $outputs) {
            if (Test-Path -LiteralPath $path) {
                if ((Get-PlainItem $path) -isnot [IO.FileInfo]) { throw "Invalid native output slot: $path" }
                Remove-Item -LiteralPath $path -Force
            }
        }
        Invoke-Checked $compiler $coreArgs 'Build FontBookCore.dll'
        Assert-File $coreDll
        Assert-File $coreLib
        if ($runNativeTests) {
            Invoke-Checked $compiler $searchArgs 'Build search_checks.exe'
            Invoke-Checked $compiler $engineArgs 'Build engine_checks.exe'
            Invoke-Checked $compiler $glyphArgs 'Build glyph_metadata_checks.exe'
            Invoke-Checked $compiler $editorArgs 'Build native editor layout checks'
            Invoke-Checked $compiler $detailArgs 'Build font metadata checks'
            foreach ($exe in $searchExe, $engineExe, $glyphExe, $editorExe, $detailExe) {
                Assert-File $exe
                Invoke-Checked $exe @() "Run $([IO.Path]::GetFileName($exe))"
            }
        }
    }
    finally { Pop-Location }
    if ($NativeOnly) { return }

    Assert-File $nugetConfig
    $dotnet = (Get-Command dotnet.exe -CommandType Application | Select-Object -First 1).Source
    $sdk = Invoke-Checked $dotnet @('--version') 'Check .NET SDK' | Select-Object -Last 1
    if ("$sdk" -notmatch '^10\.') { throw "Expected .NET SDK 10, got '$sdk'." }
    $restoreArgs = @('--configfile', $nugetConfig, '--nologo', '-p:NuGetAudit=false')
    $releaseArgs = @('-c', 'Release', '--no-restore')
    $releasePath = 'bin\Release\net10.0-windows'
    $desktopOutput = Join-Path $desktopBase $releasePath
    $coreHash = (Get-FileHash -LiteralPath $coreDll -Algorithm SHA256).Hash

    Invoke-Checked $dotnet (@('restore', $desktopProject) + $restoreArgs) 'Restore FontWorkbench offline'
    Invoke-Checked $dotnet (@('build', $desktopProject, '--nologo') + $releaseArgs) 'Build FontWorkbench'

    if ($Check) {
        $bclChecks = @(
            'tests\Collections.Checks\Collections.Checks.csproj'
            'tests\Session.Checks\Session.Checks.csproj'
            'tests\Management.Checks\Management.Checks.csproj'
        )
        foreach ($relative in $bclChecks) {
            $project = Join-Path $PSScriptRoot $relative
            $name = [IO.Path]::GetFileNameWithoutExtension($project)
            Invoke-Checked $dotnet (@('restore', $project) + $restoreArgs) "Restore $name offline"
            Invoke-Checked $dotnet (@('run', '--project', $project) + $releaseArgs) "Run $name"
        }
        Invoke-Checked $dotnet (@('restore', $uiProject) + $restoreArgs) 'Restore Ui.Checks offline'
        Invoke-Checked $dotnet (@('build', $uiProject, '--nologo') + $releaseArgs) 'Build Ui.Checks'

        $uiOutput = Join-Path $uiBase $releasePath
        $null = Get-PlainItem $uiOutput
        $uiExe = Join-Path $uiOutput 'Ui.Checks.exe'
        Assert-File $uiExe
        $uiDll = Join-Path $uiOutput 'FontBookCore.dll'
        if (Test-Path -LiteralPath $uiDll) { $null = Get-PlainItem $uiDll }
        [IO.File]::Copy($coreDll, $uiDll, $true)
        Assert-Sha256 $uiDll $coreHash

        $results = Join-Path $artifacts 'test-results'
        Ensure-Directory $results
        $screenshots = Join-Path $results "Ui.Checks-$runId"
        if (Test-Path -LiteralPath $screenshots) { throw "Screenshot directory already exists: $screenshots" }
        Push-Location -LiteralPath $uiOutput
        try { Invoke-Checked $uiExe @($screenshots) 'Run Ui.Checks' }
        finally { Pop-Location }
        $requiredScreenshots = @(
            'workbench.png', 'compact.png', 'glyphs.png', 'light.png', 'dark.png', 'editor.png', 'grid.png', 'info.png'
            'management-preflight-dark.png', 'management-preflight-light.png', 'system-font-warning.png'
            'aa-100-light-12pt-blur.png', 'aa-150-dark-48pt-focus.png'
        )
        foreach ($name in $requiredScreenshots) { Assert-File (Join-Path $screenshots $name) }
    }

    $stagingRoot = Join-Path $artifacts 'staging'
    Ensure-Directory $stagingRoot
    $stage = Join-Path $stagingRoot "workbench-win-x64-$runId"
    New-Item -ItemType Directory -Path $stage | Out-Null
    # Copy only release inputs we own; bin may contain stale or personal files.
    Copy-PackageFiles $desktopOutput $stage @(
        'FontWorkbench.exe', 'FontWorkbench.dll', 'FontWorkbench.deps.json', 'FontWorkbench.runtimeconfig.json'
        'FontBook.Workbench.Collections.dll', 'Session.dll', 'FontBook.Workbench.Management.dll'
    )
    Copy-PackageFiles $PSScriptRoot $stage @('NOTICE.txt')
    $licenses = Join-Path $stage 'licenses'
    Ensure-Directory $licenses
    Copy-PackageFiles (Join-Path $PSScriptRoot 'licenses') $licenses @(
        'Zig-MIT.txt', 'libcxx.txt', 'libcxxabi.txt', 'libunwind.txt', 'mingw-w64.txt'
    )
    $stagedDll = Join-Path $stage 'FontBookCore.dll'
    [IO.File]::Copy($coreDll, $stagedDll)
    Assert-Sha256 $stagedDll $coreHash
    if ($safety) { Import-SafetyPackage $safety (Join-Path $stage 'safety-center') }
    Publish-Workbench $stage $artifacts $runId
}
finally {
    foreach ($name in $beforeEnv.Keys) {
        $value = $beforeEnv[$name]
        if ($null -eq $value) { $value = [NullString]::Value }
        [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    }
    Pop-Location
}
