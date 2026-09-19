[CmdletBinding()]
param([switch]$SkipInstaller)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = Join-Path $projectRoot 'dist'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'

function Assert-File([string]$Path, [string]$Message) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Message`nExpected: $Path" }
}
function Assert-X86([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw "Not a PE file: $Path" }
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x014c) {
            throw "Expected x86 PE machine 0x014c: $Path"
        }
    } finally { $reader.Dispose(); $stream.Dispose() }
}
function Assert-NoReparse([string]$Path) {
    $currentPath = [IO.Path]::GetFullPath($Path)
    while ($currentPath) {
        if (Test-Path -LiteralPath $currentPath) {
            if (((Get-Item -LiteralPath $currentPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Build output path cannot contain a junction or symbolic link: $currentPath"
            }
        }
        $parentDirectory = [IO.Directory]::GetParent($currentPath)
        $currentPath = if ($parentDirectory) { $parentDirectory.FullName } else { $null }
    }
}
function Remove-BuildDirectory([string]$Path) {
    $resolvedTarget = [IO.Path]::GetFullPath($Path)
    $allowedPrefix = [IO.Path]::GetFullPath($outputRoot).TrimEnd('\') + '\'
    if (-not $resolvedTarget.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory outside dist: $resolvedTarget"
    }
    Assert-NoReparse $resolvedTarget
    if (Test-Path -LiteralPath $resolvedTarget) { Remove-Item -LiteralPath $resolvedTarget -Recurse -Force }
}
function Assert-OutputFilesAvailable([string[]]$Paths) {
    $files = New-Object 'Collections.Generic.List[string]'
    $directories = New-Object 'Collections.Generic.Stack[string]'
    $heldFiles = New-Object 'Collections.Generic.List[System.IDisposable]'
    try {
        foreach ($path in $Paths) {
            Assert-NoReparse $path
            if (Test-Path -LiteralPath $path -PathType Container) { $directories.Push([IO.Path]::GetFullPath($path)) }
            elseif (Test-Path -LiteralPath $path -PathType Leaf) { $files.Add([IO.Path]::GetFullPath($path)) }
        }
        while ($directories.Count -gt 0) {
            $directory = $directories.Pop()
            foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Build outputs cannot contain a junction or symbolic link: $($item.FullName)"
                }
                if ($item.PSIsContainer) { $directories.Push($item.FullName) }
                else { $files.Add($item.FullName) }
            }
        }
        # Keep every successful exclusive-open alive until the entire preflight passes.
        # A mapped EXE/DLL or any other locked file is rejected before any old file is removed.
        foreach ($file in ($files | Sort-Object)) {
            try { $heldFiles.Add([IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)) }
            catch { throw "Release output is in use or not writable: $file. Close the portable app, installer, or file viewer and retry. Existing outputs were not changed." }
        }
    } finally { foreach ($heldFile in $heldFiles) { $heldFile.Dispose() } }
}
function Publish-PortableDirectory([string]$Stage, [string]$Destination) {
    $resolvedStage = [IO.Path]::GetFullPath($Stage)
    $resolvedDestination = [IO.Path]::GetFullPath($Destination)
    $allowedPrefix = [IO.Path]::GetFullPath($outputRoot).TrimEnd('\') + '\'
    foreach ($path in @($resolvedStage, $resolvedDestination)) {
        if (-not $path.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Portable publication path must be inside dist: $path" }
        Assert-NoReparse $path
    }
    if (-not (Test-Path -LiteralPath $resolvedStage -PathType Container)) { throw "Staged portable package is missing: $resolvedStage" }
    Assert-OutputFilesAvailable @($resolvedDestination)
    $previous = Join-Path $outputRoot ('.previous-' + [Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $resolvedDestination) { [IO.Directory]::Move($resolvedDestination, $previous) }
    try { [IO.Directory]::Move($resolvedStage, $resolvedDestination) }
    catch {
        if ((Test-Path -LiteralPath $previous -PathType Container) -and -not (Test-Path -LiteralPath $resolvedDestination)) {
            [IO.Directory]::Move($previous, $resolvedDestination)
        }
        throw
    }
    if (Test-Path -LiteralPath $previous -PathType Container) {
        try { Assert-OutputFilesAvailable @($previous); Remove-BuildDirectory $previous }
        catch { Write-Warning "New portable output is complete. Old backup could not be removed and was retained at: $previous" }
    }
}
function Invoke-Compiler([string[]]$Arguments) {
    & $compiler @Arguments
    if ($LASTEXITCODE -ne 0) { throw "C# compiler failed with exit code $LASTEXITCODE." }
}

Assert-File $compiler 'The Windows .NET Framework C# compiler is missing. Build on Windows with .NET Framework 4.6 or later enabled.'
Assert-File (Join-Path $projectRoot 'native\pdfium.dll') 'Bundled x86 PDFium is missing. Restore native/pdfium.dll and its third-party notices; this script never downloads runtime dependencies.'
Assert-File (Join-Path $projectRoot 'assets\tem4.json') 'The bundled TEM-4 dictionary is missing. Restore assets/tem4.json before packaging.'
Assert-File (Join-Path $projectRoot 'src\app.manifest') 'Application manifest is missing.'
Assert-File (Join-Path $projectRoot 'src\MoyuWord.exe.config') 'Application runtime config is missing.'
Assert-X86 (Join-Path $projectRoot 'native\pdfium.dll')
$null = Get-Content -LiteralPath (Join-Path $projectRoot 'assets\tem4.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' -File | Sort-Object Name | ForEach-Object FullName)
if ($sourceFiles.Count -eq 0) { throw 'No C# source files were found in src/.' }

Assert-NoReparse $outputRoot
$null = New-Item -ItemType Directory -Path $outputRoot -Force
$stageRoot = Join-Path $outputRoot ('.build-' + [Guid]::NewGuid().ToString('N'))
$packageStage = Join-Path $stageRoot 'MoyuWord-x86'
$portablePath = Join-Path $outputRoot 'MoyuWord-x86'
$zipPath = Join-Path $outputRoot 'MoyuWord-x86.zip'
$setupPath = Join-Path $outputRoot 'MoyuWord-Setup-x86.exe'
$releaseTargets = @($portablePath, $zipPath)
if (-not $SkipInstaller) { $releaseTargets += $setupPath }
Assert-OutputFilesAvailable $releaseTargets
$null = New-Item -ItemType Directory -Path $packageStage -Force
try {
    $appArgs = @('/nologo', '/target:winexe', '/platform:x86', '/optimize+', '/langversion:5', '/main:MoyuWord.Program',
        ('/out:' + (Join-Path $packageStage 'MoyuWord.exe')), ('/win32manifest:' + (Join-Path $projectRoot 'src\app.manifest')))
    $iconPath = Join-Path $projectRoot 'assets\moyu.ico'
    if (Test-Path -LiteralPath $iconPath -PathType Leaf) { $appArgs += '/win32icon:' + $iconPath }
    foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Xml.dll')) {
        $appArgs += '/reference:' + (Join-Path $framework $reference)
    }
    foreach ($reference in @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll')) {
        $appArgs += '/reference:' + (Join-Path (Join-Path $framework 'WPF') $reference)
    }
    Invoke-Compiler ($appArgs + $sourceFiles)
    Copy-Item -LiteralPath (Join-Path $projectRoot 'src\MoyuWord.exe.config') -Destination (Join-Path $packageStage 'MoyuWord.exe.config')
    foreach ($directory in @('assets', 'native', 'third-party', 'examples')) {
        $sourceDirectory = Join-Path $projectRoot $directory
        if (Test-Path -LiteralPath $sourceDirectory -PathType Container) { Copy-Item -LiteralPath $sourceDirectory -Destination (Join-Path $packageStage $directory) -Recurse }
    }
    foreach ($document in @('README.md', 'LICENSE')) {
        $sourceDocument = Join-Path $projectRoot $document
        if (Test-Path -LiteralPath $sourceDocument -PathType Leaf) { Copy-Item -LiteralPath $sourceDocument -Destination (Join-Path $packageStage $document) }
    }
    $packageDocs = Join-Path $packageStage 'docs'
    $null = New-Item -ItemType Directory -Path $packageDocs -Force
    foreach ($document in @('packaging.md', 'vocabulary.md', 'verification.md')) {
        $sourceDocument = Join-Path (Join-Path $projectRoot 'docs') $document
        if (Test-Path -LiteralPath $sourceDocument -PathType Leaf) { Copy-Item -LiteralPath $sourceDocument -Destination (Join-Path $packageDocs $document) }
    }
    $setupArgs = @('/nologo', '/target:winexe', '/platform:x86', '/optimize+', '/langversion:5', '/main:MoyuWord.Setup.Program',
        ('/win32manifest:' + (Join-Path $projectRoot 'src\app.manifest')))
    if (Test-Path -LiteralPath $iconPath -PathType Leaf) { $setupArgs += '/win32icon:' + $iconPath }
    foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll', 'Microsoft.CSharp.dll')) {
        $setupArgs += '/reference:' + (Join-Path $framework $reference)
    }
    $setupSource = Join-Path $projectRoot 'installer\Setup.cs'
    Assert-File $setupSource 'Installer source is missing.'
    Invoke-Compiler ($setupArgs + @('/define:UNINSTALLER', ('/out:' + (Join-Path $packageStage 'Uninstall.exe')), $setupSource))
    Copy-Item -LiteralPath (Join-Path $projectRoot 'src\MoyuWord.exe.config') -Destination (Join-Path $packageStage 'Uninstall.exe.config')
    Assert-X86 (Join-Path $packageStage 'MoyuWord.exe')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stageZip = Join-Path $stageRoot 'MoyuWord-x86.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($packageStage, $stageZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    if (-not $SkipInstaller) {
        $stageSetup = Join-Path $stageRoot 'MoyuWord-Setup-x86.exe'
        Invoke-Compiler ($setupArgs + @(('/resource:' + $stageZip + ',MoyuWord.Package.zip'), ('/out:' + $stageSetup), $setupSource))
        Assert-X86 $stageSetup
    }
    # Check all current outputs again immediately before publication, then swap the
    # complete portable directory instead of deleting its individual files first.
    Assert-OutputFilesAvailable $releaseTargets
    Publish-PortableDirectory $packageStage $portablePath
    Move-Item -LiteralPath $stageZip -Destination $zipPath -Force
    if (-not $SkipInstaller) { Move-Item -LiteralPath $stageSetup -Destination $setupPath -Force }
    Write-Host "Portable directory: $portablePath"
    Write-Host "Portable zip:       $zipPath"
    if (-not $SkipInstaller) { Write-Host "Offline installer:  $setupPath" }
    Get-FileHash -LiteralPath $zipPath -Algorithm SHA256 | Format-Table -AutoSize
    if (-not $SkipInstaller) { Get-FileHash -LiteralPath $setupPath -Algorithm SHA256 | Format-Table -AutoSize }
} finally {
    Remove-BuildDirectory $stageRoot
}
