param(
 [string]$DotNet = 'dotnet',
 [string]$MakeAppx = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe',
 [string]$OutputDirectory = ''
)
$ErrorActionPreference='Stop'
$desktopRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(!$OutputDirectory){$OutputDirectory=Join-Path $desktopRoot 'artifacts'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$runtime=Join-Path $desktopRoot 'runtime'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
$archive=Join-Path $runtime 'xerj-1.0.0-rc.95-x86_64-pc-windows-msvc.zip'
if(!(Test-Path -LiteralPath $archive)){
 Invoke-WebRequest 'https://github.com/xerj-org/xerj/releases/download/v1.0.0-rc.95/xerj-1.0.0-rc.95-x86_64-pc-windows-msvc.zip' -OutFile $archive
}
$expected='23648a3b2e3965b60c4939876a1f3d43f1b9a609b61b3d406e752b8336220385'
if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected){throw 'XERJ archive checksum mismatch; refusing to build.'}
$extract=Join-Path $runtime 'verified-release'
Expand-Archive -LiteralPath $archive -DestinationPath $extract -Force
Copy-Item -LiteralPath (Join-Path $extract 'xerj-1.0.0-rc.95-x86_64-pc-windows-msvc/xerj.exe') -Destination (Join-Path $runtime 'xerj.exe') -Force
$staging=Join-Path $OutputDirectory ('package-'+[Guid]::NewGuid().ToString('N'))
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
Push-Location $desktopRoot
try {
 & $DotNet restore LocalSearchStudio -r win-x64 --locked-mode
 if($LASTEXITCODE){throw 'Restore failed.'}
 & $DotNet publish LocalSearchStudio -c Release -r win-x64 --self-contained true --no-restore -o $staging -p:DebugType=none
 if($LASTEXITCODE){throw 'Publish failed.'}
 Copy-Item -LiteralPath (Join-Path $desktopRoot 'packaging/AppxManifest.xml') -Destination $staging
 Copy-Item -LiteralPath (Join-Path $desktopRoot 'packaging/Assets') -Destination $staging -Recurse
 & $MakeAppx pack /d $staging /p (Join-Path $OutputDirectory 'FolderHunt-1.0.1.0-x64.msix') /o
 if($LASTEXITCODE){throw 'MSIX validation or packing failed.'}
 Compress-Archive -Path (Join-Path $staging '*') -DestinationPath (Join-Path $OutputDirectory 'FolderHunt-1.0.1-win-x64-portable.zip') -Force
 Get-FileHash -Algorithm SHA256 -Path (Join-Path $OutputDirectory '*.msix'),(Join-Path $OutputDirectory '*.zip') | Format-Table
} finally {Pop-Location}

# The unsigned MSIX is for Partner Center. Microsoft signs Store-delivered
# packages. Do not ask users to trust an ad-hoc certificate to install it.
