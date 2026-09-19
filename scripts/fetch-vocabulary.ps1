# Development-only: download and normalize a pinned, real TEM-4 vocabulary.
# The installed application never invokes this script or accesses the network.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$commit = '3992bcb94c800a2fd38a9fd6ff95b2353e755363'
$blobSha = 'bc12756e6f7b820aab9256cb88753cd0ce960fc3'
$sourcePath = 'book/1521164653685_Level4_2.zip'
$sourceUrl = "https://raw.githubusercontent.com/kajweb/dict/$commit/$sourcePath"
$archivePath = Join-Path $repo '.cache/vocabulary-Level4_2.zip'
$assetPath = Join-Path $repo 'assets/tem4.json'
[IO.Directory]::CreateDirectory((Split-Path $archivePath)) | Out-Null
[IO.Directory]::CreateDirectory((Split-Path $assetPath)) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $repo 'third-party')) | Out-Null
if (-not (Test-Path -LiteralPath $archivePath)) {
    $temporaryArchive = "$archivePath.download"
    # Small HTTP ranges tolerate networks which reset long downloads. Every part
    # is length-checked and the complete archive is checked against its Git hash.
    $expectedBytes = 1706442
    $output = [IO.File]::Create($temporaryArchive)
    try {
        for ($start = 0; $start -lt $expectedBytes; $start += 262144) {
            $end = [Math]::Min($start + 262143, $expectedBytes - 1)
            $partPath = "$archivePath.part"
            & curl.exe --silent --show-error --location --fail --retry 3 --retry-all-errors --connect-timeout 15 --max-time 60 --range "$start-$end" --output $partPath "$sourceUrl`?part=$start"
            if ($LASTEXITCODE -ne 0) { throw "Download failed at $start-$end. Retry scripts/fetch-vocabulary.ps1. curl exit $LASTEXITCODE" }
            $part = [IO.File]::ReadAllBytes($partPath)
            if ($part.Length -ne ($end - $start + 1)) { throw "Unexpected HTTP range length at $start-$end." }
            $output.Write($part, 0, $part.Length)
        }
    } finally { $output.Dispose() }
    Move-Item -LiteralPath $temporaryArchive -Destination $archivePath -Force
    Remove-Item -LiteralPath "$archivePath.part" -ErrorAction SilentlyContinue
}
$bytes = [IO.File]::ReadAllBytes($archivePath)
$gitHeader = [Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0")
$gitBlob = New-Object byte[] ($gitHeader.Length + $bytes.Length)
[Array]::Copy($gitHeader, 0, $gitBlob, 0, $gitHeader.Length)
[Array]::Copy($bytes, 0, $gitBlob, $gitHeader.Length, $bytes.Length)
$sha1 = [Security.Cryptography.SHA1]::Create()
$actualBlobSha = [BitConverter]::ToString($sha1.ComputeHash($gitBlob)).Replace('-', '').ToLowerInvariant()
$sha1.Dispose()
if ($actualBlobSha -ne $blobSha) { throw "Vocabulary integrity mismatch: expected Git blob $blobSha, got $actualBlobSha. Remove the incorrect vocabulary archive and retry." }
$sha256 = [Security.Cryptography.SHA256]::Create()
$sourceSha256 = [BitConverter]::ToString($sha256.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()

Add-Type -AssemblyName System.IO.Compression
$memory = New-Object IO.MemoryStream(,$bytes)
$zip = New-Object IO.Compression.ZipArchive($memory)
$entry = @($zip.Entries | Where-Object { $_.Name -eq 'Level4_2.json' })
if ($entry.Count -ne 1) { throw 'Pinned archive must contain exactly one Level4_2.json.' }
$reader = New-Object IO.StreamReader($entry[0].Open(), [Text.Encoding]::UTF8)
$words = New-Object 'Collections.Generic.List[object]'
$keys = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$rawCount = 0
$withExamples = 0
try {
    while (($line = $reader.ReadLine()) -ne $null) {
        if ([String]::IsNullOrWhiteSpace($line)) { continue }
        $record = ConvertFrom-Json -InputObject $line
        $rawCount++
        $content = $record.content.word.content
        $english = ([string]$record.headWord).Trim()
        $translations = @($content.trans | ForEach-Object { ([string]$_.tranCn).Trim() } | Where-Object { $_ })
        if (-not $english -or $translations.Count -eq 0) { throw "Missing headword/Chinese at source row $rawCount." }
        if (-not $keys.Add($english)) { throw "Unexpected duplicate source headword: $english" }
        $parts = @($content.trans | ForEach-Object { ([string]$_.pos).Trim() } | Where-Object { $_ } | Select-Object -Unique)
        $phonetic = [string]$content.usphone
        if (-not $phonetic) { $phonetic = [string]$content.ukphone }
        if (-not $phonetic) { $phonetic = [string]$content.phone }
        $sentence = @($content.sentence.sentences | Where-Object { $_.sContent } | Select-Object -First 1)
        $example = ''; $exampleChinese = ''
        if ($sentence.Count -gt 0) { $example = [string]$sentence[0].sContent; $exampleChinese = [string]$sentence[0].sCn; $withExamples++ }
        $words.Add([ordered]@{
            Id = 'tem4:' + $english.ToLowerInvariant(); English = $english
            Chinese = $translations -join '；'; PartOfSpeech = $parts -join ' / '
            Phonetic = $phonetic; Example = $example; ExampleChinese = $exampleChinese
        })
    }
} finally { $reader.Dispose(); $zip.Dispose(); $memory.Dispose() }
if ($rawCount -ne 4025 -or $words.Count -ne 4025) { throw "Expected 4025 TEM-4 words, got $rawCount source rows / $($words.Count) normalized words." }
$library = [ordered]@{
    Id = 'builtin-tem4'; Name = '专四核心词汇 · TEM-4'
    Source = "kajweb/dict @ $commit; Level4_2; original Youdao data; license not specified"
    Words = $words.ToArray()
}
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($assetPath, (($library | ConvertTo-Json -Depth 8) + "`n"), $utf8)
$assetSha256 = [BitConverter]::ToString($sha256.ComputeHash([IO.File]::ReadAllBytes($assetPath))).Replace('-', '').ToLowerInvariant()
$sha256.Dispose()
$provenance = [ordered]@{
    repository = 'https://github.com/kajweb/dict'; commit = $commit
    sourcePath = $sourcePath; downloadUrl = $sourceUrl; gitBlobSha1 = $blobSha
    archiveSha256 = $sourceSha256; archiveBytes = $bytes.Length
    originalUrl = 'http://ydschool-online.nos.netease.com/1521164653685_Level4_2.zip'
    upstreamName = '专四核心词汇（正序版）'; upstreamId = 'Level4_2'
    sourceRecords = $rawCount; outputWords = $words.Count; wordsWithExamples = $withExamples
    assetSha256 = $assetSha256
    rights = 'Upstream says data was crawled from the Youdao vocabulary app. No LICENSE or explicit redistribution grant was present in the pinned repository. No open license is asserted.'
}
[IO.File]::WriteAllText((Join-Path $repo 'third-party/vocabulary-provenance.json'), (($provenance | ConvertTo-Json -Depth 5) + "`n"), $utf8)
Write-Host "TEM-4: $($words.Count) words; $withExamples with source examples."
Write-Host "Archive SHA256: $sourceSha256"
Write-Host "Asset SHA256: $assetSha256"
