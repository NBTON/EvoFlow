param([Parameter(Mandatory)][int]$AppPid, [ValidateSet('calculator','browser','generic','deny','cancel','stale','disconnect')][string]$Case='calculator', [string]$ArtifactDirectory='docs/evidence/mcp-cua-ui', [long]$WitnessWindow=0, [int]$DisconnectPid=0)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'NativeUI-Helpers.ps1')
$previousWorkflow=$env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID='evoflow-mcp-live'
New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
function UI($arguments){Invoke-EvoFlowUI -AppPid $AppPid -Title 'EvoFlow' -Arguments $arguments}
function Nodes($items){foreach($item in $items){$item;Nodes $item.children}}
function Tree { $snapshot=UI @('inspect','-d','18','--json') | ConvertFrom-Json; @(Nodes $snapshot.windows.elements) }
$report=[ordered]@{case=$Case;appPid=$AppPid;target='local';status='FAIL';approvals=@();started=(Get-Date).ToString('o')}
try {
    if(-not(Tree | Where-Object automationId -eq 'TaskText')){UI @('invoke','Pill') | Out-Null}
    UI @('invoke','NewChatButton') | Out-Null
    $message=switch($Case){
        'calculator' {'Open Calculator, compute 6 × 7, and verify that Calculator displays 42.'}
        'browser' {'Use Cua to open an isolated browser, navigate to https://www.selenium.dev/selenium/web/web-form.html, fill the Text input with EvoFlow MCP verified, submit the form, and verify the page says Received! Use fresh Cua page observations.'}
        'generic' {'Use the Fixture Echo MCP data connector to echo exactly: EvoFlow connector verified. Use its discovered echo tool; do not use a terminal or write files.'}
        'deny' {'Use Cua to open Calculator and inspect its current display. If native approval is declined, stop and report the denial.'}
        'cancel' {'Use Cua to open Calculator and inspect its current display.'}
        'stale' {'Use Cua to launch a new Calculator window. Observe it, click its Close Calculator button using a fresh element token, then request get_window_state for that same now-closed pid/window_id to verify that a stale target is refused. Do not relaunch or use another backend.'}
        'disconnect' {'Use Cua to open Calculator and inspect its current display. This is a runtime failure test: if the connector disconnects or fails, stop and report that failure without retrying or using another backend.'}
    }
    UI @('set-value','TaskText',$message) | Out-Null; UI @('invoke','RunButton') | Out-Null
    if($WitnessWindow){& winapp ui focus -w $WitnessWindow | Out-Null; if($LASTEXITCODE -ne 0){throw 'Cannot focus the background witness'}}
    $seen=[Collections.Generic.List[string]]::new()
    for($attempt=0;$attempt -lt 360;$attempt++) {
        $tree=Tree
        $buttons=@($tree | Where-Object {$_.automationId -eq 'ApproveToolButton' -and $_.isEnabled})
        if($buttons.Count -gt 0){
            $description=@($tree | Where-Object automationId -eq 'ToolApprovalDescription')
            $pattern=if($Case -eq 'browser'){'(?s)^MCP · Cua · isolated browser / (browser_prepare|list_windows|get_browser_state|browser_navigate|browser_click|browser_type|get_guide)'}elseif($Case -eq 'generic'){'(?s)^MCP · Fixture Echo / echo'}else{'(?s)^MCP · Cua · Calculator / (launch_app|list_windows|get_window_state|click|verify_state|get_guide)'}
            if($description.Count -ne 1 -or $description[0].name -notmatch $pattern){throw 'Unexpected native approval outside the authorized acceptance case'}
            $line=($description[0].name -split "`n")[0]; $seen.Add($line); Write-Output $line
            if($Case -eq 'disconnect' -and $DisconnectPid){
                $owned=Get-CimInstance Win32_Process -Filter "ProcessId=$DisconnectPid"
                if($owned.ParentProcessId -ne $AppPid -or $owned.Name -ne 'cua-driver.exe'){throw 'Disconnect test target is not this EvoFlow-owned Cua child'}
                Stop-Process -Id $DisconnectPid; $DisconnectPid=0
            }
            if($Case -eq 'deny'){UI @('invoke','DeclineToolButton') | Out-Null}
            elseif($Case -eq 'cancel'){UI @('invoke','CancelButton') | Out-Null}
            else {UI @('invoke',$buttons[0].selector) | Out-Null}
        }
        if($attempt -gt 0 -and ($tree | Where-Object {$_.automationId -eq 'RunButton' -and $_.isEnabled})){break}
        Start-Sleep -Milliseconds 500
    }
    if($attempt -ge 360){throw 'Native chat acceptance deadline exceeded'}
    $tree=Tree
    $visible=@($tree | Where-Object {$_.type -eq 'Text' -and $_.name} | ForEach-Object name)
    $visible | Set-Content -LiteralPath (Join-Path $ArtifactDirectory ($Case+'-transcript.txt')) -Encoding utf8
    UI @('screenshot','--output',(Join-Path $ArtifactDirectory ($Case+'-evoflow.png')),'--json') | Out-Null
    $report.approvals=@($seen.ToArray()); $report.finalText=$visible[-12..-1]
    if($Case -eq 'calculator' -and (-not ($seen -match '/ click') -or -not($visible -match '42'))){throw 'Actual Cua clicks and a readback of 42 were not established by the chat evidence'}
    if($Case -eq 'browser' -and (-not($seen -match '/ browser_click') -or -not($visible -match 'Received'))){throw 'Cua browser interaction and changed page state were not established'}
    if($Case -eq 'generic' -and (-not($seen -match '/ echo') -or -not($visible -match 'EvoFlow connector verified'))){throw 'Generic MCP echo did not execute through native chat'}
    if($Case -eq 'deny' -and -not($visible -match 'denied|declined|denial')){throw 'Denial was not visible'}
    if($Case -eq 'cancel' -and -not($visible -match 'cancel|stopped')){throw 'Cancellation was not visible'}
    if($Case -eq 'stale' -and (-not($seen -match '/ click') -or -not($visible -match 'stale|mismatched'))){throw 'Closing and refusing the stale target was not established'}
    if($Case -eq 'disconnect' -and -not($visible -match 'failed|failure|disconnected|unavailable')){throw 'Runtime failure was not visible'}
    $report.status='PASS'
} catch { $report.error=$_.Exception.Message }
finally { & winapp ui yield --on local | Out-Null; $env:WINAPP_UI_WORKFLOW_ID=$previousWorkflow }
$report | ConvertTo-Json -Depth 5 | Tee-Object -FilePath (Join-Path $ArtifactDirectory ($Case+'-result.json'))
if($report.status -ne 'PASS'){exit 1}
