<#
  build.ps1 - restore + publish PC Cache Cleaner Pro to a single exe in dist\ (gitignored).
  Everything (SDK, NuGet caches) lives on F: via env.ps1. Run from the repo root:
      .\build.ps1            # Release build -> dist\CleanPC-Pro.exe
      .\build.ps1 -SelfTest  # also run the built-in tests (uses an asInvoker copy so no elevation is needed)
#>
param([switch]$SelfTest)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
. .\env.ps1

if (-not (Test-Path .\assets\icon.ico)) { & .\tools\make-ico.ps1 }

$proj = '.\src\CleanPC.Pro\CleanPC.Pro.csproj'
$pub  = '.\dist\publish'
if (Test-Path .\dist) { Remove-Item .\dist -Recurse -Force }
dotnet restore $proj
dotnet build $proj -c Release --no-restore --no-incremental -p:UseInvoker=false
if ($LASTEXITCODE -ne 0) { throw "build failed" }
dotnet publish $proj -c Release -o $pub --no-build -p:UseInvoker=false
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$extra = Get-ChildItem $pub -File | Where-Object { $_.Name -notin 'CleanPC-Pro.exe','CleanPC-Pro.exe.config' }
if ($extra) { Write-Warning ("Unexpected extra files in publish output: " + ($extra.Name -join ', ')) }
Copy-Item "$pub\CleanPC-Pro.exe" .\dist\CleanPC-Pro.exe -Force
$f = Get-Item .\dist\CleanPC-Pro.exe
"{0}  {1:N0} bytes  version {2}" -f $f.FullName, $f.Length, $f.VersionInfo.FileVersion
"SHA-256: " + (Get-FileHash $f.FullName -Algorithm SHA256).Hash

if ($SelfTest) {
    $tmp = Join-Path $env:TEMP 'CleanPCProSelfTestBuild'
    dotnet build $proj -c Release -p:UseInvoker=true --no-incremental -o $tmp | Out-Null
    $p = Start-Process "$tmp\CleanPC-Pro.exe" -ArgumentList '--selftest' -Wait -PassThru -RedirectStandardOutput "$tmp\selftest.txt"
    Get-Content "$tmp\selftest.txt" | Select-Object -Last 12
    # restore the requireAdministrator build outputs
    dotnet build $proj -c Release --no-incremental | Out-Null
    exit $p.ExitCode
}
