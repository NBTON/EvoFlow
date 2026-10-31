param([Parameter(Mandatory)][int]$AppPid,[Parameter(Mandatory)][long]$WindowId,
    [ValidateSet('calculator','browser','deny','cancel','stale','disconnect')][string]$Case='calculator',
    [string]$ArtifactDirectory='docs/evidence/mcp-cua-packaged',[int]$DisconnectPid=0)
$ErrorActionPreference='Stop'
$taskSession='evoflow-native-qa-'+[guid]::NewGuid().ToString('N').Substring(0,10)
$taskDirectory=[IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Force -Path $taskDirectory | Out-Null
function Driver([string]$tool,[hashtable]$arguments){
    $arguments.session=$taskSession
    $output=@(& cua-driver call $tool ($arguments|ConvertTo-Json -Depth 20 -Compress))
    if($LASTEXITCODE -ne 0){throw "Cua QA controller failed: $tool"}
    $value=($output -join "`n")|ConvertFrom-Json
    if($value.isError -or $value.status -eq 'refused'){throw "Cua QA controller refused $tool"}
    $value
}
function ReadState { Driver get_window_state @{pid=$AppPid;window_id=$WindowId;include_screenshot=$false;tree_format='elements';max_elements=250;max_depth=16} }
function Click([string]$label){
    $state=ReadState
    $control=@($state.elements|Where-Object {$_.label -eq $label -and $_.enabled})
    if($control.Count -ne 1){throw "Expected one current enabled '$label' control"}
    Driver click @{target=@{kind='window';pid=$AppPid;window_id=$WindowId};element_token=$control[0].element_token;delivery_mode='background'} | Out-Null
}
$report=[ordered]@{case=$Case;appPid=$AppPid;windowId=$WindowId;controller='Cua CLI UIA on the interactive desktop; product execution uses its separate private MCP connections';status='FAIL';approvals=@();started=(Get-Date).ToString('o')}
$seen=[Collections.Generic.List[string]]::new()
try{
    $state=ReadState
    if(-not($state.elements|Where-Object label -eq 'Task instructions')){Click 'EvoFlow, open task panel'}
    Click 'New Chat'
    $message=switch($Case){
        calculator {'Open Calculator, compute 6 × 7, and verify that Calculator displays 42.'}
        browser {'Use Cua to open an isolated browser, navigate to https://www.selenium.dev/selenium/web/web-form.html, fill the Text input with EvoFlow MCP verified, submit the form, and verify the page says Received! Use fresh Cua page observations. If browser preparation is refused, stop and report the refusal without retrying or using another backend.'}
        deny {'Use Cua to open Calculator and inspect its current display. If native approval is declined, stop and report the denial.'}
        cancel {'Use Cua to open Calculator and inspect its current display.'}
        stale {'Use Cua to launch a new Calculator window. Observe it, click its Close Calculator button using a fresh element token, then request get_window_state for that same now-closed pid/window_id to verify that a stale target is refused. Do not relaunch or use another backend.'}
        disconnect {'Use Cua to open Calculator and inspect its current display. This is a runtime failure test: if the connector disconnects or fails, stop and report that failure without retrying or using another backend.'}
    }
    $state=ReadState; $input=@($state.elements|Where-Object label -eq 'Task instructions')
    if($input.Count -ne 1){throw 'Missing task input'}
    Driver set_value @{pid=$AppPid;window_id=$WindowId;element_token=$input[0].element_token;value=$message} | Out-Null
    Click 'Run task'
    for($attempt=0;$attempt -lt 360;$attempt++){
        $state=ReadState
        $approval=@($state.elements|Where-Object {$_.label -eq 'Approve action' -and $_.enabled})
        if($approval.Count){
            $descriptions=@($state.elements|Where-Object {$_.role -eq 'Text' -and $_.label -like 'MCP · Cua ·*' -and $_.label -like '*Arguments:*'})
            $prefix=if($Case -eq 'browser'){'MCP · Cua · isolated browser / '}else{'MCP · Cua · Calculator / '}
            if($descriptions.Count -ne 1 -or -not $descriptions[0].label.StartsWith($prefix)){throw 'Approval was outside the authorized Cua test scope'}
            $line=($descriptions[0].label -split "`n")[0];$seen.Add($line);Write-Output $line
            if($Case -eq 'disconnect' -and $DisconnectPid){
                $owned=Get-CimInstance Win32_Process -Filter "ProcessId=$DisconnectPid"
                if($owned.Name -ne 'cua-driver.exe' -or $owned.ParentProcessId -ne $AppPid){throw 'Runtime failure target is not this app-owned Cua child'}
                Stop-Process -Id $DisconnectPid;$DisconnectPid=0
            }
            if($Case -eq 'deny'){Click 'Decline action'}elseif($Case -eq 'cancel'){Click 'Cancel'}else{Click 'Approve action'}
        }
        if($attempt -gt 0 -and ($state.elements|Where-Object {$_.label -eq 'Run task' -and $_.enabled})){break}
        Start-Sleep -Milliseconds 500
    }
    if($attempt -ge 360){throw 'Native chat deadline exceeded'}
    $state=ReadState;$text=($state.elements|ForEach-Object label)-join "`n"
    $text|Set-Content -LiteralPath (Join-Path $taskDirectory ($Case+'-transcript.txt')) -Encoding utf8
    Driver get_window_state @{pid=$AppPid;window_id=$WindowId;include_accessibility_tree=$false;screenshot_out_file=(Join-Path $taskDirectory ($Case+'-evoflow.png'))}|Out-Null
    if($Case -eq 'calculator' -and (-not($seen -match '/ click') -or $text -notmatch '42')){throw 'Cua Calculator clicks/readback were not established'}
    if($Case -eq 'browser' -and (-not($seen -match '/ browser_click') -or $text -notmatch 'Received')){throw 'Browser interaction remains unverified; read retained refusal'}
    if($Case -eq 'deny' -and $text -notmatch 'denied|declined|denial'){throw 'Denial was not visible'}
    if($Case -eq 'cancel' -and $text -notmatch 'cancel|stopped'){throw 'Cancellation was not visible'}
    if($Case -eq 'stale' -and (-not($seen -match '/ click') -or $text -notmatch 'stale|mismatched')){throw 'Stale target refusal was not established'}
    if($Case -eq 'disconnect' -and $text -notmatch 'failed|failure|disconnected|unavailable'){throw 'Disconnection was not visible'}
    $report.status='PASS'
}catch{$report.error=$_.Exception.Message}
finally{try{Driver end_session @{}|Out-Null}catch{}}
$report.approvals=@($seen.ToArray())
$report|ConvertTo-Json -Depth 5|Tee-Object -FilePath (Join-Path $taskDirectory ($Case+'-result.json'))
if($report.status -ne 'PASS'){exit 1}
