param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/../..").Path,
    [string]$ExecutablePath = (Join-Path $ProjectRoot 'artifacts/publish/Debug/fluent sing-box.exe')
)
$ErrorActionPreference = 'Stop'
$taskWinapp = (Get-Command winapp -ErrorAction Stop).Source
$taskRoot = Join-Path $ProjectRoot ('artifacts/localization/ui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
$taskOldData = $env:FLUENTSINGBOX_DATA_DIR; $taskOldBackground = $env:FLUENTSINGBOX_TEST_BACKGROUND
$taskResults = [Collections.Generic.List[object]]::new()
$taskProcess = $null; $taskWindow = $null; $script:taskSequence = 0
function Nodes($items) { foreach ($item in $items) { if ($item.type -eq 'ScrollBar' -or ($item.type -eq 'TitleBar' -and $item.automationId -eq 'TitleBar')) { continue }; $item; if ($item.children) { Nodes $item.children } } }
function Ui([string[]]$Arguments) {
    $script:taskSequence++
    $raw = & $taskWinapp ui @Arguments -w "$taskWindow" --json 2>&1 | Out-String
    $exit = $LASTEXITCODE
    $raw | Set-Content -LiteralPath (Join-Path $taskRoot ('command-{0:D3}.json' -f $script:taskSequence))
    $value = $raw | ConvertFrom-Json
    if ($exit -ne 0 -or $value.error -or $value.timedOut) { throw "UI command failed: $($Arguments -join ' '): $raw" }
    return $value
}
function Tree { return @(Nodes (Ui @('inspect','--depth','20')).windows.elements) }
function Expand-Group([string]$Id) {
    $inner = @(Nodes (Ui @('inspect',$Id,'--depth','3')).windows.elements | Where-Object className -eq 'Microsoft.UI.Xaml.Controls.Expander')[0]
    Ui @('invoke',$inner.selector,'--action','expand') | Out-Null
}
function Assert([bool]$Ok,[string]$Message) { if (!$Ok) { throw $Message } }
function Assert-English {
    # Framework scrollbar accessibility names use the Windows display language.
    $bad = @(Tree | Where-Object { $_.type -ne 'ScrollBar' -and $_.className -ne 'Popup' -and $_.name -match '\p{IsCJKUnifiedIdeographs}' })
    Assert ($bad.Count -eq 0) ('Chinese text in English UI: ' + ($bad.name -join ', '))
}
function Start-Fixture([string]$Directory) {
    $env:FLUENTSINGBOX_DATA_DIR = $Directory
    $env:FLUENTSINGBOX_TEST_BACKGROUND = '1'
    $script:taskProcess = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path $ExecutablePath) -WindowStyle Hidden -PassThru
    $raw = & $taskWinapp ui wait-for Navigation_Servers -a "$($taskProcess.Id)" --timeout 15000 --json 2>&1 | Out-String
    $ready = $raw | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $ready.error -or $ready.timedOut) { throw "Localization fixture did not start: $raw" }
    $windows = @(& $taskWinapp ui list-windows -a "$($taskProcess.Id)" --json | Out-String | ConvertFrom-Json)
    Assert ($windows.Count -eq 1) 'Expected one isolated window'
    $script:taskWindow = $windows[0].hwnd
    Ui @('wait-for','Core_Toggle','--property','IsEnabled','--value','true') | Out-Null
}
function Stop-Fixture {
    if ($script:taskProcess) {
        if (!$script:taskProcess.HasExited) { Stop-Process -Id $script:taskProcess.Id; $script:taskProcess.WaitForExit(5000) | Out-Null }
        $script:taskProcess.Dispose(); $script:taskProcess = $null
    }
}
function Choice([string]$Id,[string]$Value) {
    Ui @('invoke',$Id,'--action','expand') | Out-Null
    $item = @(Tree | Where-Object { $_.automationId -eq ($Id + '_' + $Value) -and $_.type -eq 'ListItem' })[0]
    Assert ($null -ne $item) ('Missing choice ' + $Id + '_' + $Value)
    Ui @('invoke',$item.selector,'--action','select') | Out-Null
}
function Save-Dialog {
    $button=@(Tree | Where-Object { $_.type -eq 'Button' -and $_.name -eq 'Save' })[0]
    Ui @('invoke',$button.selector) | Out-Null
}
function Wait-Document([scriptblock]$Condition) {
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    do {
        try { $document=Get-Content -LiteralPath $script:sourcePath -Raw | ConvertFrom-Json; if (& $Condition $document) { return $document } } catch [IO.IOException] {} catch [System.Management.Automation.ItemNotFoundException] {}
        Start-Sleep -Milliseconds 100
    } while([DateTime]::UtcNow -lt $deadline)
    throw 'Configuration save did not complete'
}
function Case([string]$Name,[scriptblock]$Work) { & $Work; $taskResults.Add([pscustomobject]@{name=$Name;status='passed'}); Write-Output "PASS $Name" }


