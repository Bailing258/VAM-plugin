param(
    [string]$PluginDir
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($PluginDir)) {
    $startDir = Split-Path -Parent $MyInvocation.MyCommand.Path
} else {
    $startDir = (Resolve-Path -LiteralPath $PluginDir).Path
}

function Find-VamRoot([string]$start) {
    $cur = (Resolve-Path -LiteralPath $start).Path
    for ($i = 0; $i -lt 10; $i++) {
        if ((Test-Path -LiteralPath (Join-Path $cur 'VaM.exe')) -and
            (Test-Path -LiteralPath (Join-Path $cur 'AddonPackages')) -and
            (Test-Path -LiteralPath (Join-Path $cur 'Custom')) -and
            (Test-Path -LiteralPath (Join-Path $cur 'BepInEx'))) {
            return $cur
        }
        $parent = Split-Path -Parent $cur
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $cur) { break }
        $cur = $parent
    }
    throw "Safety check failed: could not find VaM root from '$start'."
}

function Resolve-UnderRoot([string]$root, [string]$relative) {
    $target = Join-Path $root $relative
    $rootFull = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $targetFull = [IO.Path]::GetFullPath($target)
    if (-not ($targetFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase))) {
        throw "Safety check failed: target escapes VaM root: $targetFull"
    }
    return $targetFull
}

function Remove-IfExists([string]$path, [string]$label) {
    if (Test-Path -LiteralPath $path) {
        Write-Host "Removing ${label}: $path"
        Remove-Item -LiteralPath $path -Recurse -Force
    } else {
        Write-Host "Skip $label (not found): $path"
    }
}

$vamRoot = Find-VamRoot $startDir
Write-Host "VaM root: $vamRoot"
Write-Host "This removes AllPackagesLinker only. Allpackages and external libraries are left untouched."

$targets = @(
    @{ Path = Resolve-UnderRoot $vamRoot 'AddonPackages\_AllPackagesLinkerLinks'; Label = 'generated link cache' },
    @{ Path = Resolve-UnderRoot $vamRoot 'Saves\PluginData\AllPackagesLinker'; Label = 'plugin data' },
    @{ Path = Resolve-UnderRoot $vamRoot 'BepInEx\plugins\AllPackagesLinker'; Label = 'BepInEx plugin' },
    @{ Path = Resolve-UnderRoot $vamRoot 'Custom\Scripts\AllPackagesLinker'; Label = 'cslist wrapper/docs' }
)

foreach ($t in $targets) {
    Remove-IfExists $t.Path $t.Label
}

Write-Host 'AllPackagesLinker uninstalled. Allpackages was not modified.'
