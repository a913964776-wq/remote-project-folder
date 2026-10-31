$ErrorActionPreference = 'Stop'
$OutputName = '远程项目工作台.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
Push-Location $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /platform:anycpu /optimize+ '/out:RemoteProjectFolder.exe' /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll /r:System.Xml.dll /r:Microsoft.CSharp.dll /r:System.Web.Extensions.dll src\*.cs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    Move-Item -LiteralPath 'RemoteProjectFolder.exe' -Destination $OutputName -Force
} finally { Pop-Location }
