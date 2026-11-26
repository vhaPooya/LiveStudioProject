# Moves source files that are NOT part of this release (leftovers from older zips) into _stale_backup\
# Run from the solution folder:  powershell -ExecutionPolicy Bypass -File .\Clean-Stale.ps1
$root = $PSScriptRoot
$manifest = Get-Content (Join-Path $root 'MANIFEST.txt') | ForEach-Object { $_.Replace('/', '\').Trim() } | Where-Object { $_ }
$set = [System.Collections.Generic.HashSet[string]]::new([string[]]$manifest, [System.StringComparer]::OrdinalIgnoreCase)
$backup = Join-Path $root '_stale_backup'
$count = 0
foreach ($dir in 'src', 'tests') {
    $base = Join-Path $root $dir
    if (-not (Test-Path $base)) { continue }
    Get-ChildItem $base -Recurse -File -Include *.cs, *.razor, *.csproj |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        ForEach-Object {
            $rel = $_.FullName.Substring($root.Length).TrimStart('\')
            if (-not $set.Contains($rel)) {
                $dest = Join-Path $backup $rel
                New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
                Move-Item $_.FullName $dest -Force
                Write-Host "moved: $rel"
                $count++
            }
        }
}
foreach ($d in 'src', 'tests') {
    Get-ChildItem (Join-Path $root $d) -Recurse -Directory -Include bin, obj -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "Done. $count stale file(s) moved to _stale_backup. Now run: dotnet clean; dotnet build"
