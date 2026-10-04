$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$testDirectory = Join-Path $env:TEMP ('AwakeGuard-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$testExe = Join-Path $testDirectory 'AwakeGuard.Tests.exe'
$previewPath = Join-Path $testDirectory 'preview.png'
$sourcePath = Join-Path $PSScriptRoot 'source\AwakeGuard.cs'
$testSourcePath = Join-Path $PSScriptRoot 'source\tests\TestRunner.cs'
$manifestPath = Join-Path $PSScriptRoot 'source\awakeguard.manifest'
& $compilerPath /nologo /target:exe /main:TestRunner /platform:anycpu /utf8output "/out:$testExe" "/win32manifest:$manifestPath" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll $sourcePath $testSourcePath
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& $testExe $previewPath
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
Write-Output "Test preview: $previewPath"
