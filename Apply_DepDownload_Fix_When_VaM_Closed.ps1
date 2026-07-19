$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$dll = Join-Path $dir 'AllPackagesLinker.dll'
$pending = Join-Path $dir 'AllPackagesLinker.dll.pending_depdownload_fix'
$log = Join-Path $dir 'apply_depdownload_fix.log'
"[$(Get-Date -Format s)] waiting for VaM.exe to exit" | Add-Content -Encoding UTF8 $log
while (Get-Process -Name VaM -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 2 }
if (!(Test-Path $pending)) { "[$(Get-Date -Format s)] pending file missing: $pending" | Add-Content -Encoding UTF8 $log; exit 2 }
$bak = Join-Path $dir ("AllPackagesLinker.dll.bak_" + (Get-Date -Format 'yyyyMMdd_HHmmss') + "_before_depdownload_fix_apply")
if (Test-Path $dll) { Copy-Item -LiteralPath $dll -Destination $bak -Force }
Copy-Item -LiteralPath $pending -Destination $dll -Force
"[$(Get-Date -Format s)] applied pending build to $dll; backup=$bak" | Add-Content -Encoding UTF8 $log
