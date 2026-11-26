param([string]$Manifest = (Join-Path $PSScriptRoot 'v4.2.2\Release\build-manifest.json'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$buildRecord = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
if ($buildRecord.schema -ne 1) { throw 'Unsupported build manifest schema.' }
if (@($buildRecord.inputs).Count -eq 0) { throw 'Build manifest has no inputs.' }
$projectPrefix = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
$seenPaths = @{}
foreach ($entry in @($buildRecord.inputs) + @($buildRecord.output)) {
    if ([string]::IsNullOrWhiteSpace($entry.path) -or $entry.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $entry.bytes -isnot [ValueType] -or $entry.bytes -lt 0) { throw 'Invalid build manifest file record.' }
    $entryPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $entry.path))
    if (-not $entryPath.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Manifest path leaves the project: $($entry.path)" }
    if ($seenPaths.ContainsKey($entryPath)) { throw "Duplicate manifest path: $($entry.path)" }
    $seenPaths[$entryPath] = $true
    if (-not (Test-Path -LiteralPath $entryPath -PathType Leaf)) { throw "Missing file: $($entry.path)" }
    $actualHash = (Get-FileHash -LiteralPath $entryPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $entry.sha256 -or (Get-Item -LiteralPath $entryPath).Length -ne $entry.bytes) { throw "Build verification failed: $($entry.path)" }
}
foreach ($requiredInput in @('v4.2.2/GenP/GenP-v4.2.2.au3', 'v4.2.2/GenP/config.ini', 'v4.2.2/build.ps1', 'v4.2.2/run_build.bat', 'verify_build.ps1')) {
    if (-not $seenPaths.ContainsKey([IO.Path]::GetFullPath((Join-Path $PSScriptRoot $requiredInput)))) { throw "Required build input is missing from manifest: $requiredInput" }
}
if ($buildRecord.source -cne 'v4.2.2/GenP/GenP-v4.2.2.au3') { throw 'Unexpected source in build manifest.' }
$outputPath = Join-Path $PSScriptRoot $buildRecord.output.path
$outputBytes = [IO.File]::ReadAllBytes($outputPath)
if ($outputBytes.Length -le 80) { throw 'Executable self-hash trailer is missing.' }
$outputBodyLength = $outputBytes.Length - 80
$outputMagic = [Text.Encoding]::ASCII.GetString($outputBytes, $outputBodyLength, 16)
$recordedBodyHash = [Text.Encoding]::ASCII.GetString($outputBytes, $outputBodyLength + 16, 64)
$hashAlgorithm = [Security.Cryptography.SHA256]::Create()
try { $actualBodyHash = -join ($hashAlgorithm.ComputeHash($outputBytes, 0, $outputBodyLength) | ForEach-Object { $_.ToString('x2') }) } finally { $hashAlgorithm.Dispose() }
if ($outputMagic -cne 'GENP_SELFHASH_01' -or $recordedBodyHash -cne $actualBodyHash) { throw 'Executable self-hash verification failed.' }
Write-Host "Verified $(@($buildRecord.inputs).Count) build inputs and $($buildRecord.output.path)."
Write-Host "Executable SHA256: $($buildRecord.output.sha256)"
