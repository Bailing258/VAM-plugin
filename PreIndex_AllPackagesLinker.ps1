param(
    [string]$VamRoot
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

if ([string]::IsNullOrWhiteSpace($VamRoot)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $cur = (Resolve-Path -LiteralPath $scriptDir).Path
    for ($i=0; $i -lt 10; $i++) {
        if ((Test-Path -LiteralPath (Join-Path $cur 'VaM.exe')) -and (Test-Path -LiteralPath (Join-Path $cur 'AddonPackages'))) { $VamRoot = $cur; break }
        $parent = Split-Path -Parent $cur
        if ($parent -eq $cur) { break }
        $cur = $parent
    }
}
if ([string]::IsNullOrWhiteSpace($VamRoot)) { throw 'VaM root not found.' }
$VamRoot = (Resolve-Path -LiteralPath $VamRoot).Path

$allRoot = Join-Path $VamRoot 'Allpackages'
$dataRoot = Join-Path $VamRoot 'Saves\PluginData\AllPackagesLinker'
$thumbRoot = Join-Path $dataRoot 'thumbs'
$indexPath = Join-Path $dataRoot 'index.tsv'
New-Item -ItemType Directory -Force -Path $allRoot,$dataRoot,$thumbRoot | Out-Null

$sep = [string][char]31
function Enc([string]$s) {
    if ($null -eq $s) { $s = '' }
    return [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s))
}
function Norm([string]$s) { if ($null -eq $s) { return '' }; return $s.Replace('\','/').TrimStart('/') }
function Add-Cat([string]$n, [hashtable]$cats) {
    $p = (Norm $n).ToLowerInvariant(); if ($p.Length -eq 0) { return }
    if ($p.StartsWith('saves/scene/') -and ($p.EndsWith('.json') -or $p.EndsWith('.jpg') -or $p.EndsWith('.jpeg') -or $p.EndsWith('.png'))) { $cats['Scenes']=$true }
    if ($p.Contains('appearance') -or $p.StartsWith('saves/person/appearance/') -or $p.StartsWith('saves/person/full/') -or $p.StartsWith('custom/atom/person/appearance/')) { $cats['Looks']=$true }
    if ($p.StartsWith('custom/clothing/') -or $p.StartsWith('custom/atom/person/clothing/')) { $cats['Clothing']=$true }
    if ($p.StartsWith('custom/hair/') -or $p.StartsWith('custom/atom/person/hair/')) { $cats['Hair']=$true }
    if ($p.StartsWith('custom/atom/person/morphs/')) { $cats['Morphs']=$true }
    if ($p.StartsWith('custom/assets/') -or $p.EndsWith('.assetbundle')) { $cats['Assets']=$true }
    if ($p.StartsWith('custom/scripts/') -or $p.EndsWith('.cs') -or $p.EndsWith('.cslist')) { $cats['Scripts']=$true }
}
function Thumb-Pri([string]$n) {
    $p = $n.ToLowerInvariant()
    if (-not ($p.EndsWith('.jpg') -or $p.EndsWith('.jpeg') -or $p.EndsWith('.png'))) { return 999 }
    if ($p.StartsWith('saves/scene/')) { return 1 }
    if ($p.Contains('appearance') -or $p.StartsWith('saves/person/')) { return 2 }
    if ($p.StartsWith('custom/clothing/')) { return 3 }
    if ($p.StartsWith('custom/hair/')) { return 4 }
    return 10
}
function Add-Deps($obj, [hashtable]$deps) {
    if ($null -eq $obj -or $null -eq $obj.dependencies) { return }
    foreach ($prop in $obj.dependencies.PSObject.Properties) {
        if (-not [string]::IsNullOrWhiteSpace($prop.Name)) { $deps[$prop.Name.Trim()] = $true }
        Add-Deps $prop.Value $deps
    }
}
function Sha1Hex([string]$s) {
    $sha = [Security.Cryptography.SHA1]::Create()
    try { ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($s)) | ForEach-Object { $_.ToString('x2') }) -join '' }
    finally { $sha.Dispose() }
}
function Get-Rel([string]$root, [string]$file) {
    $u = [Uri]((Resolve-Path -LiteralPath $root).Path.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar)
    $f = [Uri]([IO.Path]::GetFullPath($file))
    return [Uri]::UnescapeDataString($u.MakeRelativeUri($f).ToString()).Replace('/', [IO.Path]::DirectorySeparatorChar)
}
function Get-VarFiles([string]$start, [bool]$followFirstLink) {
    $stack = New-Object 'System.Collections.Generic.Stack[object]'
    $stack.Push([pscustomobject]@{ Dir = $start; Via = $false })
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    while ($stack.Count -gt 0) {
        $cur = $stack.Pop()
        try { $full = [IO.Path]::GetFullPath($cur.Dir).TrimEnd('\','/') } catch { continue }
        if (-not $seen.Add($full)) { continue }
        Get-ChildItem -LiteralPath $cur.Dir -File -Filter '*.var' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
        $dirs = Get-ChildItem -LiteralPath $cur.Dir -Directory -ErrorAction SilentlyContinue
        foreach ($d in $dirs) {
            $rp = (($d.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
            if ($rp -and ((-not $followFirstLink) -or $cur.Via)) { continue }
            $stack.Push([pscustomobject]@{ Dir = $d.FullName; Via = ($cur.Via -or $rp) })
        }
    }
}
function Save-Thumb($zip, $entry, [string]$fullPath, [long]$size, [long]$ticks, [string]$entryName) {
    if ($null -eq $entry) { return '' }
    $ext = [IO.Path]::GetExtension($entryName).ToLowerInvariant()
    if ($ext -notin @('.jpg','.jpeg','.png')) { $ext = '.img' }
    $out = Join-Path $thumbRoot ((Sha1Hex ($fullPath + '|' + $size + '|' + $ticks + '|' + $entryName)) + $ext)
    if (Test-Path -LiteralPath $out) { return $out }
    if ($entry.Length -gt 12MB) { return '' }
    $s = $entry.Open()
    try {
        $fs = [IO.File]::Open($out, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $s.CopyTo($fs) } finally { $fs.Dispose() }
    } finally { $s.Dispose() }
    return $out
}

Write-Host "VaM root: $VamRoot"
Write-Host "Indexing: $allRoot"
$lines = New-Object 'System.Collections.Generic.List[string]'
$lines.Add('#APL_INDEX_V2')
$count=0; $errors=0
foreach ($file in Get-VarFiles $allRoot $true) {
    try {
        $fi = Get-Item -LiteralPath $file -Force
        $full = $fi.FullName
        $uid = [IO.Path]::GetFileNameWithoutExtension($fi.Name)
        $rel = Get-Rel $allRoot $full
        $cats = @{}
        $deps = @{}
        $scenes = New-Object 'System.Collections.Generic.List[string]'
        $desc = ''
        $firstScene = ''
        $bestThumb = ''
        $bestPri = 999
        $zip = [IO.Compression.ZipFile]::OpenRead($full)
        try {
            $meta = $null
            foreach ($e in $zip.Entries) { if ((Norm $e.FullName) -ieq 'meta.json') { $meta = $e; break } }
            if ($null -ne $meta) {
                $sr = New-Object IO.StreamReader($meta.Open(), [Text.Encoding]::UTF8)
                try { $txt = $sr.ReadToEnd() } finally { $sr.Dispose() }
                try {
                    $j = $txt | ConvertFrom-Json -ErrorAction Stop
                    if ($null -ne $j.description) { $desc = [string]$j.description }
                    if ($null -ne $j.contentList) { foreach ($c in @($j.contentList)) { Add-Cat ([string]$c) $cats } }
                    Add-Deps $j $deps
                } catch {}
            }
            foreach ($e in $zip.Entries) {
                if ([string]::IsNullOrEmpty($e.Name)) { continue }
                $n = Norm $e.FullName
                Add-Cat $n $cats
                $lower = $n.ToLowerInvariant()
                if ($lower.StartsWith('saves/scene/') -and $lower.EndsWith('.json')) {
                    if (-not $scenes.Contains($n)) { $scenes.Add($n) }
                    if ($firstScene -eq '') { $firstScene = $n }
                }
                $pri = Thumb-Pri $n
                if ($pri -lt $bestPri) { $bestPri = $pri; $bestThumb = $n }
            }
            $thumbCache = ''
            if ($bestThumb -ne '') {
                $thumbEntry = $null
                foreach ($e in $zip.Entries) { if ((Norm $e.FullName) -ieq $bestThumb) { $thumbEntry = $e; break } }
                $thumbCache = Save-Thumb $zip $thumbEntry $full $fi.Length $fi.LastWriteTimeUtc.Ticks $bestThumb
            }
        } finally { $zip.Dispose() }
        if ($cats.Count -eq 0) { $cats['Other']=$true }
        $catText = (($cats.Keys | Sort-Object) -join $sep)
        $depText = (($deps.Keys | Sort-Object) -join $sep)
        $sceneText = (($scenes | Sort-Object) -join $sep)
        $line = (Enc $full),(Enc $rel),(Enc $uid),$fi.Length,$fi.LastWriteTimeUtc.Ticks,(Enc $desc),(Enc $bestThumb),(Enc $firstScene),(Enc $thumbCache),(Enc $catText),(Enc $depText),(Enc $sceneText) -join "`t"
        $lines.Add($line)
        $count++
        if (($count % 50) -eq 0) { Write-Host "Indexed $count packages..." }
    } catch {
        $errors++
        Write-Warning "Failed: $file :: $($_.Exception.Message)"
    }
}
$tmp = $indexPath + '.tmp'
[IO.File]::WriteAllLines($tmp, $lines.ToArray(), [Text.Encoding]::UTF8)
if (Test-Path -LiteralPath $indexPath) { Remove-Item -LiteralPath $indexPath -Force }
Move-Item -LiteralPath $tmp -Destination $indexPath -Force
Write-Host "Done. packages=$count errors=$errors"
Write-Host "Index: $indexPath"
Write-Host "Thumbs: $thumbRoot"
