[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$parseTokens = $null
$parseErrors = $null
$buildAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $projectRoot 'scripts\build.ps1'), [ref]$parseTokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw 'Build script did not parse.' }
foreach ($definition in $buildAst.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    . ([scriptblock]::Create($definition.Extent.Text))
}
if (-not (Get-Command New-DistributionArchives -ErrorAction SilentlyContinue)) { throw 'Build lacks separate clean installer and portable archives.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fixtureRoot = Join-Path $env:TEMP ('MoyuWord-package-tests-' + [Guid]::NewGuid().ToString('N'))
$outputRoot = Join-Path $fixtureRoot 'dist'
$package = Join-Path $outputRoot 'MoyuWord-x86'
$portableArchive = Join-Path $outputRoot 'portable.zip'
$installerArchive = Join-Path $outputRoot 'installer.zip'
$null = New-Item -ItemType Directory -Path $package -Force
[IO.File]::WriteAllText((Join-Path $package 'MoyuWord.exe'), 'application payload')
$passed = 0
try {
    New-DistributionArchives $package $portableArchive $installerArchive
    $archive = [IO.Compression.ZipFile]::OpenRead($installerArchive)
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        if ($names -notcontains 'MoyuWord.exe') { throw 'Installer archive lost the application.' }
        if (@($names | Where-Object { $_ -eq 'portable.flag' -or $_ -match '^data(/|$)' }).Count -ne 0) { throw 'Installer archive contains portable mode or personal data.' }
        $passed++; Write-Host 'PASS installer archive excludes portable mode and data'
    } finally { $archive.Dispose() }
    $archive = [IO.Compression.ZipFile]::OpenRead($portableArchive)
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        if ($names -notcontains 'MoyuWord.exe' -or $names -notcontains 'portable.flag') { throw 'Portable archive lacks its application or portable marker.' }
        if ($names -notcontains 'data/') { throw 'Portable archive lacks its empty data directory.' }
        if (@($names | Where-Object { $_ -match '^data/.+' }).Count -ne 0) { throw 'Portable archive contains personal data.' }
        $passed++; Write-Host 'PASS portable archive includes its marker and an empty data directory'
    } finally { $archive.Dispose() }
    [IO.File]::WriteAllText((Join-Path $package 'data\private.json'), 'private favorite')
    $rejected = $false
    try { New-DistributionArchives $package (Join-Path $outputRoot 'unsafe-portable.zip') (Join-Path $outputRoot 'unsafe-installer.zip') }
    catch { $rejected = $_.Exception.Message -like '*clean*' }
    if (-not $rejected) { throw 'Archive creation accepted a package containing personal data.' }
    if ((Test-Path -LiteralPath (Join-Path $outputRoot 'unsafe-portable.zip')) -or (Test-Path -LiteralPath (Join-Path $outputRoot 'unsafe-installer.zip'))) { throw 'Rejected package left a distributable archive.' }
    $passed++; Write-Host 'PASS archive creation rejects personalized output before writing any archive'
    Write-Host "PASS: $passed portable-package tests"
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $allowedPrefix = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\MoyuWord-package-tests-'
    if (-not $resolvedFixture.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refused unsafe test cleanup.' }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
