function Select-EvoFlowWindow {
    param(
        [Parameter(Mandatory)][object[]]$Windows,
        [Parameter(Mandatory)][int]$AppPid,
        [Parameter(Mandatory)][string]$Title
    )
    $matches=@($Windows | Where-Object { $_.processId -eq $AppPid -and $_.title -eq $Title -and $_.hwnd })
    if($matches.Count -ne 1){throw "Expected one live '$Title' window for PID $AppPid; found $($matches.Count)"}
    [string]$matches[0].hwnd
}

function Get-EvoFlowWindow {
    param([Parameter(Mandatory)][int]$AppPid, [Parameter(Mandatory)][string]$Title)
    $output=@(& winapp ui list-windows --on local -a $AppPid --json)
    if($LASTEXITCODE -ne 0){throw 'Native window discovery failed'}
    Select-EvoFlowWindow -Windows @($output -join "`n" | ConvertFrom-Json) -AppPid $AppPid -Title $Title
}

function Invoke-EvoFlowUI {
    param(
        [Parameter(Mandatory)][int]$AppPid,
        [Parameter(Mandatory)][string]$Title,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    # Resolve by PID and exact title on every operation; never inherit a child HWND.
    $window=Get-EvoFlowWindow -AppPid $AppPid -Title $Title
    $output=@(& winapp ui @Arguments --on local -w $window)
    if($LASTEXITCODE -ne 0){throw "Native UI failed for '$Title': $($Arguments -join ' ')"}
    $output -join "`n"
}
