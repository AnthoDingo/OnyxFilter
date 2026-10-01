# Vérifie que chaque texte d'interface L["..."] (ou Html("...") pour un texte avec balisage) a sa traduction dans chaque SharedResource.<langue>.resx.
# Une clé manquante s'afficherait en français dans l'interface traduite.
# Usage : pwsh src/OnyxFilter/Resources/check-translations.ps1
$components = Join-Path $PSScriptRoot '..' 'Components'
$keys = Get-ChildItem $components -Recurse -Include *.razor, *.razor.cs |
    Select-String -Pattern '(?:L\[|Html\()"([^"]*)"' -AllMatches |
    ForEach-Object { $_.Matches } |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique -CaseSensitive

$failed = $false

# Les ressources compilées ne distinguent pas deux noms qui ne diffèrent que par la casse :
# l'un des deux textes resterait en français.
$collisions = $keys | Group-Object { $_.ToLowerInvariant() } | Where-Object Count -gt 1
foreach ($group in $collisions) {
    $failed = $true
    Write-Output "Clés identiques à la casse près : $($group.Group -join ' | ')"
}

foreach ($resx in Get-ChildItem $PSScriptRoot -Filter 'SharedResource.*.resx') {
    $names = ([xml](Get-Content $resx.FullName -Raw)).root.data.name
    $missing = $keys | Where-Object { $_ -cnotin $names }
    if ($missing) {
        $failed = $true
        Write-Output "$($resx.Name) : $(@($missing).Count) traduction(s) manquante(s)"
        $missing | ForEach-Object { Write-Output "  $_" }
    }
}

if ($failed) { exit 1 }
Write-Output "$(@($keys).Count) clés, toutes traduites."
