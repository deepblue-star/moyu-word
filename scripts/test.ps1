[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testOutput = Join-Path $projectRoot 'build\tests'
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
$compilerPath = Join-Path $frameworkPath 'csc.exe'
$null = New-Item -ItemType Directory -Force -Path $testOutput
$references = @('System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath $_) }
$references += @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkPath ('WPF\' + $_)) }
foreach ($directory in @('assets', 'native')) {
    $source = Join-Path $projectRoot $directory
    $destination = Join-Path $testOutput $directory
    $null = New-Item -ItemType Directory -Force -Path $destination
    Get-ChildItem -LiteralPath $source -File | Copy-Item -Destination $destination -Force
}
$baseArgs = @('/nologo','/target:exe','/platform:x86','/langversion:5') + $references
Push-Location $projectRoot
try {
    foreach ($test in @('Store','StudyProgress','Portable','Pdf','Ui','StudyUi','PdfUi','Package')) {
        $outFile = Join-Path $testOutput ($test + 'Tests.exe')
        $sourceList = switch ($test) {
            'Store' { @('src\Models.cs','src\LibraryStore.cs','tests\StoreTests.cs') }
            'StudyProgress' { @('src\Models.cs','src\LibraryStore.cs','tests\StudyProgressTests.cs') }
            'Portable' { @('src\Models.cs','src\LibraryStore.cs','tests\PortableTests.cs') }
            'Pdf' { @('src\PdfDocument.cs','tests\PdfTests.cs') }
            'Ui' { @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName) + @('tests\UiTests.cs') }
            'StudyUi' { @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName) + @('tests\StudyUiTests.cs') }
            'PdfUi' { @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName) + @('tests\PdfUiTests.cs') }
            'Package' { @('installer\Setup.cs', 'tests\PackageTests.cs') }
        }
        $compileArgs = $baseArgs + @('/out:' + $outFile) + $sourceList
        if ($test -eq 'Ui') { $compileArgs += '/main:UiTests' }
        if ($test -eq 'StudyUi') { $compileArgs += '/main:StudyUiTests' }
        if ($test -eq 'PdfUi') { $compileArgs += '/main:PdfUiTests' }
        if ($test -eq 'Package') { $compileArgs += '/main:PackageTests' }
        & $compilerPath @compileArgs
        if ($LASTEXITCODE -ne 0) { throw "$test tests failed to compile" }
        & $outFile
        if ($LASTEXITCODE -ne 0) { throw "$test tests failed" }
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $projectRoot 'tests\BuildTests.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Build safety tests failed' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $projectRoot 'tests\PortablePackageTests.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Portable package tests failed' }
} finally { Pop-Location }
