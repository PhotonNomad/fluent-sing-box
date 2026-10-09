# Compatibility entry point; Release is a directory, no archive is generated.
[CmdletBinding()]
param([string]$ProjectRoot = (Resolve-Path "$PSScriptRoot/..").Path)
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/Build.ps1" -ProjectRoot $ProjectRoot -Configuration Release
