param([switch]$SkipBuild)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$true
$taskRoot=Split-Path $PSScriptRoot -Parent
Push-Location -LiteralPath $taskRoot
try {
    if(-not $SkipBuild){
        dotnet build native/EvoFlow/EvoFlow.csproj -p:Platform=x64
        if($LASTEXITCODE -ne 0){throw 'Native build failed'}
        winapp run native/EvoFlow/EvoFlow.csproj --no-build --arch x64 -p Platform=x64 --no-launch --json
        if($LASTEXITCODE -ne 0){throw 'WinApp development staging failed'}
    }
    $taskSource=Join-Path $taskRoot 'native/EvoFlow/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/AppX'
    foreach($relative in @('AppxManifest.xml','EvoFlow.exe','tools/EvoFlow.ToolHost.exe','runtime/python.exe','runtime/cua/cua-driver.exe','config/cua-runtime.json','worker/evoflow_worker/document_handler.py')){
        if(-not(Test-Path -LiteralPath (Join-Path $taskSource $relative) -PathType Leaf)){throw "Missing staged payload: $relative"}
    }
    $taskDelivery=Join-Path $taskRoot ('delivery/EvoFlow-local-tools-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
    $taskLayout=Join-Path $taskDelivery 'app'
    New-Item -ItemType Directory -Path $taskLayout -Force | Out-Null
    Get-ChildItem -LiteralPath $taskSource -Force | Copy-Item -Destination $taskLayout -Recurse
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Launch-Delivery.ps1') -Destination (Join-Path $taskDelivery 'Launch.ps1')
    @'
Native EvoFlow development layout with embedded Python and self-contained .NET tools.
Launch: .\Launch.ps1 (Windows Developer Mode and WinApp CLI 0.7.1 required).
This is a development layout, not a signed installer. No machine-wide Python is used.
Provider credentials and device session remain in Windows-protected local app storage.
'@ | Set-Content -LiteralPath (Join-Path $taskDelivery 'README.txt') -Encoding utf8
    $taskHashes=@{}
    foreach($relative in @('EvoFlow.exe','EvoFlow.dll','EvoFlow.Connectors.dll','tools/EvoFlow.ToolHost.exe','tools/EvoFlow.ToolHost.dll','runtime/python.exe','runtime/cua/cua-driver.exe','config/cua-runtime.json','worker/evoflow_worker/local_tools.py','worker/evoflow_worker/document_handler.py')){
        $taskHashes[$relative]=(Get-FileHash -LiteralPath (Join-Path $taskLayout $relative)).Hash
    }
    [ordered]@{kind='native-development-layout';layout=$taskLayout;hashes=$taskHashes;created=(Get-Date).ToString('o')} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskDelivery 'delivery.json')
    Write-Output $taskDelivery
} finally {Pop-Location}
