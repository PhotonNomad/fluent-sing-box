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
try {
    foreach ($language in @('en-US','zh-CN')) {
        $directory = Join-Path $taskRoot $language; New-Item -ItemType Directory -Path $directory | Out-Null
        $settingsPath = Join-Path $directory 'settings.json'
        @{LanguageChoice=$language;SingBoxExecutablePath=(Join-Path $ProjectRoot 'artifacts/server-editor/core/sing-box.exe');Servers=@(@{Id='lang-node';Name='Language fixture';OutboundJson='{"type":"socks","server":"127.0.0.1","server_port":1}'});DefaultServerId='lang-node';SystemProxyEnabled=$false;Applications=@(@{Id='match-app';Name='Match fixture';ServerId='lang-node';IsEnabled=$false;ExecutablePaths=@($ExecutablePath)})} | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath
        Start-Fixture $directory
        try {
            foreach ($page in @('Servers','Routing','Proxy','DNS','Configuration','Logs','Settings')) {
                Ui @('invoke',('Navigation_' + $page)) | Out-Null
                $ready = switch ($page) { 'Servers'{'Servers_Add'} 'Routing'{'Routing_final'} 'Proxy'{'Proxy_PortTextBox'} 'DNS'{'DNS_Save'} 'Configuration'{'Configuration_Check'} 'Logs'{'Logs_Filter'} 'Settings'{'Settings_Language'} }
                if ($page -ne 'Logs') { Ui @('wait-for',$ready) | Out-Null }
                $nodes = Tree
                if ($language -eq 'en-US') { Assert-English }
                else { Assert (@($nodes | Where-Object { $_.name -match '\p{IsCJKUnifiedIdeographs}' }).Count -gt 0) ('Chinese UI resources missing on ' + $page) }
                Ui @('screenshot','-o',(Join-Path $directory ($page + '.png'))) | Out-Null
                $taskResults.Add([pscustomobject]@{name="$language/page-$page";status='passed'})
            }
            Ui @('invoke','Navigation_Proxy') | Out-Null
            Ui @('scroll','PageScrollViewer','--to','bottom') | Out-Null
            Expand-Group 'Application_Expander_match-app'
            Ui @('wait-for','Application_Edit_match-app') | Out-Null
            Ui @('invoke','Application_Edit_match-app') | Out-Null
            Ui @('wait-for','ApplicationEditor_MatchMode') | Out-Null
            Ui @('invoke','ApplicationEditor_MatchMode','--action','expand') | Out-Null
            $matchChoice = @(Tree | Where-Object { $_.automationId -eq 'ApplicationEditor_MatchMode_name' -and $_.type -eq 'ListItem' })[0]
            Ui @('invoke',$matchChoice.selector,'--action','select') | Out-Null
            Ui @('set-value','ApplicationEditor_Names','ChatGPT.exe') | Out-Null
            if ($language -eq 'en-US') { Assert-English }
            Ui @('screenshot','-o',(Join-Path $directory 'ApplicationName.png')) | Out-Null
            Ui @('invoke',$(if ($language -eq 'en-US') {'Save'}else{'保存'})) | Out-Null
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            do {
                try { $matchSettings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json } catch [IO.IOException] {} catch [System.Management.Automation.ItemNotFoundException] {}
                if ($matchSettings.Applications[0].MatchMode -eq 'name') { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $deadline)
            Assert ($matchSettings.Applications[0].ProcessNames[0] -eq 'ChatGPT.exe') 'Process name was not saved'
            Expand-Group 'Application_Expander_match-app'
            Ui @('wait-for','Application_Edit_match-app') | Out-Null
            Ui @('invoke','Application_Edit_match-app') | Out-Null
            Ui @('wait-for','ApplicationEditor_Names') | Out-Null
            Assert ((Ui @('get-value','ApplicationEditor_Names')).text -eq 'ChatGPT.exe') 'Name editor did not reload'
            Ui @('invoke',$(if ($language -eq 'en-US') {'Cancel'}else{'取消'})) | Out-Null
            $taskResults.Add([pscustomobject]@{name="$language/application-name-edit-and-reload";status='passed'})
            Ui @('invoke',$(if ($language -eq 'en-US') {'Add application group'}else{'添加应用组'})) | Out-Null
            Ui @('wait-for','ApplicationEditor_MatchMode') | Out-Null
            Ui @('set-value','ApplicationEditor_Name','Name-only fixture') | Out-Null
            Ui @('invoke','ApplicationEditor_MatchMode','--action','expand') | Out-Null
            $matchChoice = @(Tree | Where-Object { $_.automationId -eq 'ApplicationEditor_MatchMode_name' -and $_.type -eq 'ListItem' })[0]
            Ui @('invoke',$matchChoice.selector,'--action','select') | Out-Null
            Ui @('set-value','ApplicationEditor_Names','fixture.exe') | Out-Null
            Ui @('invoke',$(if ($language -eq 'en-US') {'Save'}else{'保存'})) | Out-Null
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            do {
                try { $addedSettings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json } catch [IO.IOException] {} catch [System.Management.Automation.ItemNotFoundException] {}
                if ($addedSettings.Applications.Count -eq 2) { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $deadline)
            Assert ($addedSettings.Applications.Count -eq 2 -and $addedSettings.Applications[1].MatchMode -eq 'name') 'Could not add a name-only group'
            $taskResults.Add([pscustomobject]@{name="$language/application-name-add-without-file-picker";status='passed'})
            Ui @('invoke','Navigation_Servers') | Out-Null
            Ui @('invoke','Servers_Add') | Out-Null
            Ui @('wait-for','ServerEditor_Type') | Out-Null
            if ($language -eq 'en-US') {
                Assert-English
                Ui @('invoke','ServerEditor_Type','--action','expand') | Out-Null
                $item = @(Tree | Where-Object { $_.automationId -eq 'ServerEditor_Type_vmess' -and $_.type -eq 'ListItem' })[0]
                Ui @('invoke',$item.selector,'--action','select') | Out-Null
                Assert-English
            }
            $taskResults.Add([pscustomobject]@{name="$language/dynamic-server-form";status='passed'})
            $cancel = if ($language -eq 'en-US') { 'Cancel' } else { '取消' }
            Ui @('invoke',$cancel) | Out-Null
            Ui @('invoke','Navigation_Settings') | Out-Null
            Ui @('wait-for','Settings_Language') | Out-Null
            $hash = (Get-FileHash -LiteralPath (Join-Path $directory 'sing-box/user-config.json')).Hash
            $nextLanguage = if ($language -eq 'en-US') { 'zh-CN' } else { 'en-US' }
            Ui @('invoke','Settings_Language','--action','expand') | Out-Null
            $choice = @(Tree | Where-Object { $_.automationId -eq ('Settings_Language_' + $nextLanguage) -and $_.type -eq 'ListItem' })[0]
            Ui @('invoke',$choice.selector,'--action','select') | Out-Null
            Ui @('wait-for','Settings_LanguageStatus') | Out-Null
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            do {
                try { $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json } catch [IO.IOException] {} catch [System.Management.Automation.ItemNotFoundException] {}
                if ($settings.LanguageChoice -eq $nextLanguage) { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $deadline)
            Assert ($settings.LanguageChoice -eq $nextLanguage) 'Language preference was not saved'
            Assert ((Get-FileHash -LiteralPath (Join-Path $directory 'sing-box/user-config.json')).Hash -eq $hash) 'Language selection changed proxy configuration'
            Assert (!(Test-Path -LiteralPath (Join-Path $directory 'sing-box/process.json'))) 'Language selection started a core'
            $taskResults.Add([pscustomobject]@{name="$language/save-language-no-network-change";status='passed'})
            Stop-Fixture
            Start-Fixture $directory
            $navigation = @(Tree | Where-Object automationId -eq 'Navigation_Servers')[0]
            Assert ($navigation.name -eq $(if ($nextLanguage -eq 'en-US') {'Servers'}else{'服务器'})) 'Restart did not apply language to XAML'
            $taskResults.Add([pscustomobject]@{name="$language/restart-applies-language";status='passed'})
        }
        finally { Stop-Fixture }
    }
}
catch { $taskResults.Add([pscustomobject]@{name='localization-ui';status='failed';message=$_.Exception.Message}); throw }
finally {
    Stop-Fixture
    $env:FLUENTSINGBOX_DATA_DIR = $taskOldData; $env:FLUENTSINGBOX_TEST_BACKGROUND = $taskOldBackground
    $taskResults | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRoot 'results.json')
    Write-Output "Localization UI evidence: $taskRoot"
}
