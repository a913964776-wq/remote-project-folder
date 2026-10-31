param([string]$Destination)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$Destination) { $Destination = Join-Path (Split-Path $root -Parent) 'RemoteProjectWorkbench-v1.1.0.zip' }
$stage = Join-Path $root ('dist/' + [Guid]::NewGuid().ToString('N') + '/RemoteProjectWorkbench')
New-Item -ItemType Directory -Force -Path $stage | Out-Null
# Explicit allow-list: no connection profiles, keys, logs, caches or test workspaces.
foreach ($name in @('src','server','tools','docs','vendor','third-party-source')) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $stage -Recurse -Force }
foreach ($name in @('README.md','LICENSE.txt','THIRD-PARTY.md','build.ps1','CHANGELOG.md','验证记录.md','远程项目工作台.exe')) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $stage -Force }
Copy-Item -LiteralPath (Join-Path $root '远程项目工作台.exe') -Destination (Join-Path $stage '服务器项目文件夹.exe') -Force
$lines = Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object { $relative = $_.FullName.Substring($stage.Length + 1).Replace('\','/'); '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLower(), $relative }
[IO.File]::WriteAllLines((Join-Path $stage 'SHA256SUMS.txt'), $lines, [Text.UTF8Encoding]::new($false))
Compress-Archive -LiteralPath $stage -DestinationPath $Destination -Force
Write-Host $Destination
