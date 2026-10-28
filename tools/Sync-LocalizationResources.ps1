param([string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/..").Path)
$ErrorActionPreference = 'Stop'
$taskClient = Join-Path $ProjectRoot 'src/FluentSingBox.Client'
foreach ($language in @('en-US','zh-CN')) {
    $source = if ($language -eq 'en-US') { 'Resources/Strings.resx' } else { 'Resources/Strings.zh-CN.resx' }
    $doc = [xml](Get-Content -LiteralPath (Join-Path $taskClient $source) -Raw)
    foreach ($entry in @($doc.root.data)) {
        if (!$entry.name.Contains('.')) { [void]$doc.root.RemoveChild($entry) }
    }
    $directory = Join-Path $taskClient ('Strings/' + $language)
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $settings = [Xml.XmlWriterSettings]::new(); $settings.Indent=$true; $settings.Encoding=[Text.UTF8Encoding]::new($false)
    $settings.NewLineChars = "`n"
    $writer = [Xml.XmlWriter]::Create((Join-Path $directory 'Resources.resw'),$settings)
    try { $doc.Save($writer) } finally { $writer.Dispose() }
}
Write-Output 'WinUI resw resources synchronized from resx catalogs.'
