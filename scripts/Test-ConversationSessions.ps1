param([Parameter(Mandatory)][int]$AppPid, [string]$ArtifactDirectory='docs/evidence/ui-conversation-sessions')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'NativeUI-Helpers.ps1')
$previous=$env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID='evoflow-conversation-sessions'
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
$checks=[Collections.Generic.List[string]]::new()
function UI($a,$title='EvoFlow'){Invoke-EvoFlowUI -AppPid $AppPid -Title $title -Arguments $a}
function Nodes($ns){foreach($n in $ns){$n;Nodes $n.children}}
function Tree { $t=UI @('inspect','-d','14','--json') | ConvertFrom-Json; @(Nodes $t.windows.elements) }
function Ready {
    for($attempt=0;$attempt -lt 180;$attempt++){
        $state=@(Tree | Where-Object automationId -eq 'RunButton')
        if($state.Count -eq 1 -and $state[0].isEnabled){return}
        Start-Sleep -Milliseconds 500
    }
    throw 'Conversation did not become ready within the UI deadline'
}
function Send($text){UI @('set-value','TaskText',$text) | Out-Null; UI @('invoke','RunButton') | Out-Null; Ready}
function Save($name){UI @('screenshot','--output',(Join-Path $ArtifactDirectory $name),'--json') | Out-Null}
$report=[ordered]@{target='local';appPid=$AppPid;status='FAIL';checks=@()}
try {
    if(-not (Tree | Where-Object automationId -eq 'TaskText')){UI @('invoke','Pill') | Out-Null}
    Ready
    UI @('invoke','PinButton','--action','toggle-on') | Out-Null
    foreach($id in @('NewChatButton','ChatHistoryButton','BrainButton','SettingsButton')){
        if(-not (Tree | Where-Object automationId -eq $id)){throw "Missing header control $id"}
    }
    UI @('invoke','NewChatButton') | Out-Null
    Ready
    if(Tree | Where-Object {$_.type -eq 'Text' -and $_.name -eq 'You'}){throw 'New Chat retained an old message'}
    $checks.Add('New Chat, History, Memory and Settings header controls; fresh empty transcript')
    Send 'Hi, my name is Omar.'
    $tree=Tree
    if(-not ($tree | Where-Object {$_.type -eq 'Text' -and $_.name -match 'Omar' -and $_.name -ne 'Hi, my name is Omar.'})){throw 'Greeting did not produce a relevant model reply'}
    if($tree | Where-Object name -like '*orders checked*'){throw 'Fictional reconciliation leaked into greeting'}
    $checks.Add('A: natural actual-model greeting; no reconciliation output')
    Send 'What is my name?'
    if(@(Tree | Where-Object {$_.type -eq 'Text' -and $_.name -match 'Omar'}).Count -lt 3){throw 'Follow-up did not recall Omar'}
    Save 'same-chat-context.png'
    $checks.Add('B: follow-up uses current chat context')
    UI @('set-value','TaskText','Prove correctness and give Python code for a longest path in a directed acyclic graph with negative edges, in O(V+E). Include the topological-sort invariant, unreachable targets and a counterexample to greedy Dijkstra.') | Out-Null
    UI @('invoke','RunButton') | Out-Null
    $tree=Tree
    if(-not ($tree | Where-Object {$_.automationId -eq 'NewChatButton' -and $_.isEnabled})){throw 'New Chat disabled during active task'}
    Save 'processing.png'
    UI @('invoke','NewChatButton') | Out-Null
    Ready
    if(Tree | Where-Object {$_.type -eq 'Text' -and $_.name -eq 'You'}){throw 'Old task messages entered new chat'}
    Send 'What name did I tell you earlier?'
    $tree=Tree
    if($tree | Where-Object {$_.type -eq 'Text' -and $_.name -match 'Omar|orders checked|topological|Dijkstra'}){throw 'Prior session response/context leaked into new chat'}
    Save 'new-chat-isolated.png'
    $checks.Add('C: New Chat during a running task isolates messages, output and name context')
    UI @('invoke','ChatHistoryButton') | Out-Null
    $old=@(Tree | Where-Object {$_.automationId -like 'Chat_*' -and $_.name -eq 'Hi, my name is Omar.'})
    if($old.Count -lt 1){throw 'Prior chat not accessible in History'}
    UI @('invoke',$old[0].automationId) | Out-Null
    Ready
    if(-not (Tree | Where-Object name -eq 'What is my name?')){throw 'Reopened chat lost its transcript'}
    Save 'history-reopened.png'
    $checks.Add('History reopens the previous transcript and safely completed old task')
    UI @('invoke','ChatHistoryButton') | Out-Null
    if(-not (Tree | Where-Object automationId -eq 'LegacyTaskHistory')){throw 'Legacy task history unavailable'}
    UI @('send-keys','esc','--via','send-input') | Out-Null
    $checks.Add('Legacy task results remain accessible separately from chat transcripts')
    UI @('invoke','NewChatButton') | Out-Null
    Ready
    UI @('invoke','PinButton','--action','toggle-off') | Out-Null
    $report.status='PASS'
} catch { $report.error=$_.Exception.Message }
finally {
    try { & winapp ui yield --on local | Out-Null }
    catch {$report.cleanupError=$_.Exception.Message}
    $env:WINAPP_UI_WORKFLOW_ID=$previous
}
$report.checks=@($checks.ToArray())
$report | ConvertTo-Json -Depth 5 | Tee-Object -FilePath (Join-Path $ArtifactDirectory 'test-results.json')
if($report.status -ne 'PASS'){exit 1}
