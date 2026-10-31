$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Split-Path $PSScriptRoot -Parent
function Fetch-Checked($Url, $Hash, $Target) {
    if ((Test-Path -LiteralPath $Target) -and ((Get-FileHash -LiteralPath $Target -Algorithm SHA256).Hash -eq $Hash)) { return }
    New-Item -ItemType Directory -Force -Path (Split-Path $Target -Parent) | Out-Null
    $download = $Target + '.download'
    Invoke-WebRequest -UseBasicParsing -TimeoutSec 90 -UserAgent 'RemoteProjectWorkbench/1.1' -Uri $Url -OutFile $download
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $Hash) { throw "Checksum mismatch: $Url" }
    Move-Item -LiteralPath $download -Destination $Target -Force
}
$cache = Join-Path $root 'work-dependencies'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
$zip = Join-Path $cache 'syncthing.zip'
Fetch-Checked 'https://github.com/syncthing/syncthing/releases/download/v2.1.7/syncthing-windows-amd64-v2.1.7.zip' '3ce7da770f546a8d02b65b2e7ee31f2a3b8b7c3f990ede045590cca4172ecfe1' $zip
$unpack = Join-Path $cache 'syncthing'
Expand-Archive -LiteralPath $zip -DestinationPath $unpack -Force
$syncDir = Join-Path $root 'vendor/syncthing'
New-Item -ItemType Directory -Force -Path $syncDir | Out-Null
foreach ($name in @('syncthing.exe','LICENSE.txt','AUTHORS.txt')) { Copy-Item -LiteralPath (Join-Path $unpack "syncthing-windows-amd64-v2.1.7/$name") -Destination $syncDir -Force }
$putty = Join-Path $root 'vendor/putty/putty.exe'
Fetch-Checked 'https://the.earth.li/~sgtatham/putty/0.85/w64/putty.exe' 'd01fdb5aae8f112526040a39b0bfb9e27d813003178645e65f8d1cfdb2a26c87' $putty
if ((Get-AuthenticodeSignature -LiteralPath $putty).Status -ne 'Valid') { throw 'PuTTY signature invalid' }
# WinSCP is only used by the optional advanced SFTP window.
$winZip = Join-Path $cache 'winscp.zip'
Fetch-Checked 'https://netix.dl.sourceforge.net/project/winscp/WinSCP/6.7/WinSCP-6.7-Portable.zip?download=1' 'fef88545873aca4b353ac322b1e2702f2feadf30758b713f5fc7e2bed60f7662' $winZip
Expand-Archive -LiteralPath $winZip -DestinationPath (Join-Path $root 'vendor/winscp') -Force
Fetch-Checked 'https://codeload.github.com/winscp/winscp/zip/refs/tags/6.7' '4493196f18bf5927ae74fadb872d37d9ec22970ed3b4265eeb109f9fab941308' (Join-Path $root 'third-party-source/WinSCP-6.7-Source.zip')
Write-Host 'Dependencies ready.'
