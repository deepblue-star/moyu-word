param([string]$ArchivePath)
$ErrorActionPreference = 'Stop'

# Development-only acquisition. The application and normal build never access this URL.
$releaseUrl = 'https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8057/pdfium-win-x86.tgz'
$archiveSha256 = '242AA2959BE60FB5224011130E98FAB71E83330F1BAD4F728F5D78495624251F'
$dllSha256 = '5D8025AE0F7E501DE11842FEFC0F7B723E50480DB916DBFC5F59BC062F4F7E0F'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$stage = Join-Path $tempBase ('moyu-pdfium-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    if ($ArchivePath) {
        $archive = (Resolve-Path -LiteralPath $ArchivePath).Path
    } else {
        $archive = Join-Path $stage 'pdfium-win-x86.tgz'
        & curl.exe --fail --location --silent --show-error --retry 2 --connect-timeout 30 --max-time 600 $releaseUrl --output $archive
        if ($LASTEXITCODE -ne 0) { throw 'PDFium download failed.' }
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveSha256) {
        throw 'PDFium archive checksum mismatch. Nothing was installed.'
    }
    & tar.exe -xzf $archive -C $stage
    if ($LASTEXITCODE -ne 0) { throw 'PDFium archive extraction failed.' }
    $dll = Join-Path $stage 'bin\pdfium.dll'
    if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ne $dllSha256) {
        throw 'PDFium DLL checksum mismatch. Nothing was installed.'
    }
    $bytes = [IO.File]::ReadAllBytes($dll)
    $peOffset = [BitConverter]::ToInt32($bytes, 60)
    if ([BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x14c) { throw 'Expected x86 PDFium.' }
    $nativeDirectory = Join-Path $repoRoot 'native'
    $noticeDirectory = Join-Path $repoRoot 'third-party\pdfium'
    $licenseDirectory = Join-Path $noticeDirectory 'licenses'
    New-Item -ItemType Directory -Force -Path $nativeDirectory,$noticeDirectory,$licenseDirectory | Out-Null
    Copy-Item -LiteralPath $dll -Destination (Join-Path $nativeDirectory 'pdfium.dll') -Force
    foreach ($name in @('LICENSE', 'VERSION', 'args.gn')) {
        Copy-Item -LiteralPath (Join-Path $stage $name) -Destination (Join-Path $noticeDirectory $name) -Force
    }
    Get-ChildItem -LiteralPath (Join-Path $stage 'licenses') -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licenseDirectory $_.Name) -Force
    }
    Write-Output 'Verified PDFium 155.0.8057.0, Windows x86, V8/XFA disabled.'
    Write-Output ('SHA256 native/pdfium.dll: ' + $dllSha256)
} finally {
    # Resolve and constrain the exact generated staging directory before recursive removal.
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $requiredPrefix = $tempBase.TrimEnd('\') + '\moyu-pdfium-'
    if ($resolvedStage.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedStage)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
