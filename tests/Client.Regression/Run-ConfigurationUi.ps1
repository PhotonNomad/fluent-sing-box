param(
    [string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/../..").Path,
    [string]$ExecutablePath = (Join-Path $ProjectRoot 'artifacts/publish/Debug/fluent sing-box.exe'),
    [switch]$RoutingOnly
)
$ErrorActionPreference = 'Stop'
$taskWinapp = (Get-Command winapp -ErrorAction Stop).Source
$taskRoot = Join-Path $ProjectRoot ('artifacts/configuration-ui/run-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
$taskSettings = Join-Path $taskRoot 'settings.json'
$taskCore = Join-Path $ProjectRoot 'artifacts/server-editor/core/sing-box.exe'
if (!(Test-Path -LiteralPath $taskCore)) { throw 'Prepare the core fixture with tests/ServerEditor.Regression before UI tests' }
@{LanguageChoice='zh-CN';SingBoxExecutablePath=$taskCore;Servers=@(@{Id='ui-node';Name='UI fixture';OutboundJson='{"type":"socks","server":"127.0.0.1","server_port":1}'});DefaultServerId='ui-node';SystemProxyEnabled=$false;Applications=@(@{Id='ui-app';Name='UI application';ServerId='ui-node';IsEnabled=$false;ExecutablePaths=@('C:\fixtures\ui-app.exe')})} |
    ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $taskSettings
$taskResults = [Collections.Generic.List[object]]::new()
$taskOldData = $env:FLUENTSINGBOX_DATA_DIR
$taskOldBackground = $env:FLUENTSINGBOX_TEST_BACKGROUND
$taskOldEditor = $env:FLUENTSINGBOX_TEST_NO_EDITOR
$taskOldImport = $env:FLUENTSINGBOX_TEST_IMPORT_JSON
$taskOldExport = $env:FLUENTSINGBOX_TEST_EXPORT_JSON
$taskProcess = $null; $taskWindow = $null; $script:taskSequence = 0
function Ui([string[]]$Arguments) {
    $script:taskSequence++
    $output = & $taskWinapp ui @Arguments -w "$taskWindow" --json 2>&1 | Out-String
    $exit = $LASTEXITCODE
    $output | Set-Content -LiteralPath (Join-Path $taskRoot ('command-{0:D3}.json' -f $script:taskSequence))
    $json = $output | ConvertFrom-Json
    if ($exit -ne 0 -or $json.error -or $json.timedOut) { throw "UI command failed: $($Arguments -join ' '): $output" }
    if ($taskProcess.HasExited) { throw 'Test client exited unexpectedly' }
    return $json
}
function Assert([bool]$Ok, [string]$Reason) { if (!$Ok) { throw $Reason } }
function Nodes($items) { foreach ($item in $items) { $item; if ($item.children) { Nodes $item.children } } }
function Tree { return @(Nodes (Ui @('inspect','--depth','20')).windows.elements) }
function Expand-Dns([string]$Id) {
    $tree = Ui @('inspect',$Id,'--depth','3')
    $inner = @(Nodes $tree.windows.elements | Where-Object className -eq 'Microsoft.UI.Xaml.Controls.Expander')[0]
    Assert ($null -ne $inner) "Settings expander missing: $Id"
    Ui @('invoke',$inner.selector,'--action','expand') | Out-Null
}
function Case([string]$Name, [scriptblock]$Work) {
    if ($RoutingOnly -and !$Name.StartsWith('routing/')) { return }
    try { & $Work; $taskResults.Add([pscustomobject]@{name=$Name;status='passed'}); Write-Output "PASS $Name" }
    catch { $taskResults.Add([pscustomobject]@{name=$Name;status='failed';message=$_.Exception.Message}); throw }
}
function Choice([string]$Id, [string]$ItemId) {
    Ui @('invoke',$Id,'--action','expand') | Out-Null
    $matches = @(Tree | Where-Object { $_.automationId -eq $ItemId -and $_.type -eq 'ListItem' })
    Assert ($matches.Count -ge 1) "Dropdown item not found: $ItemId"
    Ui @('invoke',$matches[0].selector,'--action','select') | Out-Null
}
function Read-Settings { return Get-Content -LiteralPath $taskSettings -Raw | ConvertFrom-Json }
function Wait-Listener([bool]$Present) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $listener = @(Get-NetTCPConnection -LocalPort 10819 -State Listen -ErrorAction SilentlyContinue)
        if (($listener.Count -gt 0) -eq $Present) { return $listener }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Listener state did not become $Present"
}
function Wait-Saved([scriptblock]$Condition) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $taskProcess.Refresh(); if ($taskProcess.HasExited) { throw 'Client exited while saving' }
        try { $settings = Read-Settings; if (& $Condition $settings) { return $settings } }
        catch [IO.IOException] {}
        catch [System.Management.Automation.ItemNotFoundException] {} # Atomic replacement can briefly race this read.
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Saved DNS state did not match'
}
try {
    $env:FLUENTSINGBOX_DATA_DIR = $taskRoot
    $env:FLUENTSINGBOX_TEST_BACKGROUND = '1'
    $env:FLUENTSINGBOX_TEST_NO_EDITOR = '1'
    $env:FLUENTSINGBOX_TEST_IMPORT_JSON = Join-Path $taskRoot 'import.json'
    $env:FLUENTSINGBOX_TEST_EXPORT_JSON = Join-Path $taskRoot 'export.json'
    $taskProcess = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path $ExecutablePath) -WindowStyle Hidden -PassThru
    $readyOutput = & $taskWinapp ui wait-for Navigation_DNS -a "$($taskProcess.Id)" --timeout 15000 --json 2>&1 | Out-String
    $ready = $readyOutput | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $ready.error -or $ready.timedOut) {
        $taskProcess.Refresh()
        throw "DNS navigation did not appear (process exited: $($taskProcess.HasExited)): $readyOutput"
    }
    $windows = @(& $taskWinapp ui list-windows -a "$($taskProcess.Id)" --json | Out-String | ConvertFrom-Json)
    Assert ($windows.Count -eq 1) 'Expected exactly one isolated test window'
    $taskWindow = $windows[0].hwnd
    Case 'startup/opens-without-core-configuration-check' {
        Ui @('wait-for','Core_Toggle','--property','IsEnabled','--value','true') | Out-Null
        Assert (!(Test-Path -LiteralPath (Join-Path $taskRoot 'sing-box/startup-preflight.json'))) 'Opening the app ran a startup preflight'
        Assert (!(Test-Path -LiteralPath (Join-Path $taskRoot 'sing-box/config.validation.json'))) 'Opening the app checked core configuration'
        Assert (!(Test-Path -LiteralPath (Join-Path $taskRoot 'sing-box/process.json'))) 'Opening the app started a core'
    }
    Case 'lifecycle/same-data-directory-rejects-second-window' {
        $second = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path $ExecutablePath) -WindowStyle Hidden -PassThru
        try
        {
            Assert ($second.WaitForExit(8000)) 'Second client did not exit under the instance lock'
            $taskProcess.Refresh()
            Assert (!$taskProcess.HasExited) 'Instance lock stopped the original client'
            $windows = @(& $taskWinapp ui list-windows -a "$($taskProcess.Id)" --json | Out-String | ConvertFrom-Json)
            Assert ($windows.Count -eq 1) 'Original process no longer has exactly one window'
        }
        finally { if (!$second.HasExited) { Stop-Process -Id $second.Id }; $second.Dispose() }
    }
    Case 'routing/final-choice-save-and-reload-without-core-start' {
        Ui @('invoke','Navigation_Routing') | Out-Null
        Ui @('wait-for','Routing_final') | Out-Null
        Choice 'Routing_final' 'Routing_final_proxy-ui-node'
        Assert (@(Tree | Where-Object automationId -eq 'Routing_Save').Count -eq 0) 'Manual route save button still exists'
        Ui @('wait-for','配置已校验并保存，重启核心后生效。','--type','Text') | Out-Null
        Assert ((Get-Content (Join-Path $taskRoot 'sing-box/user-config.json') -Raw | ConvertFrom-Json).route.final -eq 'proxy-ui-node') 'Final outbound was not saved'
        Ui @('invoke','Navigation_Proxy') | Out-Null
        Ui @('invoke','Navigation_Routing') | Out-Null
        Ui @('wait-for','Routing_final') | Out-Null
        Assert ((Ui @('get-value','Routing_final')).text -eq 'proxy-ui-node') 'Final selection was not restored'
        Assert (!(Test-Path (Join-Path $taskRoot 'sing-box/process.json'))) 'Saving final started core'
        Assert (!(Test-Path (Join-Path $taskRoot 'sing-box/sing-box.log'))) 'Startup or validation created runtime log'
        $finalSelector = @(Tree | Where-Object automationId -eq 'Routing_final')[0].selector
        Ui @('invoke',$finalSelector,'--action','expand') | Out-Null
        Assert (@(Tree | Where-Object automationId -eq 'Routing_final_').Count -eq 0) 'Empty final option is still available'
        $selectedItem = @(Tree | Where-Object { $_.automationId -eq 'Routing_final_proxy-ui-node' -and $_.type -eq 'ListItem' })[0].selector
        Ui @('invoke',$selectedItem,'--action','select') | Out-Null
        Ui @('scroll-into-view','Routing_final') | Out-Null
        Ui @('screenshot','-o',(Join-Path $taskRoot 'routing.png')) | Out-Null
    }
    Case 'routing/rule-toggle-and-order-autosave' {
        Ui @('invoke','Navigation_Routing') | Out-Null
        Ui @('wait-for','Routing_Rules_0') | Out-Null
        $path = Join-Path $taskRoot 'sing-box/user-config.json'
        $before = Get-Content $path -Raw | ConvertFrom-Json
        Expand-Dns 'Routing_Rules_0'
        Ui @('invoke','Routing_Rules_0_Enabled','--action','toggle-off') | Out-Null
        Ui @('wait-for','配置已校验并保存，重启核心后生效。','--type','Text') | Out-Null
        $after = Get-Content $path -Raw | ConvertFrom-Json
        Assert ($after.route.rules.Count -eq $before.route.rules.Count - 1) 'Disabled route rule was not automatically saved'
        Ui @('invoke','Routing_Rules_0_Enabled','--action','toggle-on') | Out-Null
        Ui @('wait-for','配置已校验并保存，重启核心后生效。','--type','Text') | Out-Null
        Assert ((Get-Content $path -Raw | ConvertFrom-Json).route.rules.Count -eq $before.route.rules.Count) 'Enabled route rule was not restored'
        Ui @('invoke','Routing_Rules_0_Down') | Out-Null
        Ui @('wait-for','配置已校验并保存，重启核心后生效。','--type','Text') | Out-Null
        Assert ((Get-Content $path -Raw | ConvertFrom-Json).route.rules[1].action -eq $before.route.rules[0].action) 'Route move was not automatically saved'
    }
    Case 'proxy/flat-settings-and-no-empty-status-row' {
        Ui @('invoke','Navigation_Proxy') | Out-Null
        Ui @('wait-for','Proxy_PortTextBox') | Out-Null
        $nodes = Tree
        Assert (@($nodes | Where-Object automationId -eq 'Proxy_SystemExpander').Count -eq 0) 'System proxy still nested in expander'
        Assert (@($nodes | Where-Object automationId -eq 'Proxy_SettingsStatus').Count -eq 0) 'Empty status message still occupies UI'
        Assert (@($nodes | Where-Object automationId -eq 'Proxy_UsernameTextBox').Count -eq 0) 'Disabled auth fields visible'
        Ui @('invoke','Proxy_AuthenticationSwitch') | Out-Null
        Ui @('wait-for','Proxy_UsernameTextBox') | Out-Null
        Ui @('invoke','Proxy_AuthenticationSwitch') | Out-Null
        Ui @('wait-for','Proxy_UsernameTextBox','--gone') | Out-Null
        Ui @('set-value','Proxy_PortTextBox','10819') | Out-Null
        Ui @('invoke','Proxy_Apply') | Out-Null
        $saved = Wait-Saved { param($s) $s.SystemProxyPort -eq 10819 }
        Assert (!$saved.SystemProxyEnabled) 'Applying disabled proxy settings enabled proxy'
        Ui @('wait-for','设置已应用。','--type','Text') | Out-Null
        Start-Sleep -Milliseconds 400
        Ui @('screenshot','-o',(Join-Path $taskRoot 'proxy-flat.png')) | Out-Null
    }
    Case 'proxy/global-tun-preference-without-core-start' {
        Ui @('invoke','Proxy_GlobalTun') | Out-Null
        $saved = Wait-Saved { param($s) $s.GlobalTunEnabled }
        Assert (!(Test-Path -LiteralPath (Join-Path $taskRoot 'sing-box/process.json'))) 'Global TUN preference started a core'
        Ui @('invoke','Navigation_DNS') | Out-Null
        Ui @('invoke','Navigation_Proxy') | Out-Null
        Ui @('invoke','Proxy_GlobalTun') | Out-Null
        $saved = Wait-Saved { param($s) !$s.GlobalTunEnabled }
    }
    Case 'core/only-footer-controls-process-lifetime' {
        Assert (@(Get-NetTCPConnection -LocalPort 10819 -State Listen -ErrorAction SilentlyContinue).Count -eq 0) 'Fixture port is occupied'
        Expand-Dns 'Application_Expander_ui-app'
        Ui @('invoke','启用应用代理') | Out-Null
        $saved = Wait-Saved { param($s) $s.Applications[0].IsEnabled }
        Start-Sleep -Milliseconds 2300 # Verify health monitoring leaves configured apps enabled while core is off.
        Assert ((Read-Settings).Applications[0].IsEnabled) 'Health monitor reset app config while core off'
        Assert (!(Test-Path (Join-Path $taskRoot 'sing-box/config.json'))) 'Application preference started core'
        Ui @('invoke','启用应用代理') | Out-Null
        $saved = Wait-Saved { param($s) !$s.Applications[0].IsEnabled }
        Ui @('invoke','Core_Toggle') | Out-Null
        $listener = Wait-Listener $true
        $corePid = $listener[0].OwningProcess
        Assert ((Get-CimInstance Win32_Process -Filter "ProcessId=$corePid").CommandLine.Contains($taskRoot)) 'Listener does not belong to isolated test core'
        Ui @('invoke','启用应用代理') | Out-Null
        $saved = Wait-Saved { param($s) $s.Applications[0].IsEnabled }
        Ui @('invoke','启用应用代理') | Out-Null
        $saved = Wait-Saved { param($s) !$s.Applications[0].IsEnabled }
        Ui @('set-value','Proxy_PortTextBox','10829') | Out-Null
        Ui @('invoke','Proxy_Apply') | Out-Null
        $saved = Wait-Saved { param($s) $s.SystemProxyPort -eq 10829 }
        Assert ((Get-NetTCPConnection -LocalPort 10819 -State Listen).OwningProcess -contains $corePid) 'Proxy config restarted or stopped core'
        Assert (@(Get-NetTCPConnection -LocalPort 10829 -State Listen -ErrorAction SilentlyContinue).Count -eq 0) 'Preference hot-applied unexpectedly'
        Start-Sleep -Milliseconds 400
        Ui @('screenshot','-o',(Join-Path $taskRoot 'core-footer.png')) | Out-Null
        Ui @('invoke','Core_Toggle') | Out-Null
        Wait-Listener $false | Out-Null
        Assert (!(Get-Process -Id $corePid -ErrorAction SilentlyContinue)) 'Footer stop left core running'
        Ui @('invoke','Core_Toggle') | Out-Null
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do { $newListener = @(Get-NetTCPConnection -LocalPort 10829 -State Listen -ErrorAction SilentlyContinue); if ($newListener.Count) { break }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $deadline)
        Assert ($newListener.Count -gt 0) 'Footer start did not apply saved port'
        Ui @('invoke','Core_Toggle') | Out-Null
        Ui @('wait-for','Core_Toggle','--property','IsEnabled','--value','true') | Out-Null
        Assert (!(Read-Settings).SystemProxyEnabled) 'Core switch enabled Windows proxy'
    }
    Case 'configuration/four-flat-actions-without-advanced-mode' {
        Ui @('invoke','Navigation_Configuration') | Out-Null
        Ui @('wait-for','Configuration_Check') | Out-Null
        $nodes = Tree
        foreach ($id in @('Configuration_EditJson','Configuration_Import','Configuration_Export','Configuration_Check')) {
            Assert (@($nodes | Where-Object automationId -eq $id).Count -eq 1) "Missing configuration action: $id"
        }
        foreach ($id in @('Configuration_TUN','Configuration_Core','Configuration_Advanced','Configuration_Actions','Configuration_Native','Configuration_Mode','Configuration_JsonEditor')) {
            Assert (@($nodes | Where-Object automationId -eq $id).Count -eq 0) "Old configuration UI remains: $id"
        }
        Assert (!(Test-Path (Join-Path $taskRoot 'client-errors.log'))) 'Client recorded an exception'
        Start-Sleep -Milliseconds 400 # Allow the expand animation to settle before capturing layout.
        Ui @('screenshot','-o',(Join-Path $taskRoot 'configuration-expanded.png')) | Out-Null
    }
    Case 'configuration/external-file-edit-check-and-roundtrip' {
        Ui @('invoke','Navigation_Configuration') | Out-Null
        Ui @('invoke','Configuration_EditJson') | Out-Null
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        while (!(Test-Path (Join-Path $taskRoot 'editor-request.json')) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        $path = (Get-Content (Join-Path $taskRoot 'editor-request.json') -Raw | ConvertFrom-Json).path
        $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        Assert ($json.dns.servers[1].server -eq (Read-Settings).Configuration.ProxyDns.Server) 'External file does not contain current generated config'
        Assert ($json.'$schema' -eq './schema.json' -and (Test-Path (Join-Path (Split-Path $path) 'schema.json'))) 'External editor has no offline schema'
        Assert (@(Tree | Where-Object automationId -eq 'Configuration_JsonEditor').Count -eq 0) 'Inline editor still present'
        $saved = Wait-Saved { param($s) $s.Configuration.ExternalFilePath -eq $path -and !$s.Configuration.NativeEnabled }
        $hash = (Get-FileHash -LiteralPath $taskSettings).Hash
        '{"unknown_ui_test_field":true}' | Set-Content -LiteralPath $path
        Ui @('invoke','Configuration_Check') | Out-Null
        Ui @('wait-for','unknown field','--type','Text') | Out-Null
        Assert ((Get-FileHash -LiteralPath $taskSettings).Hash -eq $hash) 'Invalid external config changed saved settings'
        $json.log.level = 'warn'
        $json | ConvertTo-Json -Depth 60 | Set-Content -LiteralPath $path
        Ui @('invoke','Configuration_Check') | Out-Null
        Ui @('wait-for','配置校验通过。','--type','Text') | Out-Null
        $actual = Get-Content (Join-Path $taskRoot 'sing-box/config.validation.json') -Raw | ConvertFrom-Json
        Assert ($actual.log.level -eq 'warn') 'Core check did not read edited file'
        $json.log.level = 'error'
        $json | ConvertTo-Json -Depth 60 | Set-Content -LiteralPath $path
        Ui @('invoke','Core_Toggle') | Out-Null
        Ui @('wait-for','Core_Toggle','--property','IsEnabled','--value','true') | Out-Null
        $actual = Get-Content (Join-Path $taskRoot 'sing-box/config.json') -Raw | ConvertFrom-Json
        Assert ($actual.log.level -eq 'error') 'Core start used stale JSON instead of external file'
        Ui @('invoke','Core_Toggle') | Out-Null
        Ui @('wait-for','Core_Toggle','--property','IsEnabled','--value','true') | Out-Null
        Ui @('invoke','Navigation_Servers') | Out-Null
        Ui @('invoke','Navigation_Configuration') | Out-Null
        Ui @('invoke','Configuration_EditJson') | Out-Null
        Assert ((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).log.level -eq 'error') 'Edit button overwrote user file'
        Assert (!(Test-Path (Join-Path $taskRoot 'client-errors.log'))) 'External editor flow recorded exception'
        Start-Sleep -Milliseconds 400
        Ui @('screenshot','-o',(Join-Path $taskRoot 'external-editor.png')) | Out-Null
    }
    Case 'configuration/import-export-and-form-sync' {
        $saved = Read-Settings
        $path = $saved.Configuration.ExternalFilePath
        $original = Get-Content -LiteralPath $path -Raw
        '{"unknown_import_field":true}' | Set-Content -LiteralPath $env:FLUENTSINGBOX_TEST_IMPORT_JSON
        Ui @('invoke','Configuration_Import') | Out-Null
        Ui @('wait-for','unknown field','--type','Text') | Out-Null
        Assert ((Get-Content -LiteralPath $path -Raw) -eq $original) 'Invalid import changed managed source'
        $import = $original | ConvertFrom-Json
        $import.log.level = 'warn'
        $import | ConvertTo-Json -Depth 60 | Set-Content -LiteralPath $env:FLUENTSINGBOX_TEST_IMPORT_JSON
        Ui @('invoke','Configuration_Import') | Out-Null
        Ui @('wait-for','配置校验通过并已导入。后续编辑和启动使用应用自带文件；下次启动核心生效。','--type','Text') | Out-Null
        Assert ((Read-Settings).Configuration.ExternalFilePath -eq $path) 'Import used source file instead of managed file'
        Assert ((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).log.level -eq 'warn') 'Import did not replace managed contents'
        Assert (Test-Path -LiteralPath ($path + '.backup')) 'Import backup missing'
        Ui @('invoke','Configuration_Export') | Out-Null
        Ui @('wait-for','已导出应用配置文件。','--type','Text') | Out-Null
        Assert ((Get-Content -LiteralPath $env:FLUENTSINGBOX_TEST_EXPORT_JSON -Raw) -eq (Get-Content -LiteralPath $path -Raw)) 'Export differed from current source'
        Ui @('invoke','Navigation_Routing') | Out-Null
        Ui @('wait-for','Routing_final') | Out-Null
        Choice 'Routing_final' 'Routing_final_direct'
        Ui @('wait-for','配置已校验并保存，重启核心后生效。','--type','Text') | Out-Null

        $actual = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        Assert ($actual.route.final -eq 'direct' -and $actual.log.level -eq 'warn') 'Form change failed to update file or lost manual field'
        Ui @('invoke','Navigation_DNS') | Out-Null
        Ui @('wait-for','DNS_final','--property','IsEnabled','--value','true') | Out-Null
        Assert (!(Test-Path (Join-Path $taskRoot 'client-errors.log'))) 'Managed configuration flow recorded exception'
    }
}
catch {
    if ($taskWindow) { try { Ui @('screenshot','-o',(Join-Path $taskRoot 'failure.png')) | Out-Null } catch {} }
    throw
}
finally {
    $taskResults | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRoot 'results.json')
    Write-Output "UI evidence: $taskRoot"
    if ($taskProcess) { if (!$taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id }; $taskProcess.Dispose() }
    $env:FLUENTSINGBOX_DATA_DIR = $taskOldData; $env:FLUENTSINGBOX_TEST_BACKGROUND = $taskOldBackground; $env:FLUENTSINGBOX_TEST_NO_EDITOR = $taskOldEditor
    $env:FLUENTSINGBOX_TEST_IMPORT_JSON = $taskOldImport; $env:FLUENTSINGBOX_TEST_EXPORT_JSON = $taskOldExport
}
