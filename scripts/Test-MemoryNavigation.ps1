param([Parameter(Mandatory)][int]$AppPid, [string]$OutputDirectory='docs/evidence/ui-continuation')
$ErrorActionPreference='Stop'
$previous=$env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID='evoflow-native-final'
. (Join-Path $PSScriptRoot 'NativeUI-Helpers.ps1')
$windowTitle='EvoFlow'
function UI([string[]]$Arguments) {
    Invoke-EvoFlowUI -AppPid $AppPid -Title $windowTitle -Arguments $Arguments
}
function Nodes($items) { foreach($item in $items){ $item; Nodes $item.children } }
$report=[ordered]@{target='local';appPid=$AppPid;time=(Get-Date).ToString('o');status='FAIL';checks=@()}
try {
    $windows=@(& winapp ui list-windows --on local -a $AppPid --json | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0){throw 'Window discovery failed'}
    $Window=[string]($windows | Where-Object title -eq 'EvoFlow').hwnd
    if(-not $Window){throw 'Main window not found'}
    $tree=UI @('inspect','--json') | ConvertFrom-Json
    if($tree.windows[0].elements[0].height -lt 100){UI @('invoke','Pill') | Out-Null}
    UI @('invoke','BrainButton') | Out-Null
    $windows=@(& winapp ui list-windows --on local -a $AppPid --json | ConvertFrom-Json)
    if($LASTEXITCODE -ne 0){throw 'Memory discovery failed'}
    $Window=[string]($windows | Where-Object title -eq 'EvoFlow · Memory').hwnd
    if(-not $Window){throw 'Memory window not found'}
    $report.window=$Window
    $windowTitle='EvoFlow · Memory'
    UI @('set-value','TextBox','shipping-policy') | Out-Null
    UI @('send-keys','enter','--target','TextBox','--via','send-input') | Out-Null
    UI @('wait-for','MemoryGraphStatus','--value','1 nodes','--contains','-t','3000') | Out-Null
    $report.checks += 'Search shipping-policy: exactly 1 matching node'
    UI @('send-keys','ctrl+a backspace enter','--target','TextBox','--via','send-input') | Out-Null
    UI @('focus','MemoryTypeFilter') | Out-Null
    UI @('send-keys','home down','--target','MemoryTypeFilter','--via','send-input') | Out-Null
    UI @('wait-for','MemoryTypeFilter','--value','Knowledge','-t','3000') | Out-Null
    UI @('wait-for','MemoryGraphStatus','--value','1 nodes · 0 recorded links','--contains','-t','3000') | Out-Null
    $report.checks += 'Knowledge filter: exactly 1 node, 0 links'
    UI @('send-keys','home','--target','MemoryTypeFilter','--via','send-input') | Out-Null
    UI @('wait-for','MemoryTypeFilter','--value','All types','-t','3000') | Out-Null
    $tree=UI @('inspect','--json') | ConvertFrom-Json
    $knowledge=@(Nodes $tree.windows.elements | Where-Object automationId -like 'MemoryList_knowledge:*')
    if($knowledge.Count -ne 1){throw 'Expected one saved knowledge list item'}
    UI @('invoke',$knowledge[0].automationId,'--action','select') | Out-Null
    $tree=UI @('inspect','--json') | ConvertFrom-Json
    $relationship=@(Nodes $tree.windows.elements | Where-Object { $_.automationId -like 'MemoryRelationship_*' -and $_.name -like '*sourced from*' })
    if($relationship.Count -ne 1){throw 'Expected one stored source relationship'}
    $report.checks += 'Selected knowledge: stored source relationship populated'
    UI @('invoke',$relationship[0].automationId) | Out-Null
    UI @('wait-for','Source reference','-t','3000') | Out-Null
    $report.checks += 'Relationship invocation: source inspector displayed'
    UI @('screenshot','--output',(Join-Path $OutputDirectory 'memory-source.png'),'--json') | Out-Null
    $report.status='PASS'
} catch { $report.error=$_.Exception.Message }
finally {
    try { & winapp ui yield --on local | Out-Null; if($LASTEXITCODE -ne 0){throw 'UI yield failed'} }
    catch { $report.status='FAIL'; $report.cleanupError=$_.Exception.Message }
    $env:WINAPP_UI_WORKFLOW_ID=$previous
}
$report | ConvertTo-Json -Depth 5 | Tee-Object -FilePath (Join-Path $OutputDirectory 'memory-navigation.json')
if($report.status -ne 'PASS'){exit 1}