function Assert-EditorError([string]$Message) {
    $deadline=[DateTime]::UtcNow.AddSeconds(5)
    do {if(@(Tree|Where-Object name -eq $Message).Count -gt 0){return};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
    throw ('Missing editor error: '+$Message)
}
function Wait-App([string]$Name) {
    $deadline=[DateTime]::UtcNow.AddSeconds(8)
    do {$s=Get-Content (Join-Path $directory 'settings.json') -Raw|ConvertFrom-Json;$a=@($s.Applications|Where-Object Name -eq $Name);if($a.Count -eq 1){return $a[0]};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
    throw 'Application did not save'
}
try {
    $directory=Join-Path $taskRoot 'data';New-Item -ItemType Directory -Path $directory|Out-Null
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start();$fixturePort=$listener.LocalEndpoint.Port;$listener.Stop()
    @{SystemProxyPort=$fixturePort;LanguageChoice='en-US';SingBoxExecutablePath=(Join-Path $ProjectRoot 'artifacts/server-editor/core/sing-box.exe');Servers=@(@{Id='node';Name='App fixture';OutboundJson='{"type":"socks","server":"127.0.0.1","server_port":1}'});DefaultServerId='node';GlobalTunEnabled=$false;SystemProxyEnabled=$false;Applications=@(@{Id='legacy';Name='Legacy paths';ServerId='node';IsEnabled=$false;ExecutablePaths=@('C:\Windows\notepad.exe')})}|ConvertTo-Json -Depth 20|Set-Content (Join-Path $directory 'settings.json')
    Start-Fixture $directory
    Ui @('invoke','Navigation_Proxy')|Out-Null;Ui @('wait-for','Proxy_Tun_Enabled')|Out-Null
    $add=@(Tree|Where-Object {$_.type -eq 'Button' -and $_.name -eq 'Add application group'})[0]
    Assert ($null -ne $add) 'Add app group missing';Ui @('invoke',$add.selector)|Out-Null
    Ui @('wait-for','ApplicationEditor_Names')|Out-Null
    Case 'application/name-mode-default-with-empty-inputs' {
        Assert (@(Tree|Where-Object automationId -eq 'ApplicationEditor_Paths').Count -eq 0) 'Path editor displayed by default'
        Assert ([string]::IsNullOrEmpty((Ui @('get-value','ApplicationEditor_Names')).text)) 'Default process names populated'
        Assert ([string]::IsNullOrEmpty((Ui @('get-value','ApplicationEditor_Name')).text)) 'Default group name populated'
        Ui @('screenshot','-o',(Join-Path $taskRoot 'Application-Name-Default.png'))|Out-Null
    }
    Case 'application/accurate-errors-and-name-only-save' {
        Ui @('set-value','ApplicationEditor_Names','chrome.exe')|Out-Null;Save-Dialog
        Assert-EditorError 'Enter an application group name.'
        Assert ((Ui @('get-value','ApplicationEditor_Names')).text -eq 'chrome.exe') 'Invalid save lost draft'
        Ui @('set-value','ApplicationEditor_Name','Named group')|Out-Null
        Ui @('set-value','ApplicationEditor_Names','')|Out-Null;Save-Dialog
        Assert-EditorError 'Enter at least one process name. A program path is not required in name mode.'
        Ui @('set-value','ApplicationEditor_Names','chrome.exe')|Out-Null;Save-Dialog
        Ui @('wait-for','ApplicationEditor_Names','--gone')|Out-Null
        $a=Wait-App 'Named group'
        Assert ($a.MatchMode -eq 'name' -and $a.ProcessNames -contains 'chrome.exe' -and @($a.ExecutablePaths).Count -eq 0) 'Name-only group did not save correctly'
    }
    Case 'application/multiple-process-names-survive-edit-and-save' {
        Ui @('invoke','Navigation_Routing')|Out-Null;Ui @('wait-for','Proxy_Tun_Enabled','--gone')|Out-Null;Ui @('invoke','Navigation_Proxy')|Out-Null
        $a=Wait-App 'Named group'
        $groupId='Application_Expander_' + $a.Id
        Ui @('wait-for',$groupId)|Out-Null
        Ui @('scroll','PageScrollViewer','--to','bottom')|Out-Null
        Expand-Group $groupId
        Ui @('invoke',('Application_Edit_' + $a.Id))|Out-Null
        $expected=@('chrome.exe','msedge.exe','firefox.exe')
        Ui @('set-value','ApplicationEditor_Names',($expected -join "`r`n"))|Out-Null;Save-Dialog
        Ui @('wait-for','ApplicationEditor_Names','--gone')|Out-Null
        $deadline=[DateTime]::UtcNow.AddSeconds(8)
        do {$a=Wait-App 'Named group';if(@($a.ProcessNames).Count -eq 3){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
        Assert (($a.ProcessNames -join '|') -eq ($expected -join '|')) 'Multiple process names did not save'
        Expand-Group $groupId
        Ui @('invoke',('Application_Edit_' + $a.Id))|Out-Null
        Ui @('wait-for','ApplicationEditor_Names')|Out-Null
        $actual=@(((Ui @('get-value','ApplicationEditor_Names')).text) -split '\r?\n|\r' | Where-Object {$_})
        Assert (($actual -join '|') -eq ($expected -join '|')) ('Reopening lost process names: ' + ($actual -join ', '))
        Ui @('screenshot','-o',(Join-Path $taskRoot 'Application-Multiple-Names.png'))|Out-Null
        Save-Dialog;Ui @('wait-for','ApplicationEditor_Names','--gone')|Out-Null
        $a=Wait-App 'Named group'
        Assert (($a.ProcessNames -join '|') -eq ($expected -join '|')) 'Unchanged edit lost process names'
    }
    Case 'application/legacy-path-mode-preserved' {
        Ui @('invoke','Navigation_Routing')|Out-Null;Ui @('wait-for','Proxy_Tun_Enabled','--gone')|Out-Null;Ui @('invoke','Navigation_Proxy')|Out-Null;Ui @('wait-for','Application_Expander_legacy')|Out-Null
        Ui @('scroll','PageScrollViewer','--to','bottom')|Out-Null
        Expand-Group 'Application_Expander_legacy';Ui @('wait-for','Application_Edit_legacy')|Out-Null;Ui @('invoke','Application_Edit_legacy')|Out-Null
        Ui @('wait-for','ApplicationEditor_Paths')|Out-Null
        Assert (@(Tree|Where-Object automationId -eq 'ApplicationEditor_Names').Count -eq 0) 'Legacy path group changed to name mode'
        Assert ((Ui @('get-value','ApplicationEditor_Paths')).text -eq 'C:\Windows\notepad.exe') 'Legacy paths lost'
        $expectedPaths=@('C:\Windows\notepad.exe','C:\Windows\System32\cmd.exe')
        Ui @('set-value','ApplicationEditor_Paths',($expectedPaths -join "`r`n"))|Out-Null
        Ui @('set-value','ApplicationEditor_Name','Legacy edited')|Out-Null;Save-Dialog
        Ui @('wait-for','ApplicationEditor_Paths','--gone')|Out-Null
        $a=Wait-App 'Legacy edited';Assert ($a.MatchMode -eq 'path' -and $a.ExecutablePaths -contains 'C:\Windows\notepad.exe') 'Legacy mode not retained'
        Expand-Group 'Application_Expander_legacy';Ui @('invoke','Application_Edit_legacy')|Out-Null
        Ui @('wait-for','ApplicationEditor_Paths')|Out-Null
        $actualPaths=@(((Ui @('get-value','ApplicationEditor_Paths')).text) -split '\r?\n|\r' | Where-Object {$_})
        Assert (($actualPaths -join '|') -eq ($expectedPaths -join '|')) 'Reopening lost program paths'
        Save-Dialog;Ui @('wait-for','ApplicationEditor_Paths','--gone')|Out-Null
        $a=Wait-App 'Legacy edited'
        Assert (($a.ExecutablePaths -join '|') -eq ($expectedPaths -join '|')) 'Unchanged edit lost program paths'
        Assert (!(Test-Path (Join-Path $directory 'sing-box/process.json'))) 'Application edit started core'
        Assert (!(Test-Path (Join-Path $directory 'client-errors.log'))) 'Unhandled client exception'
    }
}
catch {if($taskWindow){Tree|ConvertTo-Json -Depth 70|Set-Content (Join-Path $taskRoot 'failure-tree.json')};$taskResults.Add([pscustomobject]@{name='application-name-ui';status='failed';message=$_.Exception.Message});throw}
finally {
    Stop-Fixture;$env:FLUENTSINGBOX_DATA_DIR=$taskOldData;$env:FLUENTSINGBOX_TEST_BACKGROUND=$taskOldBackground
    $taskResults|ConvertTo-Json -Depth 8|Set-Content (Join-Path $taskRoot 'results.json');Write-Output "Application UI evidence: $taskRoot"
}
