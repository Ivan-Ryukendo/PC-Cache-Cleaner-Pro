# Dot-source before building:  . .\env.ps1
# Keeps the whole toolchain and every cache on drive F: (nothing on C:). Session-level only.
$global:ProDevRoot = 'F:\Dev'
$env:DOTNET_ROOT          = 'F:\Dev\tools\dotnet'
$env:DOTNET_CLI_HOME      = 'F:\Dev\tools\dotnet-home'
$env:NUGET_PACKAGES       = 'F:\Dev\tools\nuget\packages'
$env:NUGET_HTTP_CACHE_PATH= 'F:\Dev\tools\nuget\http'
$env:NUGET_PLUGINS_CACHE_PATH = 'F:\Dev\tools\nuget\plugins-cache'
$env:DOTNET_NOLOGO        = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
foreach ($d in $env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES,$env:NUGET_HTTP_CACHE_PATH,$env:NUGET_PLUGINS_CACHE_PATH) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Force $d | Out-Null }
}
if (($env:PATH -split ';') -notcontains 'F:\Dev\tools\dotnet') { $env:PATH = 'F:\Dev\tools\dotnet;' + $env:PATH }
# TEMP/TMP on F: too, so test runs and child tools never write to C:
$env:TEMP = 'F:\Dev\tools\tmp'; $env:TMP = $env:TEMP
if (-not (Test-Path $env:TEMP)) { New-Item -ItemType Directory -Force $env:TEMP | Out-Null }
