param([string]$SourceDirectory = (Join-Path $env:USERPROFILE '.cua-driver/packages/releases/0.38.0-x86_64-pc-windows-msvc'))
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskTarget = Join-Path $taskRoot 'runtime/cua'
$taskDriver = Join-Path $SourceDirectory 'cua-driver.exe'
$taskExpected = 'ffa58d88779ee219304b0c307f4cfa970fe822f830c6ecf9254eda5c29e22bf2'
if ((Get-FileHash -LiteralPath $taskDriver -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskExpected) { throw 'Cua 0.38.0 source integrity mismatch; inspect the release before changing this pin.' }
if ((& $taskDriver --version) -ne 'cua-driver 0.38.0') { throw 'Unexpected Cua runtime version.' }
New-Item -ItemType Directory -Force -Path $taskTarget | Out-Null
$taskHashes = @{}
foreach ($taskFile in @('cua-driver.exe','cua-driver-uia.exe','cua-cursor-theme.exe')) {
    Copy-Item -LiteralPath (Join-Path $SourceDirectory $taskFile) -Destination (Join-Path $taskTarget $taskFile) -Force
    $taskHashes[$taskFile] = (Get-FileHash -LiteralPath (Join-Path $taskTarget $taskFile)).Hash.ToLowerInvariant()
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/cua-LICENSE.txt') -Destination (Join-Path $taskTarget 'LICENSE.txt') -Force
[ordered]@{ version = '0.38.0'; source_commit = '85e2b1318bd7'; platform = 'x86_64-pc-windows-msvc'; files = $taskHashes; license = 'MIT'; optional_extensions = 'not included' } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskRoot 'config/cua-runtime.json') -Encoding utf8
Write-Output 'Pinned Cua 0.38.0 payload and integrity record synchronized. Shared daemon and agent configurations were not changed.'
