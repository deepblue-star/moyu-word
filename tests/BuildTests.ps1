[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$parseTokens = $null
$parseErrors = $null
$buildAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $projectRoot 'scripts\build.ps1'), [ref]$parseTokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw 'Build script did not parse.' }
# Import function definitions only. Never execute the build's live packaging/output code.
$definitions = $buildAst.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)
foreach ($definition in $definitions) { . ([scriptblock]::Create($definition.Extent.Text)) }
if (-not (Get-Command Assert-OutputFilesAvailable -ErrorAction SilentlyContinue)) { throw 'Build lacks a non-destructive output-lock preflight.' }
if (-not (Get-Command Publish-PortableDirectory -ErrorAction SilentlyContinue)) { throw 'Build lacks safe portable-directory publication.' }

$fixtureRoot = Join-Path $env:TEMP ('MoyuWord-build-tests-' + [Guid]::NewGuid().ToString('N'))
$outputRoot = Join-Path $fixtureRoot 'dist'
$portable = Join-Path $outputRoot 'MoyuWord-x86'
$stage = Join-Path $outputRoot '.build-test\MoyuWord-x86'
$zip = Join-Path $outputRoot 'MoyuWord-x86.zip'
$sentinel = Join-Path $portable 'a-sentinel.txt'
$lockedPath = Join-Path $portable 'native\z-locked.dll'
$null = New-Item -ItemType Directory -Path (Split-Path $lockedPath -Parent), $stage -Force
[IO.File]::WriteAllText($sentinel, 'old package sentinel')
[IO.File]::WriteAllText($lockedPath, 'old native payload')
[IO.File]::WriteAllText($zip, 'old zip')
[IO.File]::WriteAllText((Join-Path $stage 'new-version.txt'), 'new package')
$passed = 0
try {
    Assert-OutputFilesAvailable @($portable, $zip)
    if ([IO.File]::ReadAllText($sentinel) -ne 'old package sentinel') { throw 'Unlocked preflight changed old output.' }
    $passed++; Write-Host 'PASS unlocked output preflight leaves all files unchanged'

    $held = [IO.File]::Open($lockedPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $rejected = $false
        try { Publish-PortableDirectory $stage $portable } catch { $rejected = $_.Exception.Message -like '*in use or not writable*' }
        if (-not $rejected) { throw 'Locked native file did not block portable publication.' }
        if ([IO.File]::ReadAllText($sentinel) -ne 'old package sentinel') { throw 'Locked publication deleted or changed the sentinel.' }
        if ([IO.File]::ReadAllText($lockedPath) -ne 'old native payload') { throw 'Locked publication changed old payload.' }
        if (-not (Test-Path -LiteralPath (Join-Path $stage 'new-version.txt'))) { throw 'Rejected publication consumed staged output.' }
        $passed++; Write-Host 'PASS locked portable file rejects publication before deleting sentinel or other files'
    } finally { $held.Dispose() }

    $held = [IO.File]::Open($zip, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $rejected = $false
        try { Assert-OutputFilesAvailable @($portable, $zip) } catch { $rejected = $_.Exception.Message -like '*in use or not writable*' }
        if (-not $rejected) { throw 'Locked archive did not block release preflight.' }
        if ([IO.File]::ReadAllText($sentinel) -ne 'old package sentinel') { throw 'Archive lock preflight changed portable output.' }
        if ([IO.File]::ReadAllText($zip) -ne 'old zip') { throw 'Archive lock preflight changed archive output.' }
        $passed++; Write-Host 'PASS locked archive leaves portable directory and archive intact'
    } finally { $held.Dispose() }

    Publish-PortableDirectory $stage $portable
    if ([IO.File]::ReadAllText((Join-Path $portable 'new-version.txt')) -ne 'new package') { throw 'New portable package was not published.' }
    if (Test-Path -LiteralPath $sentinel) { throw 'Old package was not replaced after successful publication.' }
    if (@(Get-ChildItem -LiteralPath $outputRoot -Directory -Filter '.previous-*').Count -ne 0) { throw 'Successful publication left an old backup.' }
    $passed++; Write-Host 'PASS unlocked publication swaps complete directories and cleans only its old backup'
    Write-Host "PASS: $passed build-publication tests"
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $allowedPrefix = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\MoyuWord-build-tests-'
    if (-not $resolvedFixture.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refused unsafe test cleanup.' }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
