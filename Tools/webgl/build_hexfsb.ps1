[CmdletBinding()]
param(
    [string]$SdkRoot = (Join-Path ${env:ProgramFiles(x86)} 'FMOD SoundSystem/FMOD Studio API Windows'),
    [string]$Output = (Join-Path $env:LOCALAPPDATA 'HexLive/FMOD/2.03.15/bin')
)

$ErrorActionPreference = 'Stop'
$api = Join-Path $SdkRoot 'api'
$core = Join-Path $api 'core'
$fsbank = Join-Path $api 'fsbank'
$fsbankInclude = Join-Path $fsbank 'inc'
$fsbankLib = Join-Path $fsbank 'lib/x64'
$coreHeader = Join-Path $core 'inc/fmod_common.h'
$coreDll = Join-Path $core 'lib/x64/fmod.dll'
$fsbankDll = Join-Path $fsbankLib 'fsbank.dll'
$importLibrary = Join-Path $fsbankLib 'fsbank_vc.lib'
$vorbisDll = Join-Path $fsbankLib 'libfsbvorbis64.dll'
foreach ($file in @($coreHeader, $coreDll, $fsbankDll, $importLibrary, $vorbisDll, (Join-Path $fsbankInclude 'fsbank.h'))) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Missing $file. Install FMOD Engine 2.03.15 for Windows or pass -SdkRoot to its installation directory."
    }
}
if (!(Select-String -LiteralPath $coreHeader -Pattern '^\s*#\s*define\s+FMOD_VERSION\s+0x00020315\b' -Quiet)) {
    throw "Expected FMOD Engine 2.03.15 in $SdkRoot."
}

if ($env:VSCMD_ARG_TGT_ARCH -ne 'x64') {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw 'Install Visual Studio Build Tools with Desktop development with C++ and a Windows SDK.'
    }
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (!$vs) {
        throw 'Install Visual Studio Build Tools with Desktop development with C++ and a Windows SDK.'
    }
    Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64'
}

$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$exe = Join-Path $Output 'hexfsb.exe'
$object = Join-Path $Output 'hexfsb.obj'
$compiler = Get-Command cl.exe -CommandType Application
try {
    & $compiler.Source /nologo /O2 /MT /W4 "/I$fsbankInclude" "/Fo$object" "/Fe$exe" `
        (Join-Path $PSScriptRoot 'hexfsb.c') $importLibrary
    if ($LASTEXITCODE -ne 0) {
        throw "MSVC failed to build hexfsb (exit $LASTEXITCODE)."
    }
}
finally {
    if (Test-Path -LiteralPath $object -PathType Leaf) {
        Remove-Item -LiteralPath $object
    }
}
Copy-Item -LiteralPath $fsbankDll, $vorbisDll, $coreDll -Destination $Output
Write-Output "Built $exe"
