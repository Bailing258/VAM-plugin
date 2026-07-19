param([string]$VamRoot)
$ErrorActionPreference='Stop'

if([string]::IsNullOrWhiteSpace($VamRoot)){
  $scriptDir=Split-Path -Parent $MyInvocation.MyCommand.Path
  $cur=(Resolve-Path $scriptDir).Path
  for($i=0;$i -lt 10;$i++){
    if((Test-Path (Join-Path $cur 'VaM.exe')) -and (Test-Path (Join-Path $cur 'AddonPackages'))){$VamRoot=$cur;break}
    $p=Split-Path -Parent $cur; if($p -eq $cur){break}; $cur=$p
  }
}
if([string]::IsNullOrWhiteSpace($VamRoot)){throw 'VaM root not found'}

$testDir=Join-Path $VamRoot 'Saves\PluginData\AllPackagesLinker\symlink_test'
New-Item -ItemType Directory -Force -Path $testDir | Out-Null
$src=Join-Path $testDir 'src.txt'
$link=Join-Path $testDir 'link.txt'
'dev mode symlink test' | Set-Content -LiteralPath $src -Encoding ASCII
Remove-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue

$code = @"
using System;
using System.Runtime.InteropServices;
public static class SymlinkNative {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  public static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, int dwFlags);
  public const int SYMBOLIC_LINK_FLAG_FILE = 0x0;
  public const int SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE = 0x2;
  public static int LastError() { return Marshal.GetLastWin32Error(); }
}
"@
Add-Type -TypeDefinition $code -ErrorAction Stop

Write-Host "Testing file symlink permission with CreateSymbolicLinkW + ALLOW_UNPRIVILEGED_CREATE..."
$ok = [SymlinkNative]::CreateSymbolicLink($link, $src, [SymlinkNative]::SYMBOLIC_LINK_FLAG_FILE -bor [SymlinkNative]::SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE)
$err = [SymlinkNative]::LastError()
if($ok -and (Test-Path -LiteralPath $link)){
  Write-Host 'OK: Developer Mode symlink API works for current user/process.' -ForegroundColor Green
  Write-Host "Link: $link"
  exit 0
}

Write-Host "API test failed. Win32 error=$err" -ForegroundColor Yellow
Write-Host 'Trying legacy PowerShell New-Item test for comparison...'
try {
  Remove-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
  New-Item -ItemType SymbolicLink -Path $link -Target $src -Force | Out-Null
  if(Test-Path -LiteralPath $link){
    Write-Host 'OK: legacy PowerShell symbolic link also works.' -ForegroundColor Green
    exit 0
  }
} catch {
  Write-Host ('Legacy PowerShell test failed: ' + $_.Exception.Message) -ForegroundColor Yellow
}

switch($err){
  1314 { Write-Host 'Meaning: privilege not held. Developer Mode may require sign-out/reboot, policy may block it, or process token has not picked it up.' -ForegroundColor Red }
  87   { Write-Host 'Meaning: invalid parameter. This Windows build/API path may not support unprivileged symlink flag.' -ForegroundColor Red }
  5    { Write-Host 'Meaning: access denied. Check antivirus/controlled folder access/path permissions.' -ForegroundColor Red }
  default { Write-Host 'Meaning: see Win32 error code above.' -ForegroundColor Red }
}
Write-Host 'Fix options: restart Windows after enabling Developer Mode, or run VaM as Administrator.' -ForegroundColor Yellow
exit 1
