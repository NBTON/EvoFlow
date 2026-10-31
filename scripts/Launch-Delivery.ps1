$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$true
if(-not(Get-Command winapp -ErrorAction SilentlyContinue)){throw 'Install WinApp CLI 0.7.1 before launching this native development layout.'}
$taskLayout=Join-Path $PSScriptRoot 'app'
$taskManifest=Join-Path $taskLayout 'AppxManifest.xml'
if(-not(Test-Path -LiteralPath $taskManifest -PathType Leaf)){throw 'The delivery app/AppxManifest.xml is missing.'}
winapp run $taskLayout --manifest $taskManifest --detach --json
if($LASTEXITCODE -ne 0){throw 'Native development layout activation failed. Developer Mode must be enabled.'}
