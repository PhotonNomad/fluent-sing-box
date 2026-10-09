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


function Wait-Preference([bool]$Enabled) {
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    do {
        $settings=Get-Content (Join-Path $directory 'settings.json') -Raw | ConvertFrom-Json
        $found=$null -ne $settings.Configuration.TunPreferredRouteBackups.'app-tun'
        if($found -eq $Enabled){return}
        Start-Sleep -Milliseconds 100
    } while([DateTime]::UtcNow -lt $deadline)
    throw 'Preference state did not save'
}
try {
    $directory=Join-Path $taskRoot 'data'; New-Item -ItemType Directory -Path $directory | Out-Null
    @{LanguageChoice='en-US';SingBoxExecutablePath=(Join-Path $ProjectRoot 'artifacts/server-editor/core/sing-box.exe');Servers=@(@{Id='lang-node';Name='Schema fixture';OutboundJson='{"type":"socks","server":"127.0.0.1","server_port":1}'});DefaultServerId='lang-node';SystemProxyEnabled=$false;Applications=@()} | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $directory 'settings.json')
    $script:sourcePath=Join-Path $directory 'sing-box/user-config.json'
    Start-Fixture $directory
    Ui @('invoke','Navigation_Proxy') | Out-Null; Ui @('wait-for','Proxy_Tun_0_PreferRoute') | Out-Null
    Ui @('invoke','Proxy_Tun_Enabled','--action','toggle') | Out-Null
    Wait-Document {param($d) @($d.inbounds | Where-Object type -eq 'tun').Count -eq 1} | Out-Null
    Ui @('invoke','Navigation_Routing') | Out-Null; Ui @('wait-for','Routing_Save') | Out-Null
    $d=Get-Content $script:sourcePath -Raw | ConvertFrom-Json
    $tun=@($d.inbounds | Where-Object type -eq 'tun')[0]; $tun | Add-Member route_address @('10.0.0.0/8')
    $d | ConvertTo-Json -Depth 40 | Set-Content $script:sourcePath
    Ui @('invoke','Navigation_Proxy') | Out-Null; Ui @('wait-for','Proxy_Tun_0_PreferRoute') | Out-Null
    Case 'tun/prefer-route-auto-save-with-core-check' {
        Ui @('invoke','Proxy_Tun_0_PreferRoute','--action','toggle') | Out-Null
        Wait-Document {param($d) $t=@($d.inbounds | Where-Object type -eq 'tun')[0]; @($t.route_address).Count -eq 4 -and $t.route_address -contains '0.0.0.0/1' -and $t.route_address -contains '8000::/1' -and $t.auto_route} | Out-Null
        Wait-Preference $true
    }
    Case 'tun/preference-navigation-and-original-route-restoration' {
        Ui @('invoke','Navigation_Routing') | Out-Null; Ui @('wait-for','Routing_Save') | Out-Null
        Ui @('invoke','Navigation_Proxy') | Out-Null; Ui @('wait-for','Proxy_Tun_0_PreferRoute') | Out-Null
        Ui @('invoke','Proxy_Tun_0_PreferRoute','--action','toggle') | Out-Null
        Wait-Document {param($d) $t=@($d.inbounds | Where-Object type -eq 'tun')[0]; @($t.route_address).Count -eq 1 -and $t.route_address -contains '10.0.0.0/8'} | Out-Null
        Wait-Preference $false
    }
    Case 'tun/auto-route-off-clears-preference' {
        Ui @('invoke','Proxy_Tun_0_PreferRoute','--action','toggle') | Out-Null
        Wait-Document {param($d) @(@($d.inbounds | Where-Object type -eq 'tun')[0].route_address).Count -eq 4} | Out-Null
        Choice 'Proxy_Tun_0_auto_route' 'false'
        Wait-Document {param($d) $t=@($d.inbounds | Where-Object type -eq 'tun')[0]; !$t.auto_route -and @($t.route_address).Count -eq 1 -and $t.route_address -contains '10.0.0.0/8'} | Out-Null
        Wait-Preference $false
        Assert (!(Test-Path (Join-Path $directory 'sing-box/process.json'))) 'Preference editing started core'
        Assert (!(Test-Path (Join-Path $directory 'client-errors.log'))) 'Unhandled exception'
        Ui @('scroll','PageScrollViewer','--to','bottom') | Out-Null
        Ui @('screenshot','-o',(Join-Path $taskRoot 'Tun-Preference.png')) | Out-Null
    }
}
catch { $taskResults.Add([pscustomobject]@{name='tun-preference-ui';status='failed';message=$_.Exception.Message}); throw }
finally {
    Stop-Fixture; $env:FLUENTSINGBOX_DATA_DIR=$taskOldData; $env:FLUENTSINGBOX_TEST_BACKGROUND=$taskOldBackground
    $taskResults | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $taskRoot 'results.json'); Write-Output "UI evidence: $taskRoot"
}