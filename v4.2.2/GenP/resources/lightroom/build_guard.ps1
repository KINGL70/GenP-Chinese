[CmdletBinding()]
param([switch]$SelfTest)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'ServiceGuard.cs'
$binary = Join-Path $PSScriptRoot 'LightroomServiceGuard.exe'
$manifest = Join-Path $PSScriptRoot 'guard.ini'
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (!(Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) { $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'The Windows .NET Framework compiler is unavailable.' }
$compilerHash = (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash.ToLowerInvariant()
$cached = @{}
if (Test-Path -LiteralPath $manifest -PathType Leaf) {
    foreach ($line in Get-Content -LiteralPath $manifest) {
        if ($line -match '^([^=]+)=(.+)$') { $cached[$matches[1]] = $matches[2] }
    }
}
$reusable = (Test-Path -LiteralPath $binary -PathType Leaf) -and $cached['SourceSHA256'] -eq $sourceHash -and $cached['CompilerSHA256'] -eq $compilerHash
if ($reusable) { $reusable = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant() -eq $cached['SHA256'] }
if (!$reusable) {
    $pending = Join-Path $PSScriptRoot ('guard-' + [Guid]::NewGuid().ToString('N') + '.exe')
    try {
        $arguments = @('/nologo','/optimize+','/target:exe','/platform:anycpu',('/out:' + $pending))
        foreach ($reference in @('WPF\UIAutomationClient.dll','WPF\UIAutomationTypes.dll','WPF\WindowsBase.dll','System.Windows.Forms.dll','System.Drawing.dll')) {
            $path = Join-Path $framework $reference
            if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing .NET dependency: $path" }
            $arguments += '/reference:' + $path
        }
        & $compiler @arguments $source
        if ($LASTEXITCODE -ne 0) { throw "Dialog guard compilation failed with exit code $LASTEXITCODE." }
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sourceHash) { throw 'Dialog guard source changed during compilation.' }
        Move-Item -LiteralPath $pending -Destination $binary -Force
        $binaryHash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllLines($manifest,[string[]]@('[Guard]',('SHA256=' + $binaryHash),('SourceSHA256=' + $sourceHash),('CompilerSHA256=' + $compilerHash)),[Text.UTF8Encoding]::new($false))
    } finally {
        if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending }
    }
}
Write-Output 'Lightroom dialog guard ready for embedding.'
if ($SelfTest) {
    $testLog = Join-Path ([IO.Path]::GetTempPath()) ('GenP-guard-test-' + [Guid]::NewGuid().ToString('N') + '.log')
    try {
        & $binary '--self-test' $testLog
        if ($LASTEXITCODE -ne 0) { throw 'Dialog guard self-tests failed.' }
    } finally {
        if (Test-Path -LiteralPath $testLog) { Remove-Item -LiteralPath $testLog }
    }
}
