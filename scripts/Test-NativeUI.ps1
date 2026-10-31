param([Parameter(Mandatory)][int]$AppPid, [string]$ArtifactDirectory='docs/evidence/ui-final')
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$taskPreviousWorkflow=$env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID='evoflow-native-final'
$taskResults=[Collections.Generic.List[object]]::new()
$taskScreenshots=[Collections.Generic.List[string]]::new()
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
$taskArtifactPath=(Resolve-Path -LiteralPath $ArtifactDirectory).Path
function Invoke-Checked([string[]]$Arguments){
    $taskOutput=@(& winapp @Arguments)
    if($LASTEXITCODE -ne 0){throw ($taskOutput -join "`n")}
    return $taskOutput -join "`n"
}
function Invoke-UI([string[]]$Arguments,[string]$Window=$script:taskMainWindow){
    Invoke-Checked (@('ui')+$Arguments+@('--on','local','-w',$Window))
}
function Test-UI([string]$Name,[scriptblock]$Action){
    try{ & $Action; $taskResults.Add(@{name=$Name;status='PASS'}) }
    catch{$taskResults.Add(@{name=$Name;status='FAIL';detail=$_.Exception.Message})}
}
function Save-UI([string]$Name,[string]$Window=$script:taskMainWindow){
    $taskPath=Join-Path $taskArtifactPath $Name
    Invoke-UI @('screenshot','--capture-screen','--output',$taskPath,'--json') $Window | Out-Null
    if(-not(Test-Path -LiteralPath $taskPath -PathType Leaf)){throw "Screenshot missing: $taskPath"}
    $taskScreenshots.Add($taskPath)
}
function Find-AppWindow([string]$Title){
    $taskWindows=Invoke-Checked @('ui','list-windows','--on','local','--json') | ConvertFrom-Json
    $taskMatch=@($taskWindows | Where-Object { $_.processId -eq $AppPid -and $_.title -eq $Title })
    if($taskMatch.Count -ne 1){throw "Expected one $Title window in PID $AppPid"}
    return [string]$taskMatch[0].hwnd
}
try{
    $script:taskMainWindow=Find-AppWindow 'EvoFlow'
    Test-UI 'Compact native pill' {
        $taskTree=Invoke-UI @('inspect','--json') | ConvertFrom-Json
        if($taskTree.windows[0].elements[0].width -ne 280 -or $taskTree.windows[0].elements[0].height -ne 44){throw 'Unexpected initial compact size at current 100% scale'}
        Save-UI 'compact.png'
    }
    Test-UI 'Hover expansion and stable pointer traversal' {
        Invoke-UI @('hover','Pill','--dwell-time','500') | Out-Null
        Invoke-UI @('wait-for','TaskText','-t','3000') | Out-Null
        Invoke-UI @('hover','TaskText','--dwell-time','1000') | Out-Null
        Invoke-UI @('wait-for','RunButton','-p','IsOffscreen','--value','False') | Out-Null
        $taskTree=Invoke-UI @('inspect','--json') | ConvertFrom-Json
        if($taskTree.windows[0].elements[0].height -ne 520){throw 'Hover surface collapsed during traversal'}
        Save-UI 'hover.png'
    }
    Test-UI 'Click keeps task panel accessible; no settings form in chat' {
        Invoke-UI @('invoke','PinButton','--action','toggle-on') | Out-Null
        Invoke-UI @('wait-for','TaskText') | Out-Null
        $taskTree=Invoke-UI @('inspect','--interactive','--json')
        if($taskTree -match 'OpenRouterKey|TopmostToggle|HoverToggle'){throw 'Settings form leaked into task panel'}
    }
    Test-UI 'Separate settings window and automatic cloud connection' {
        Invoke-UI @('invoke','SettingsButton') | Out-Null
        $script:taskSettingsWindow=Find-AppWindow 'EvoFlow Settings'
        Invoke-UI @('wait-for','OpenRouterKey') $taskSettingsWindow | Out-Null
        Invoke-UI @('wait-for','ConnectionStatus','--value','connected','--contains','-t','10000') $taskSettingsWindow | Out-Null
        Start-Sleep -Milliseconds 350
        $taskMain=Invoke-UI @('inspect','--json') | ConvertFrom-Json
        if($taskMain.windows[0].elements[0].height -ne 44){throw 'Task panel obscures settings'}
        Save-UI 'settings.png' $taskSettingsWindow
        Invoke-UI @('invoke','CloseSettingsButton') $taskSettingsWindow | Out-Null
    }
    Test-UI 'Native graph uses stored memory' {
        Invoke-UI @('inspect','--json') | Out-Null
        Invoke-UI @('invoke','Pill') | Out-Null
        Invoke-UI @('wait-for','BrainButton','-p','IsOffscreen','--value','False') | Out-Null
        Start-Sleep -Milliseconds 250
        Invoke-UI @('invoke','BrainButton') | Out-Null
        Start-Sleep -Milliseconds 300
        $script:taskMemoryWindow=Find-AppWindow 'EvoFlow · Memory'
        Invoke-UI @('wait-for','MemoryGraphStatus','--value','recorded links','--contains','-t','10000') $taskMemoryWindow | Out-Null
        Invoke-UI @('inspect','--interactive','--json') $taskMemoryWindow | Set-Content -LiteralPath (Join-Path $taskArtifactPath 'memory-tree.json')
        Save-UI 'memory.png' $taskMemoryWindow
    }
    Test-UI 'Graph search, empty state and recovery' {
        Invoke-UI @('set-value','TextBox','evoflow-no-matching-memory-20261030') $taskMemoryWindow | Out-Null
        Invoke-UI @('send-keys','enter','--target','TextBox','--via','send-input') $taskMemoryWindow | Out-Null
        Invoke-UI @('wait-for','MemoryGraphStatus','--value','0 nodes','--contains','-t','5000') $taskMemoryWindow | Out-Null
        Save-UI 'memory-search-empty.png' $taskMemoryWindow
        Invoke-UI @('send-keys','ctrl+a backspace enter','--target','TextBox','--via','send-input') $taskMemoryWindow | Out-Null
        Invoke-UI @('wait-for','MemoryGraphStatus','--value','recorded links','--contains','-t','5000') $taskMemoryWindow | Out-Null
    }
    Test-UI 'Graph zoom controls and history filter' {
        Invoke-UI @('invoke','MemoryZoomIn') $taskMemoryWindow | Out-Null
        Invoke-UI @('invoke','MemoryZoomOut') $taskMemoryWindow | Out-Null
        Invoke-UI @('invoke','MemoryFit') $taskMemoryWindow | Out-Null
        Invoke-UI @('invoke','MemoryHistory') $taskMemoryWindow | Out-Null
        Invoke-UI @('wait-for','MemoryGraphStatus','--value','recorded links','--contains') $taskMemoryWindow | Out-Null
        Invoke-UI @('invoke','MemoryHistory') $taskMemoryWindow | Out-Null
    }
    Test-UI 'Preference management remains separate and cancellable' {
        Invoke-UI @('invoke','MemoryManage') $taskMemoryWindow | Out-Null
        Invoke-UI @('invoke','MemoryManage_preference') $taskMemoryWindow | Out-Null
        Invoke-UI @('wait-for','MemoryEditorKey') $taskMemoryWindow | Out-Null
        Save-UI 'memory-management.png' $taskMemoryWindow
        Invoke-UI @('send-keys','esc','--via','send-input') $taskMemoryWindow | Out-Null
        Invoke-UI @('wait-for','MemoryEditorKey','--gone') $taskMemoryWindow | Out-Null
    }
} catch{
    $taskResults.Add(@{name='Target discovery';status='FAIL';detail=$_.Exception.Message})
} finally{
    try{Invoke-Checked @('ui','yield','--on','local') | Out-Null}finally{$env:WINAPP_UI_WORKFLOW_ID=$taskPreviousWorkflow}
    $taskFailed=@($taskResults | Where-Object status -eq 'FAIL').Count
    [ordered]@{target='local';appPid=$AppPid;mainWindow=$script:taskMainWindow;time=(Get-Date).ToString('o');passed=$taskResults.Count-$taskFailed;failed=$taskFailed;results=@($taskResults.ToArray());screenshots=@($taskScreenshots.ToArray());visual_review='pending'} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskArtifactPath 'test-results.json')
}
Get-Content -LiteralPath (Join-Path $taskArtifactPath 'test-results.json')
if($taskFailed){exit 1}
