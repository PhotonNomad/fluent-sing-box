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
public static class LogTestWindow {
 [DllImport("user32.dll",SetLastError=true)] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int width,int height,uint flags);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint message,UIntPtr w,IntPtr l);
}
"@
$taskDesktopRuntime = Join-Path (Split-Path (Get-Command dotnet).Source) 'shared/Microsoft.WindowsDesktop.App'
$taskDesktopRuntime = (Get-ChildItem $taskDesktopRuntime -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).FullName
Add-Type -Path (Join-Path $taskDesktopRuntime 'UIAutomationTypes.dll')
Add-Type -Path (Join-Path $taskDesktopRuntime 'UIAutomationClient.dll')
function Scroll-Pattern {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr][long]$taskWindow)
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'VerticalScrollBar')
    $bar = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    Assert ($null -ne $bar) 'Missing log vertical scrollbar'
    return $bar.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
}
function Scroll-Value { return (Scroll-Pattern).Current.Value }
try {
    $directory=Join-Path $taskRoot 'data'; New-Item -ItemType Directory -Path $directory | Out-Null
    @{LanguageChoice='en-US';SingBoxExecutablePath=(Join-Path $ProjectRoot 'artifacts/server-editor/core/sing-box.exe');Servers=@(@{Id='lang-node';Name='Log fixture';OutboundJson='{"type":"socks","server":"127.0.0.1","server_port":1}'});DefaultServerId='lang-node';SystemProxyEnabled=$false;Applications=@()} | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $directory 'settings.json')
    Start-Fixture $directory
    $logDirectory=Join-Path $directory 'sing-box'
    $long='INFO LONG_LINE_START '+('sample-log-segment ' * 90)+' LONG_LINE_END'
    $lines=@($long)+@(1..160 | ForEach-Object {"INFO line $_"})+@('ERROR fixture-error')
    Set-Content (Join-Path $logDirectory 'sing-box.log') ($lines -join "`n")
    Ui @('invoke','Navigation_Logs') | Out-Null; Ui @('wait-for','Logs_Content') | Out-Null
    $deadline=[DateTime]::UtcNow.AddSeconds(8)
    do {$value=(Ui @('get-value','Logs_Content')).text; if($value.Contains('LONG_LINE_END')){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
    Assert ($value.Contains('LONG_LINE_END')) 'Long log did not load completely'
    $bounds=[Collections.Generic.List[object]]::new()
    Case 'logs/wrap-and-fit-at-three-window-sizes' {
        foreach($size in @(@(1400,1000),@(1900,1300),@(2600,1600))) {
            Assert (!$taskProcess.HasExited) 'Test window exited'
            Assert ([LogTestWindow]::SetWindowPos([IntPtr][long]$taskWindow,[IntPtr]::Zero,0,0,$size[0],$size[1],0x16)) 'Fixture resize failed'
            Start-Sleep -Milliseconds 200
            $nodes=Tree
            $window=@($nodes | Where-Object {$_.type -eq 'Window' -and $_.name -eq 'fluent sing-box'})[0]
            $log=@($nodes | Where-Object automationId -eq 'Logs_Content')[0]
            Assert ($null -ne $window -and $null -ne $log) 'Missing window/log bounds'
            Assert ($log.width -gt 200 -and $log.height -gt 100) 'Log area collapsed'
            Assert ($log.x -ge $window.x -and $log.y -ge $window.y -and ($log.x+$log.width) -le ($window.x+$window.width+2) -and ($log.y+$log.height) -le ($window.y+$window.height+2)) 'Log area extends beyond window'
            Assert (@($nodes | Where-Object automationId -eq 'PageScrollViewer').Count -eq 0) 'Outer page scroll remains'
            $bounds.Add(@{window=$size;log=@{x=$log.x;y=$log.y;width=$log.width;height=$log.height}})
            Ui @('screenshot','-o',(Join-Path $taskRoot ('Logs-'+$size[0]+'.png'))) | Out-Null
        }
        Assert ($bounds[2].log.width -gt $bounds[0].log.width -and $bounds[2].log.height -gt $bounds[0].log.height) 'Log area did not grow with window'
        $bounds | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $taskRoot 'layout-bounds.json')
    }
    Case 'logs/refresh-retains-reading-position' {
        $pattern = Scroll-Pattern
        $pattern.SetValue($pattern.Current.Maximum / 2)
        Start-Sleep -Milliseconds 200
        $before = Scroll-Value
        Assert ($before -gt 0) 'Fixture did not scroll away from top'
        Add-Content (Join-Path $logDirectory 'sing-box.log') 'INFO REFRESH_POSITION_MARKER'
        $deadline = [DateTime]::UtcNow.AddSeconds(6)
        do {
            $value = (Ui @('get-value','Logs_Content')).text
            if ($value.Contains('REFRESH_POSITION_MARKER')) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        Assert ($value.Contains('REFRESH_POSITION_MARKER')) 'New log content did not refresh'
        Start-Sleep -Milliseconds 1200
        $after = Scroll-Value
        Assert ([Math]::Abs($after-$before) -lt 2) "Refresh moved viewport: $before -> $after"
        @{before=$before;after=$after} | ConvertTo-Json | Set-Content (Join-Path $taskRoot 'scroll-position.json')
    }
    Case 'logs/refresh-retains-text-selection' {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr][long]$taskWindow)
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'Logs_Content')
        $edit = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        $pattern = $edit.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
        $range = $pattern.DocumentRange.FindText('INFO line 100', $false, $false)
        Assert ($null -ne $range) 'Cannot select fixture log text'
        $range.Select()
        Add-Content (Join-Path $logDirectory 'sing-box.log') 'INFO SELECTION_REFRESH_MARKER'
        $deadline = [DateTime]::UtcNow.AddSeconds(6)
        do {
            $value = (Ui @('get-value','Logs_Content')).text
            if ($value.Contains('SELECTION_REFRESH_MARKER')) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        Assert ($value.Contains('SELECTION_REFRESH_MARKER')) 'Selection fixture did not refresh'
        Assert ($pattern.GetSelection()[0].GetText(-1) -eq 'INFO line 100') 'Log refresh lost selected text'
    }
    Case 'logs/filter-pause-clear-preserved' {
        Ui @('set-value','Logs_Filter','ERROR') | Out-Null
        $deadline=[DateTime]::UtcNow.AddSeconds(4)
        do {
            $value=(Ui @('get-value','Logs_Content')).text
            if ($value.Contains('fixture-error') -and !$value.Contains('LONG_LINE_START')) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        Assert ($value.Contains('fixture-error') -and !$value.Contains('LONG_LINE_START')) 'Filtering failed'
        Ui @('invoke','Logs_Pause','--action','toggle-on') | Out-Null
        Ui @('invoke','Logs_Clear') | Out-Null
        Assert ([string]::IsNullOrEmpty((Ui @('get-value','Logs_Content')).text)) 'Clear did not empty display'
        Assert ((Get-Content (Join-Path $logDirectory 'sing-box.log') -Raw).Contains('LONG_LINE_END')) 'Clear deleted source log'
        Assert (!(Test-Path (Join-Path $directory 'sing-box/process.json'))) 'Test started core'
    }
    Case 'navigation/proxy-before-routing-and-background-entry' {
        $nodes = Tree
        $proxy = @($nodes | Where-Object automationId -eq 'Navigation_Proxy')[0]
        $routing = @($nodes | Where-Object automationId -eq 'Navigation_Routing')[0]
        Assert ($proxy.y -lt $routing.y) 'Routing was not placed below proxy'
        $background = @($nodes | Where-Object automationId -eq 'App_Background')[0]
        Assert ($background.width -gt 0 -and $background.height -gt 0) 'Background entry missing'
    }
    Case 'tray/hide-restore-close-to-tray' {
        Ui @('invoke','App_Background') | Out-Null
        Start-Sleep -Milliseconds 250
        Assert (!$taskProcess.HasExited -and ![LogTestWindow]::IsWindowVisible([IntPtr][long]$taskWindow)) 'Background did not hide the window'
        [LogTestWindow]::PostMessage([IntPtr][long]$taskWindow,0x802A,[UIntPtr]1,[IntPtr]0x202) | Out-Null
        Start-Sleep -Milliseconds 250
        Assert ([LogTestWindow]::IsWindowVisible([IntPtr][long]$taskWindow)) 'Tray restore did not show the window'
        [LogTestWindow]::PostMessage([IntPtr][long]$taskWindow,0x112,[UIntPtr]0xF060,[IntPtr]0) | Out-Null
        Start-Sleep -Milliseconds 250
        Assert (!$taskProcess.HasExited -and ![LogTestWindow]::IsWindowVisible([IntPtr][long]$taskWindow)) 'Close did not retain background process'
        [LogTestWindow]::PostMessage([IntPtr][long]$taskWindow,0x802A,[UIntPtr]1,[IntPtr]0x202) | Out-Null
        Start-Sleep -Milliseconds 250
        Assert ([LogTestWindow]::IsWindowVisible([IntPtr][long]$taskWindow)) 'Second tray restore failed'
    }
    Case 'settings/core-description-alignment' {
        Ui @('invoke','Navigation_Settings') | Out-Null
        Ui @('wait-for','Settings_CoreDescription') | Out-Null
        $nodes = Tree
        $description = @($nodes | Where-Object automationId -eq 'Settings_CoreDescription')[0]
        $header = @($nodes | Where-Object { $_.type -eq 'Text' -and $_.name -eq 'Core executable' })[0]
        Assert ($null -ne $description -and $null -ne $header) 'Core header/description missing'
        Assert ([Math]::Abs($description.x-$header.x) -le 2 -and $description.y -gt $header.y) 'Core description does not align under its header'
        Ui @('screenshot','-o',(Join-Path $taskRoot 'Settings-Core.png')) | Out-Null
    }
}
catch {$taskResults.Add([pscustomobject]@{name='logs-layout';status='failed';message=$_.Exception.Message});throw}
finally {
    Stop-Fixture; $env:FLUENTSINGBOX_DATA_DIR=$taskOldData; $env:FLUENTSINGBOX_TEST_BACKGROUND=$taskOldBackground
    $taskResults | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $taskRoot 'results.json'); Write-Output "Log UI evidence: $taskRoot"
}
