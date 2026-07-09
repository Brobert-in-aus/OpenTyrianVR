# Builds the otyr host test harness with the VS C compiler.
$ErrorActionPreference = 'Stop'
$vsroot = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath
Import-Module "$vsroot\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vsroot -DevCmdArguments '-arch=x64' -SkipAutomaticLocation | Out-Null
Set-Location $PSScriptRoot
cl /nologo /W4 otyr_host_harness.c /Fe:otyr_host_harness.exe
