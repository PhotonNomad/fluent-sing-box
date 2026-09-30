param([string]$ProjectRoot = (Resolve-Path "$PSScriptRoot\..\..").Path, [switch]$Foreground, [string]$CaseFilter, [string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
$taskArtifacts = Join-Path $ProjectRoot 'artifacts\server-editor'
$taskRunRoot = Join-Path $taskArtifacts ('ui-run-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRunRoot | Out-Null
$taskSettingsPath = Join-Path $taskRunRoot 'settings.json'
Copy-Item -LiteralPath (Join-Path $taskArtifacts 'ui-data\settings.json') -Destination $taskSettingsPath
$taskSeed = Get-Content -LiteralPath $taskSettingsPath -Raw | ConvertFrom-Json
if ($taskSeed.SystemProxyEnabled -or @($taskSeed.Applications | Where-Object IsEnabled).Count) { throw 'UI fixtures must have all proxies disabled' }
$taskSeed.Applications = @([pscustomobject]@{Id='ui-app-group';Name='测试应用组';ServerId=$taskSeed.DefaultServerId;IsEnabled=$false;ExecutablePaths=@('C:\fixtures\example.exe')})
$taskSeed.RoutingRules = @([pscustomobject]@{Id='ui-custom-rule';Name='测试地址规则';Addresses='example.com';Outbound='direct';Scope='system';IsEnabled=$true;IsBuiltIn=$false})
$taskSeed | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $taskSettingsPath
$taskExecutable = Join-Path $ProjectRoot 'src\FluentSingBox.Client\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\fluent sing-box.exe'
if ($ExecutablePath) { $taskExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path }
$taskResults = [Collections.Generic.List[object]]::new()
$taskOldData = $env:FLUENTSINGBOX_DATA_DIR
$taskOldBackground = $env:FLUENTSINGBOX_TEST_BACKGROUND
$env:FLUENTSINGBOX_TEST_BACKGROUND = if ($Foreground) { '0' } else { '1' }
$env:FLUENTSINGBOX_DATA_DIR = $taskRunRoot
$script:taskProcess = $null
$script:taskWindow = $null
$taskSequence = 0
function Invoke-TestUi([string[]]$Arguments) {
    $script:taskSequence++
    $output = & winapp ui @Arguments --json 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    $output | Set-Content -LiteralPath (Join-Path $taskRunRoot ('command-{0:D3}.json' -f $script:taskSequence))
    $value = $output | ConvertFrom-Json
    if ($exitCode -ne 0 -or $value.error -or $value.timedOut) { throw "UI command failed: $($Arguments -join ' '): $output" }
    return $value
}
function Use-Ui([string[]]$Arguments) {
    $result = Invoke-TestUi ($Arguments + @('-w', "$script:taskWindow"))
    if ($Arguments[0] -eq 'set-value') {
        $read = Invoke-TestUi @('get-value', $Arguments[1], '-w', "$script:taskWindow")
        if (($read.text -replace '\r\n|\r', "`n") -ne ($Arguments[2] -replace '\r\n|\r', "`n")) {
            # Preserve the observed mismatch and allow one bounded retry of this exact value assignment.
            [pscustomobject]@{selector=$Arguments[1];expected=$Arguments[2];observed=$read.text;reason='value-assignment-mismatch';retry=1} |
                ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRunRoot ('value-mismatch-{0:D3}.json' -f $script:taskSequence))
            Invoke-TestUi ($Arguments + @('-w', "$script:taskWindow")) | Out-Null
            $read = Invoke-TestUi @('get-value', $Arguments[1], '-w', "$script:taskWindow")
            if (($read.text -replace '\r\n|\r', "`n") -ne ($Arguments[2] -replace '\r\n|\r', "`n")) { throw "Assigned value did not reach control $($Arguments[1])" }
        }
    }
    return $result
}
function Select-UiChoice([string]$ComboId, [string]$ItemId, [int]$Attempt = 0) {
    Use-Ui @('invoke', $ComboId, '--action', 'expand') | Out-Null
    $tree = Use-Ui @('inspect', '--depth', '16')
    function Find-UiOption($Nodes) {
        foreach ($node in $Nodes) {
            if ($node.automationId -eq $ItemId -and $node.type -eq 'ListItem') { $node }
            if ($node.children) { Find-UiOption $node.children }
        }
    }
    $matches = @(Find-UiOption $tree.windows.elements)
    if (!$matches.Count) { throw "Dropdown option missing: $ItemId" }
    try { Use-Ui @('invoke', $matches[0].selector, '--action', 'select') | Out-Null }
    catch {
        if ($Attempt -ne 0 -or $_.Exception.Message -notmatch 'element_not_found') { throw }
        [pscustomobject]@{combo=$ComboId;item=$ItemId;reason='popup-element-expired-between-inspect-and-select';retry=1} |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRunRoot ('popup-expired-{0:D3}.json' -f $script:taskSequence))
        Select-UiChoice $ComboId $ItemId 1
    }
}
function Open-Add {
    Use-Ui @('invoke', 'Servers_Add') | Out-Null
    Use-Ui @('wait-for', 'ServerEditor_Type') | Out-Null
}
function Open-Edit([string]$Id) {
    Expand-Server $Id
    Use-Ui @('invoke', "Server_Edit_$Id") | Out-Null
    Use-Ui @('wait-for', 'ServerEditor_Type') | Out-Null
}
function Expand-Server([string]$Id) { Expand-Settings "Server_Expander_$Id" }
function Expand-Settings([string]$Selector) {
    Use-Ui @('wait-for', $Selector) | Out-Null
    $tree = Use-Ui @('inspect', $Selector, '--depth', '3')
    function Find-InnerExpander($Nodes) {
        foreach ($node in $Nodes) {
            if ($node.className -eq 'Microsoft.UI.Xaml.Controls.Expander') { $node }
            if ($node.children) { Find-InnerExpander $node.children }
        }
    }
    $expander = @(Find-InnerExpander $tree.windows.elements)[0]
    if (!$expander) { throw "Inner expander not found: $Selector" }
    Use-Ui @('invoke', $expander.selector, '--action', 'expand') | Out-Null
}
function Assert-Ui([bool]$Value, [string]$Reason) { if (!$Value) { throw $Reason } }
function Run-UiCase([string]$Name, [scriptblock]$Action) {
    if ($CaseFilter -and $Name -notmatch $CaseFilter) { return }
    try { & $Action; $taskResults.Add([pscustomobject]@{name=$Name;status='passed'}); Write-Output "PASS $Name" }
    catch {
        $taskResults.Add([pscustomobject]@{name=$Name;status='failed';message=$_.Exception.Message})
        try { Use-Ui @('screenshot', '-o', (Join-Path $taskRunRoot 'failure.png')) | Out-Null } catch {}
        throw
    }
}
function Get-SavedSettings {
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try { return Get-Content -LiteralPath $taskSettingsPath -Raw | ConvertFrom-Json }
        catch [System.Management.Automation.ItemNotFoundException] {
            if ($attempt -eq 4) { throw }
            [pscustomobject]@{reason='settings-replacement-read-race';attempt=$attempt + 1} | ConvertTo-Json |
                Set-Content -LiteralPath (Join-Path $taskRunRoot ('settings-read-retry-{0:D3}-{1}.json' -f $script:taskSequence, $attempt))
            Start-Sleep -Milliseconds 100
        }
        catch [IO.IOException] { if ($attempt -eq 4) { throw }; Start-Sleep -Milliseconds 100 }
    }
}
function Wait-SavedSettings([scriptblock]$Condition) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        try { $settings = Get-SavedSettings; if (& $Condition $settings) { return $settings } }
        catch [IO.IOException] {} catch [ArgumentException] {}
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Timed out waiting for the requested saved state'
}
try {
    $script:taskProcess = Start-Process -FilePath $taskExecutable -WorkingDirectory (Split-Path $taskExecutable) -WindowStyle Normal -PassThru
    Invoke-TestUi @('wait-for', 'Servers_Add', '-a', "$($taskProcess.Id)") | Out-Null
    $taskWindows = @(Invoke-TestUi @('list-windows', '-a', "$($taskProcess.Id)"))
    $script:taskWindow = $taskWindows[0].hwnd
    if (!$script:taskWindow) { throw 'Test client window not found' }
    Run-UiCase 'add/protocol-selection-and-draft-retention' {
        Open-Add
        Use-Ui @('set-value', 'ServerEditor_Name', 'ui-added-vmess') | Out-Null
        Use-Ui @('set-value', 'ServerEditor_server', '127.0.0.1') | Out-Null
        Use-Ui @('set-value', 'ServerEditor_uuid', 'bf000d23-0752-40b4-affe-68f7707a9661') | Out-Null
        Select-UiChoice 'ServerEditor_Type' 'ServerEditor_Type_vmess'
        Use-Ui @('wait-for', 'ServerEditor_security') | Out-Null
        $address = Use-Ui @('get-value', 'ServerEditor_server')
        Assert-Ui ($address.text -eq '127.0.0.1') 'Address was not carried to VMess'
        Select-UiChoice 'ServerEditor_Type' 'ServerEditor_Type_hysteria2'
        Use-Ui @('wait-for', 'ServerEditor_password') | Out-Null
        Use-Ui @('screenshot', '-o', (Join-Path $taskRunRoot 'add-hysteria2.png')) | Out-Null
        Select-UiChoice 'ServerEditor_Type' 'ServerEditor_Type_vless'
        $id = Use-Ui @('get-value', 'ServerEditor_uuid')
        Assert-Ui ($id.text -eq 'bf000d23-0752-40b4-affe-68f7707a9661') 'Switching back lost the VLESS draft'
        Select-UiChoice 'ServerEditor_Type' 'ServerEditor_Type_vmess'
    }
    Run-UiCase 'add/inline-error-keeps-dialog-open' {
        Use-Ui @('set-value', 'ServerEditor_uuid', 'invalid') | Out-Null
        Use-Ui @('invoke', 'PrimaryButton') | Out-Null
        Use-Ui @('wait-for', 'ServerEditor_Error') | Out-Null
        Use-Ui @('wait-for', 'ServerEditor_Type') | Out-Null
        Assert-Ui (@((Get-SavedSettings).Servers).Count -eq 11) 'Invalid input created a server'
        Use-Ui @('screenshot', '-o', (Join-Path $taskRunRoot 'validation-error.png')) | Out-Null
    }
    Run-UiCase 'add/vmess-websocket-save' {
        Use-Ui @('set-value', 'ServerEditor_uuid', 'bf000d23-0752-40b4-affe-68f7707a9661') | Out-Null
        Select-UiChoice 'ServerEditor_security' 'ServerEditor_Choice_security_none'
        Select-UiChoice 'ServerEditor__transport' 'ServerEditor_Choice__transport_ws'
        Use-Ui @('set-value', 'ServerEditor_transport_path', '/ui-ws') | Out-Null
        Use-Ui @('set-value', 'ServerEditor_transport_headers_Host', 'fixture.example') | Out-Null
        Use-Ui @('inspect', '--depth', '14') | Out-Null
        Use-Ui @('screenshot', '-o', (Join-Path $taskRunRoot 'add-vmess.png')) | Out-Null
        Use-Ui @('invoke', 'PrimaryButton') | Out-Null
        Use-Ui @('wait-for', 'ServerEditor_Dialog', '--gone') | Out-Null
        $saved = Wait-SavedSettings { param($settings) @($settings.Servers | Where-Object Name -eq 'ui-added-vmess').Count -eq 1 }
        $created = $saved.Servers | Where-Object Name -eq 'ui-added-vmess'
        Assert-Ui ($null -ne $created) 'Added server did not persist'
        $outbound = $created.OutboundJson | ConvertFrom-Json
        Assert-Ui ($outbound.type -eq 'vmess' -and $outbound.security -eq 'none' -and $outbound.transport.path -eq '/ui-ws') 'Saved protocol fields are incorrect'
    }
    Run-UiCase 'edit/vmess-prefill-save-and-reference-preservation' {
        $source = (Get-SavedSettings).Servers | Where-Object Name -eq 'fixture-vmess'
        Open-Edit $source.Id
        $before = Use-Ui @('get-value', 'ServerEditor_uuid')
        Assert-Ui ($before.text -eq 'bf000d23-0752-40b4-affe-68f7707a9661') 'Editing VMess did not fill UUID'
        Use-Ui @('set-value', 'ServerEditor_Name', 'ui-edited-vmess') | Out-Null
        Use-Ui @('set-value', 'ServerEditor_server_port', '8443') | Out-Null
        Use-Ui @('screenshot', '-o', (Join-Path $taskRunRoot 'edit-vmess.png')) | Out-Null
        Use-Ui @('invoke', 'PrimaryButton') | Out-Null
        Use-Ui @('wait-for', 'ServerEditor_Dialog', '--gone') | Out-Null
        $settings = Wait-SavedSettings { param($settings) @($settings.Servers | Where-Object { $_.Id -eq $source.Id -and $_.Port -eq 8443 -and $_.Name -eq 'ui-edited-vmess' }).Count -eq 1 }
        $saved = $settings.Servers | Where-Object Id -eq $source.Id
        Assert-Ui ($saved.Name -eq 'ui-edited-vmess' -and $saved.Port -eq 8443) 'Editing did not persist by original ID'
    }
    Run-UiCase 'edit/cancel-leaves-settings-unchanged' {
        $source = (Get-SavedSettings).Servers | Where-Object Name -eq 'fixture-trojan'
        $before = $source | ConvertTo-Json -Depth 20 -Compress
        Open-Edit $source.Id
        Use-Ui @('set-value', 'ServerEditor_Name', 'must-not-persist') | Out-Null
        Use-Ui @('invoke', 'CloseButton') | Out-Null
        Use-Ui @('wait-for', 'ServerEditor_Dialog', '--gone') | Out-Null
        $after = (Get-SavedSettings).Servers | Where-Object Id -eq $source.Id | ConvertTo-Json -Depth 20 -Compress
        Assert-Ui ($before -eq $after) 'Cancel changed saved settings'
    }
    Run-UiCase 'edit/optional-advanced-json-retains-unmapped-fields' {
        $source = (Get-SavedSettings).Servers | Where-Object Name -eq 'ui-edited-vmess'
        Open-Edit $source.Id
        Use-Ui @('invoke', 'ServerEditor_Advanced', '--action', 'expand') | Out-Null
        Use-Ui @('invoke', 'ServerEditor_AdvancedMode', '--action', 'toggle-on') | Out-Null
        $outbound = $source.OutboundJson | ConvertFrom-Json
        $outbound | Add-Member -NotePropertyName connect_timeout -NotePropertyValue '7s' -Force
        Use-Ui @('set-value', 'ServerEditor_JsonPreview', ($outbound | ConvertTo-Json -Depth 20 -Compress)) | Out-Null
        Use-Ui @('invoke', 'PrimaryButton') | Out-Null
        Use-Ui @('wait-for', 'ServerEditor_Dialog', '--gone') | Out-Null
        $saved = Wait-SavedSettings { param($settings) @($settings.Servers | Where-Object { $_.Id -eq $source.Id -and ($_.OutboundJson | ConvertFrom-Json).connect_timeout -eq '7s' }).Count -eq 1 }
        Assert-Ui (@($saved.Servers).Count -eq 12) 'Advanced editing duplicated a server'
    }
    Run-UiCase 'add/all-eleven-types-open-structured-fields' {
        Open-Add
        $types = @{
            vless='uuid'; vmess='security'; trojan='password'; shadowsocks='method';
            hysteria2='up_mbps'; tuic='congestion_control'; anytls='password';
            hysteria='auth_str'; http='username'; socks='version'; naive='username'
        }
        foreach ($type in @('vless','vmess','trojan','shadowsocks','hysteria2','tuic','anytls','hysteria','http','socks','naive')) {
            Select-UiChoice 'ServerEditor_Type' "ServerEditor_Type_$type"
            Use-Ui @('wait-for', "ServerEditor_$($types[$type])") | Out-Null
        }
        Use-Ui @('invoke', 'CloseButton') | Out-Null
        Use-Ui @('wait-for', 'ServerEditor_Dialog', '--gone') | Out-Null
        Assert-Ui (@((Get-SavedSettings).Servers).Count -eq 12) 'Opening forms created extra servers'
    }
    Run-UiCase 'edit/change-protocol-keeps-default-reference' {
        $source = (Get-SavedSettings).Servers | Where-Object Name -eq 'fixture-vless'
        Open-Edit $source.Id
        Select-UiChoice 'ServerEditor_Type' 'ServerEditor_Type_http'
        Use-Ui @('invoke', 'PrimaryButton') | Out-Null
        Use-Ui @('wait-for', 'ServerEditor_Dialog', '--gone') | Out-Null
        $settings = Wait-SavedSettings { param($settings) @($settings.Servers | Where-Object { $_.Id -eq $source.Id -and ($_.OutboundJson | ConvertFrom-Json).type -eq 'http' }).Count -eq 1 }
        Assert-Ui ($settings.DefaultServerId -eq $source.Id) 'Protocol change broke default reference'
        $outbound = ($settings.Servers | Where-Object Id -eq $source.Id).OutboundJson | ConvertFrom-Json
        Assert-Ui (!$outbound.uuid -and !$outbound.flow -and !$outbound.transport) 'Old protocol credentials/options leaked into HTTP'
    }
    Run-UiCase 'session/restart-retains-added-and-edited-servers' {
        $tree = Use-Ui @('inspect', '--depth', '2')
        $close = @($tree.windows.elements.children.children | Where-Object { $_.automationId -eq 'Close' -and !$_.isOffscreen })[0]
        if (!$close) { throw 'Visible window close button not found' }
        Use-Ui @('invoke', $close.selector) | Out-Null
        $taskProcess.WaitForExit(5000) | Out-Null
        $script:taskProcess = Start-Process -FilePath $taskExecutable -WorkingDirectory (Split-Path $taskExecutable) -WindowStyle Normal -PassThru
        Invoke-TestUi @('wait-for', 'Servers_Add', '-a', "$($taskProcess.Id)") | Out-Null
        $script:taskWindow = @(Invoke-TestUi @('list-windows', '-a', "$($taskProcess.Id)"))[0].hwnd
        $saved = Get-SavedSettings
        Assert-Ui (@($saved.Servers).Count -eq 12) 'Restart lost added server'
        $source = $saved.Servers | Where-Object Name -eq 'ui-edited-vmess'
        $tree = Use-Ui @('inspect', '--depth', '14')
        function Find-RestartedRow($Nodes) {
            foreach ($node in $Nodes) {
                if ($node.type -eq 'Group' -and $node.name -eq 'ui-edited-vmess') { $node }
                if ($node.children) { Find-RestartedRow $node.children }
            }
        }
        function Get-RowNames($Nodes) {
            foreach ($node in $Nodes) { $node.name; if ($node.children) { Get-RowNames $node.children } }
        }
        $rows = @(Find-RestartedRow $tree.windows.elements)
        Assert-Ui ($rows.Count -gt 0) 'Restarted UI did not display the edited server'
        $names = @(Get-RowNames $rows[0].children)
        Assert-Ui ($names -contains '127.0.0.1:8443') 'Restarted UI did not display the saved port'
        Use-Ui @('screenshot', '-o', (Join-Path $taskRunRoot 'restarted-list.png')) | Out-Null
    }
    Run-UiCase 'import/preview-partial-valid-lines-and-cancel' {
        Use-Ui @('invoke', 'Servers_Import') | Out-Null
        Use-Ui @('wait-for', 'Servers_ImportInput') | Out-Null
        Use-Ui @('set-value', 'Servers_ImportInput', "http://fixture-user:fixture-secret@127.0.0.1:8080#ui-preview`ninvalid://secret@127.0.0.1") | Out-Null
        $tree = Use-Ui @('inspect', '--depth', '16')
        $json = $tree | ConvertTo-Json -Depth 50
        Assert-Ui ($json.Contains('ui-preview') -and $json.Contains('无法识别 1 项')) 'Import preview did not separate valid/invalid entries'
        Assert-Ui (!$json.Contains('无法导入：invalid://secret')) 'Import error exposed a share link'
        Use-Ui @('invoke', 'CloseButton') | Out-Null
        Assert-Ui (@((Get-SavedSettings).Servers).Count -eq 12) 'Canceled preview created servers'
    }
    Run-UiCase 'logs/read-filter-pause-clear-with-redaction' {
        $logDirectory = Join-Path $taskRunRoot 'sing-box'
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $logDirectory 'sing-box.log') -Value "INFO fixture-start`nERROR fixture-password node failed" -Encoding utf8
        Use-Ui @('invoke', 'Navigation_Logs', '--action', 'select') | Out-Null
        Use-Ui @('wait-for', 'Logs_Content') | Out-Null
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do { $read = Use-Ui @('get-value', 'Logs_Content'); if ($read.text.Contains('fixture-start')) { break }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $deadline)
        Assert-Ui ($read.text.Contains('fixture-start')) 'Log refresh did not load fixture'
        Assert-Ui ($read.text.Contains('[REDACTED_SECRET]') -and !$read.text.Contains('fixture-password')) 'Log credentials were not redacted'
        Use-Ui @('set-value', 'Logs_Filter', 'ERROR') | Out-Null
        $read = Use-Ui @('get-value', 'Logs_Content')
        Assert-Ui ($read.text.Contains('ERROR') -and !$read.text.Contains('INFO')) 'Log filter failed'
        Use-Ui @('invoke', 'Logs_Pause', '--action', 'toggle-on') | Out-Null
        Use-Ui @('invoke', 'Logs_Clear') | Out-Null
        $read = Use-Ui @('get-value', 'Logs_Content')
        Assert-Ui ([string]::IsNullOrEmpty($read.text)) 'Clear did not empty display'
        Assert-Ui ((Get-Content -LiteralPath (Join-Path $logDirectory 'sing-box.log') -Raw).Contains('fixture-start')) 'Clear deleted core log'
        Use-Ui @('screenshot', '-o', (Join-Path $taskRunRoot 'logs.png')) | Out-Null
    }
    Run-UiCase 'server-row/management-in-expanded-children-copy-delete' {
        Use-Ui @('invoke', 'Navigation_Servers', '--action', 'select') | Out-Null
        Use-Ui @('wait-for', 'Servers_Add') | Out-Null
        $currentSettings = Get-SavedSettings
        $initialServerCount = @($currentSettings.Servers).Count
        $source = $currentSettings.Servers | Where-Object Id -eq $currentSettings.DefaultServerId
        $tree = Use-Ui @('inspect', '--depth', '16')
        Assert-Ui (!(($tree | ConvertTo-Json -Depth 60).Contains('Server_More_'))) 'Old parent-row more button remains'
        Expand-Server $source.Id
        Use-Ui @('wait-for', "Server_Edit_$($source.Id)") | Out-Null
        Use-Ui @('wait-for', "Server_Delete_$($source.Id)") | Out-Null
        Use-Ui @('screenshot', '-o', (Join-Path $taskRunRoot 'server-child-actions.png')) | Out-Null
        Use-Ui @('invoke', "Server_Copy_$($source.Id)") | Out-Null
        $saved = Wait-SavedSettings { param($settings) @($settings.Servers).Count -eq $initialServerCount + 1 }
        $copy = $saved.Servers | Where-Object Name -eq ($source.Name + '（副本）')
        $copyJson = $copy.OutboundJson | ConvertFrom-Json | ConvertTo-Json -Depth 40 -Compress
        $sourceJson = $source.OutboundJson | ConvertFrom-Json | ConvertTo-Json -Depth 40 -Compress
        Assert-Ui ($copy.Id -ne $source.Id -and $copyJson -eq $sourceJson) 'Copy lost fields or reused identity'
        Use-Ui @('scroll', 'PageScrollViewer', '--to', 'bottom') | Out-Null
        Expand-Server $copy.Id
        Use-Ui @('wait-for', "Server_Delete_$($copy.Id)") | Out-Null
        Use-Ui @('invoke', "Server_Delete_$($copy.Id)") | Out-Null
        Use-Ui @('invoke', 'CloseButton') | Out-Null
        Use-Ui @('wait-for', 'CloseButton', '--gone') | Out-Null
        Assert-Ui (@((Get-SavedSettings).Servers).Count -eq $initialServerCount + 1) 'Cancel deleted node'
        Use-Ui @('invoke', "Server_Delete_$($copy.Id)") | Out-Null
        Use-Ui @('invoke', 'PrimaryButton') | Out-Null
        Wait-SavedSettings { param($settings) @($settings.Servers).Count -eq $initialServerCount } | Out-Null
        $taskWindowsFinal = @(Invoke-TestUi @('list-windows', '-a', "$($taskProcess.Id)"))
        [pscustomobject]@{backgroundRequested= !$Foreground; startupForeground=$taskWindows[0].isForeground; finalForeground=$taskWindowsFinal[0].isForeground} |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRunRoot 'foreground-observation.json')
    }
    Run-UiCase 'routing/child-controls-toggle-persist-and-details-link' {
        Use-Ui @('invoke', 'Navigation_Routing') | Out-Null
        Expand-Settings 'Route_Expander_ui-custom-rule'
        Use-Ui @('wait-for', 'Route_Details_ui-custom-rule') | Out-Null
        $link = Use-Ui @('inspect', 'Route_Details_ui-custom-rule', '--depth', '1')
        Assert-Ui (($link | ConvertTo-Json -Depth 20).Contains('Hyperlink')) 'Details link is not a hyperlink'
        Use-Ui @('invoke', 'Route_Toggle_ui-custom-rule', '--action', 'toggle-off') | Out-Null
        Wait-SavedSettings { param($settings) !($settings.RoutingRules | Where-Object Id -eq 'ui-custom-rule').IsEnabled } | Out-Null
        Use-Ui @('invoke', 'Route_Toggle_ui-custom-rule', '--action', 'toggle-on') | Out-Null
        Wait-SavedSettings { param($settings) ($settings.RoutingRules | Where-Object Id -eq 'ui-custom-rule').IsEnabled } | Out-Null
        Use-Ui @('screenshot', '-o', (Join-Path $taskRunRoot 'routing-child-controls.png')) | Out-Null
    }
    Run-UiCase 'proxy/system-inline-port-validation-and-save' {
        Use-Ui @('invoke', 'Navigation_Proxy') | Out-Null
        Expand-Settings 'Proxy_SystemExpander'
        Use-Ui @('wait-for', 'Proxy_PortTextBox') | Out-Null
        Use-Ui @('set-value', 'Proxy_PortTextBox', '70000') | Out-Null
        Use-Ui @('invoke', 'Proxy_Apply') | Out-Null
        Use-Ui @('wait-for', 'CloseButton') | Out-Null
        Assert-Ui ((Get-SavedSettings).SystemProxyPort -eq 10809) 'Invalid port was saved'
        Use-Ui @('invoke', 'CloseButton') | Out-Null
        Use-Ui @('wait-for', 'CloseButton', '--gone') | Out-Null
        Use-Ui @('set-value', 'Proxy_PortTextBox', '12089') | Out-Null
        Use-Ui @('invoke', 'Proxy_Apply') | Out-Null
        Wait-SavedSettings { param($settings) $settings.SystemProxyPort -eq 12089 } | Out-Null
        Assert-Ui (!(Get-SavedSettings).SystemProxyEnabled) 'Saving settings enabled host proxy'
        Use-Ui @('screenshot', '-o', (Join-Path $taskRunRoot 'proxy-inline-settings.png')) | Out-Null
    }
    Run-UiCase 'proxy/application-child-controls-and-editor' {
        Use-Ui @('scroll', 'PageScrollViewer', '--to', 'bottom') | Out-Null
        Expand-Settings 'Application_Expander_ui-app-group'
        Use-Ui @('wait-for', 'Application_Server_ui-app-group') | Out-Null
        Use-Ui @('wait-for', 'Application_Delete_ui-app-group') | Out-Null
        Use-Ui @('invoke', 'Application_Edit_ui-app-group') | Out-Null
        Use-Ui @('wait-for', 'ApplicationEditor_Name') | Out-Null
        Use-Ui @('set-value', 'ApplicationEditor_Name', '已编辑的应用组') | Out-Null
        Use-Ui @('invoke', 'PrimaryButton') | Out-Null
        Use-Ui @('wait-for', 'ApplicationEditor_Name', '--gone') | Out-Null
        Wait-SavedSettings { param($settings) ($settings.Applications | Where-Object Id -eq 'ui-app-group').Name -eq '已编辑的应用组' } | Out-Null
        Assert-Ui (!(Get-SavedSettings).Applications[0].IsEnabled) 'Editing enabled TUN'
        Use-Ui @('screenshot', '-o', (Join-Path $taskRunRoot 'application-child-controls.png')) | Out-Null
    }
    Write-Output "UI regression passed; evidence: $taskRunRoot"
}
finally {
    $taskResults | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskRunRoot 'ui-results.json')
    $env:FLUENTSINGBOX_DATA_DIR = $taskOldData
    $env:FLUENTSINGBOX_TEST_BACKGROUND = $taskOldBackground
    if ($taskProcess -and !$taskProcess.HasExited -and $taskProcess.Path -eq $taskExecutable) { Stop-Process -Id $taskProcess.Id }
}
