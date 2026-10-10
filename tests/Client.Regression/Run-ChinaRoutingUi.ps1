param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/../..").Path,
    [string]$ExecutablePath = (Join-Path $ProjectRoot 'artifacts/publish/Debug/fluent sing-box.exe')
)
$ErrorActionPreference = 'Stop'
$taskWinapp = (Get-Command winapp -ErrorAction Stop).Source
$taskRoot = Join-Path $ProjectRoot ('artifacts/china-routing-ui/run-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
$taskSettings = Join-Path $taskRoot 'settings.json'
$taskSource = Join-Path $taskRoot 'sing-box/user-config.json'
$taskCore = Join-Path $ProjectRoot 'artifacts/server-editor/core/sing-box.exe'
@{LanguageChoice='en-US';SingBoxExecutablePath=$taskCore;Servers=@(@{Id='dns-ui';Name='DNS fixture';OutboundJson='{"type":"socks","server":"127.0.0.1","server_port":1}'});DefaultServerId='dns-ui';SystemProxyEnabled=$false;Applications=@(@{Id='missing';Name='Missing app';IsEnabled=$true;MatchMode='path';ExecutablePaths=@('C:\fixtures\missing.exe');ServerId='dns-ui'},@{Id='store';Name='Store app';IsEnabled=$true;MatchMode='path';ExecutablePaths=@((Join-Path ([IO.Path]::GetPathRoot($env:windir)) 'Program Files\WindowsApps\OpenAI.Codex_1.2.3.4_x64__publisher\app\ChatGPT.exe'));ServerId='dns-ui'})} |
    ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $taskSettings
$taskOldData = $env:FLUENTSINGBOX_DATA_DIR; $taskOldBackground = $env:FLUENTSINGBOX_TEST_BACKGROUND
$taskResults = [Collections.Generic.List[object]]::new()
$taskProcess = $null; $taskWindow = $null; $script:taskSequence = 0
function Assert([bool]$Ok,[string]$Reason) { if (!$Ok) { throw $Reason } }
function Nodes($items) { foreach ($item in $items) { $item; if ($item.children) { Nodes $item.children } } }
function Ui([string[]]$Arguments) {
    $script:taskSequence++
    $raw = & $taskWinapp ui @Arguments -w "$taskWindow" --json 2>&1 | Out-String
    $exit = $LASTEXITCODE
    $raw | Set-Content -LiteralPath (Join-Path $taskRoot ('command-{0:D3}.json' -f $script:taskSequence))
    $value = $raw | ConvertFrom-Json
    if ($exit -ne 0 -or $value.error -or $value.timedOut) { throw "UI command failed: $($Arguments -join ' '): $raw" }
    Assert (!$taskProcess.HasExited) 'Test client exited unexpectedly'
    return $value
}
function Tree { return @(Nodes (Ui @('inspect','--depth','20')).windows.elements) }
function Choice([string]$Id,[string]$Value) {
    Ui @('invoke',$Id,'--action','expand') | Out-Null
    $item = @(Tree | Where-Object { $_.automationId -eq ($Id + '_' + $Value) -and $_.type -eq 'ListItem' })[0]
    Assert ($null -ne $item) "Missing choice: $Id $Value"
    Ui @('invoke',$item.selector,'--action','select') | Out-Null
}
function Expand-Group([string]$Id) {
    $inner = @(Nodes (Ui @('inspect',$Id,'--depth','3')).windows.elements | Where-Object className -eq 'Microsoft.UI.Xaml.Controls.Expander')[0]
    Ui @('invoke',$inner.selector,'--action','expand') | Out-Null
}
function Save-Dialog {
    $button = @(Tree | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Save' })[0]
    Ui @('invoke',$button.selector) | Out-Null
}
function Wait-Document([scriptblock]$Condition) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        try { $document = Get-Content -LiteralPath $taskSource -Raw | ConvertFrom-Json; if (& $Condition $document) { return $document } }
        catch [IO.IOException] {} catch [System.Management.Automation.ItemNotFoundException] {}
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'DNS auto save did not complete'
}
function Case([string]$Name,[scriptblock]$Work) {
    try { & $Work; $taskResults.Add([pscustomobject]@{name=$Name;status='passed'}); Write-Output "PASS $Name" }
    catch { $taskResults.Add([pscustomobject]@{name=$Name;status='failed';message=$_.Exception.Message}); throw }
}
try {
    $env:FLUENTSINGBOX_DATA_DIR = $taskRoot; $env:FLUENTSINGBOX_TEST_BACKGROUND = '1'
    $taskProcess = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path $ExecutablePath) -WindowStyle Hidden -PassThru
    $raw = & $taskWinapp ui wait-for Navigation_DNS -a "$($taskProcess.Id)" --timeout 15000 --json 2>&1 | Out-String
    $ready = $raw | ConvertFrom-Json
    Assert ($LASTEXITCODE -eq 0 -and !$ready.error -and !$ready.timedOut) "Fixture did not start: $raw"
    $windows = @(& $taskWinapp ui list-windows -a "$($taskProcess.Id)" --json | Out-String | ConvertFrom-Json)
    Assert ($windows.Count -eq 1) 'Expected one isolated window'; $taskWindow = $windows[0].hwnd
    Case 'china-ui/dns-network-policy' {
        Ui @('invoke','Navigation_DNS') | Out-Null; Ui @('wait-for','DNS_AutoIpv4Only') | Out-Null
        Assert (@(Tree | Where-Object automationId -eq 'DNS_NetworkStatus').Count -gt 0) 'IPv6 status missing'
        Ui @('invoke','DNS_AutoIpv4Only','--action','toggle-off') | Out-Null
        Start-Sleep -Milliseconds 800
        Assert ((Get-Content $taskSettings -Raw | ConvertFrom-Json).Configuration.AutoIpv4OnlyWhenNoIpv6 -eq $false) 'Auto IPv4 setting did not save'
        Ui @('invoke','DNS_AutoIpv4Only','--action','toggle-on') | Out-Null
        Ui @('wait-for','DNS_final') | Out-Null
        Ui @('screenshot','-o',(Join-Path $taskRoot 'dns-network-policy.png')) | Out-Null
    }
    Case 'china-ui/missing-and-store-path-warnings' {
        Ui @('invoke','Navigation_Proxy') | Out-Null; Ui @('wait-for','Application_PathWarnings') | Out-Null
        $tree=Tree
        Assert (@($tree | Where-Object { $_.name -like '*executable path does not exist*' }).Count -gt 0) 'Missing path warning absent'
        Assert (@($tree | Where-Object { $_.name -like '*version-independent*' }).Count -gt 0) 'Store matching explanation absent'
        Assert (@((Get-Content $taskSource -Raw | ConvertFrom-Json).route.rules | Where-Object process_path_regex).Count -eq 1) 'Store regex not generated'
        Assert (!(Test-Path (Join-Path $taskRoot 'sing-box/process.json'))) 'Warnings started core'
        Ui @('scroll','PageScrollViewer','--to','bottom') | Out-Null
        Ui @('screenshot','-o',(Join-Path $taskRoot 'application-warnings.png')) | Out-Null
        Ui @('invoke','Navigation_Routing') | Out-Null; Ui @('wait-for','Routing_PathWarnings') | Out-Null
    }
    Case 'china-ui/process-picker-and-name-validation' {
        Ui @('invoke','Navigation_Proxy') | Out-Null
        Ui @('scroll','PageScrollViewer','--to','bottom') | Out-Null
        Expand-Group 'Application_Expander_missing'
        Ui @('scroll','PageScrollViewer','--to','bottom') | Out-Null
        Ui @('invoke','Application_Edit_missing') | Out-Null; Ui @('wait-for','ApplicationEditor_Name') | Out-Null
        Choice 'ApplicationEditor_MatchMode' 'name'
        Ui @('invoke','ApplicationEditor_MatchMode','--action','collapse') | Out-Null
        Ui @('wait-for','ApplicationEditor_Names','--property','IsOffscreen','--value','false') | Out-Null
        Ui @('set-value','ApplicationEditor_Names','unknown-bare-process-name') | Out-Null
        Save-Dialog
        Ui @('wait-for','ApplicationEditor_Error') | Out-Null
        Assert (@(Tree | Where-Object automationId -eq 'ApplicationEditor_Names').Count -gt 0) 'Invalid name closed dialog'
        Ui @('invoke','ApplicationEditor_ChooseProcesses') | Out-Null; Ui @('wait-for','ApplicationEditor_RunningProcesses') | Out-Null
        Ui @('wait-for','ApplicationEditor_ProcessSearch','--property','IsOffscreen','--value','false') | Out-Null
        Ui @('set-value','ApplicationEditor_ProcessSearch','fluent sing-box.exe') | Out-Null
        $item = @(Tree | Where-Object { $_.type -eq 'ListItem' -and $_.name -like '*fluent sing-box.exe*' })[0]
        Assert ($null -ne $item) 'Test process missing from picker'
        Ui @('invoke',$item.selector,'--action','select') | Out-Null
        Ui @('set-value','ApplicationEditor_Names','') | Out-Null
        Ui @('invoke','ApplicationEditor_AddProcesses') | Out-Null
        Assert ((Ui @('get-value','ApplicationEditor_Names')).text -eq 'fluent sing-box.exe') 'Picker did not fill complete filename'
        Ui @('screenshot','-o',(Join-Path $taskRoot 'process-picker.png')) | Out-Null
        Save-Dialog
        Start-Sleep -Milliseconds 800
        $app=@((Get-Content $taskSettings -Raw | ConvertFrom-Json).Applications | Where-Object Id -eq 'missing')[0]
        Assert ($app.MatchMode -eq 'name' -and $app.ProcessNames -contains 'fluent sing-box.exe') 'Chosen process did not save'
        & $taskCore check -c $taskSource
        Assert ($LASTEXITCODE -eq 0) 'UI result failed core check'
    }
}
catch { if ($taskWindow) { try { Ui @('screenshot','-o',(Join-Path $taskRoot 'failure.png')) | Out-Null } catch {} }; throw }
finally {
    $taskResults | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRoot 'results.json')
    Write-Output "UI evidence: $taskRoot"
    if ($taskProcess) { if (!$taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id }; $taskProcess.Dispose() }
    $env:FLUENTSINGBOX_DATA_DIR = $taskOldData; $env:FLUENTSINGBOX_TEST_BACKGROUND = $taskOldBackground
}
