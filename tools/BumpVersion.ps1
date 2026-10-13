# ---------------------------------------------------------------------------
# Raises the patch number in Properties\AssemblyInfo.cs — 1.0.0 -> 1.0.1.
#
# The Release build calls this from the BumpVersion target in the csproj, and
# only when a source file has actually changed, so rebuilding unchanged code
# does not burn a version. Run it by hand for a minor/major release:
#   powershell -File tools\BumpVersion.ps1 -Part minor
#
# AssemblyInfo stays the single source of truth: the title bar, the About box
# and the releases\OmrXmlBuilder_v{version}.exe name all read it from there.
# ---------------------------------------------------------------------------
param(
    [string]$AssemblyInfo = (Join-Path $PSScriptRoot '..\Properties\AssemblyInfo.cs'),
    [ValidateSet('major', 'minor', 'patch')]
    [string]$Part = 'patch'
)

$ErrorActionPreference = 'Stop'
$path = [System.IO.Path]::GetFullPath($AssemblyInfo)
if (-not (Test-Path $path)) { throw "AssemblyInfo not found: $path" }

$text = [System.IO.File]::ReadAllText($path)
$rx = '(?m)^\[assembly:\s*Assembly(File)?Version\("(\d+)\.(\d+)\.(\d+)(\.\d+)?"\)\]'
$m = [regex]::Match($text, $rx)
if (-not $m.Success) { throw "No AssemblyVersion attribute found in $path" }

$major = [int]$m.Groups[2].Value
$minor = [int]$m.Groups[3].Value
$patch = [int]$m.Groups[4].Value
$old = "$major.$minor.$patch"

switch ($Part) {
    'major' { $major++; $minor = 0; $patch = 0 }
    'minor' { $minor++; $patch = 0 }
    'patch' { $patch++ }
}
$new = "$major.$minor.$patch"

# Rewrite both attributes; the 4th field stays 0 so ToString(3) in the csproj
# and the About box always show the same three numbers.
$text = [regex]::Replace($text, $rx, {
    param($hit)
    $attr = if ($hit.Groups[1].Success) { 'AssemblyFileVersion' } else { 'AssemblyVersion' }
    "[assembly: $attr(""$new.0"")]"
})
[System.IO.File]::WriteAllText($path, $text)
Write-Host "Version bumped: $old -> $new  ($Part)"
