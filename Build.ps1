$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath)) {
    throw 'Install .NET Framework 4.8 to build AwakeGuard.'
}
$sourcePath = Join-Path $PSScriptRoot 'source\AwakeGuard.cs'
$manifestPath = Join-Path $PSScriptRoot 'source\awakeguard.manifest'
$iconPath = Join-Path $PSScriptRoot 'source\AwakeGuard.ico'
$outputPath = Join-Path $PSScriptRoot 'AwakeGuard.exe'
& $compilerPath /nologo /target:winexe /platform:anycpu /optimize+ /utf8output "/out:$outputPath" "/win32manifest:$manifestPath" "/win32icon:$iconPath" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll $sourcePath
if ($LASTEXITCODE -ne 0) { throw 'AwakeGuard build failed.' }
Write-Output "Built $outputPath"
