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

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class FooterTrayWindow {
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint message,UIntPtr w,IntPtr l);
}
"@


try {
    $directory=Join-Path $taskRoot 'data';New-Item -ItemType Directory -Path $directory|Out-Null
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
    @{LanguageChoice='en-US';SingBoxExecutablePath=(Join-Path $ProjectRoot 'artifacts/server-editor/core/sing-box.exe');SystemProxyPort=$port;Servers=@(@{Id='node';Name='Footer fixture';OutboundJson='{"type":"socks","server":"127.0.0.1","server_port":1}'});DefaultServerId='node';GlobalTunEnabled=$false;SystemProxyEnabled=$false;Applications=@()}|ConvertTo-Json -Depth 20|Set-Content (Join-Path $directory 'settings.json')
    Start-Fixture $directory
    Case 'footer/compact-start-stop-button' {
        Ui @('wait-for','Core_Toggle','--property','Name','--value','Start sing-box')|Out-Null
        $button=@(Tree | Where-Object automationId -eq 'Core_Toggle')[0]
        Assert ($button.type -eq 'Button') 'Footer is not a compact button'
        Assert ($button.height -lt 110) 'Footer button too tall'
        Ui @('screenshot','-o',(Join-Path $taskRoot 'Footer-Stopped.png'))|Out-Null
    }
    Case 'footer/single-click-local-core-stop' {
        Ui @('invoke','Core_Toggle')|Out-Null
        Ui @('wait-for','Core_Toggle','--property','Name','--value','Stop sing-box','--timeout','15000')|Out-Null
        Ui @('wait-for','Core_Toggle','--property','IsEnabled','--value','true')|Out-Null
        $recordPath=Join-Path $directory 'sing-box/process.json'
        $record=Get-Content $recordPath -Raw|ConvertFrom-Json
        $core=Get-Process -Id $record.Pid
        Assert (!$core.HasExited) 'Fixture core did not run'
        Ui @('invoke','App_Background') | Out-Null
        Start-Sleep -Milliseconds 250
        $core.Refresh()
        Assert (!$core.HasExited -and ![FooterTrayWindow]::IsWindowVisible([IntPtr][long]$taskWindow)) 'Background stopped the local fixture core'
        [FooterTrayWindow]::PostMessage([IntPtr][long]$taskWindow,0x802A,[UIntPtr]1,[IntPtr]0x202) | Out-Null
        Start-Sleep -Milliseconds 250
        Assert ([FooterTrayWindow]::IsWindowVisible([IntPtr][long]$taskWindow)) 'Could not restore running core window'
        Ui @('screenshot','-o',(Join-Path $taskRoot 'Footer-Running.png'))|Out-Null
        Ui @('invoke','Core_Toggle')|Out-Null
        Ui @('wait-for','Core_Toggle','--property','Name','--value','Start sing-box','--timeout','15000')|Out-Null
        Ui @('wait-for','Core_Toggle','--property','IsEnabled','--value','true')|Out-Null
        $core.Refresh()
        Assert ($core.HasExited) 'One stop click did not terminate fixture core'
        Assert (!(Test-Path $recordPath)) 'Stopped fixture retained process record'
        Assert (@(Tree|Where-Object {$_.name -match 'Cannot switch sing-box|TUN_PRESENT_AFTER_STOP'}).Count -eq 0) 'Stop showed unexpected cleanup failure'
        $core.Dispose()
    }
    Case 'tray/exit-command-stops-local-core' {
        Ui @('invoke','Core_Toggle') | Out-Null
        Ui @('wait-for','Core_Toggle','--property','Name','--value','Stop sing-box','--timeout','15000') | Out-Null
        Ui @('wait-for','Core_Toggle','--property','IsEnabled','--value','true') | Out-Null
        $recordPath = Join-Path $directory 'sing-box/process.json'
        $record = Get-Content $recordPath -Raw | ConvertFrom-Json
        $core = Get-Process -Id $record.Pid
        Ui @('invoke','App_Background') | Out-Null
        Start-Sleep -Milliseconds 250
        [FooterTrayWindow]::PostMessage([IntPtr][long]$taskWindow,0x111,[UIntPtr]0xA002,[IntPtr]0) | Out-Null
        Assert ($taskProcess.WaitForExit(15000)) 'Tray Exit did not close fixture client'
        $core.Refresh()
        Assert ($core.HasExited -and !(Test-Path $recordPath)) 'Tray Exit retained fixture core'
        $saved = Get-Content (Join-Path $directory 'settings.json') -Raw | ConvertFrom-Json
        Assert (!$saved.CoreOnlyEnabled -and !$saved.SystemProxyEnabled) 'Tray Exit retained enabled settings'
        $core.Dispose()
    }
}
catch {$taskResults.Add([pscustomobject]@{name='footer-stop';status='failed';message=$_.Exception.Message});throw}
finally {
    Stop-Fixture;$env:FLUENTSINGBOX_DATA_DIR=$taskOldData;$env:FLUENTSINGBOX_TEST_BACKGROUND=$taskOldBackground
    $taskResults|ConvertTo-Json -Depth 8|Set-Content (Join-Path $taskRoot 'results.json');Write-Output "Footer UI evidence: $taskRoot"
}
