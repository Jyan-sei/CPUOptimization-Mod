$game = "C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown Demo"
$managed = Join-Path $game "CasualtiesUnknown_Data\Managed"
$krok = Join-Path $game "BepInEx\plugins\KrokMP\KrokoshaCasualtiesMP.dll"

Add-Type -Path (Join-Path $managed "UnityEngine.CoreModule.dll")
Add-Type -Path (Join-Path $managed "UnityEngine.dll")
[AppDomain]::CurrentDomain.LoadFrom($krok) | Out-Null
$utilsPath = Join-Path (Split-Path $krok) "KrokoshaCasualtiesUtils.dll"
if (Test-Path $utilsPath) { [AppDomain]::CurrentDomain.LoadFrom($utilsPath) | Out-Null }

function FindType($name) {
    foreach ($asm in [AppDomain]::CurrentDomain.GetAssemblies()) {
        $t = $asm.GetType($name, $false)
        if ($t) { return $t }
    }
    return $null
}

$checks = @{
    OnWillRender = "KrokoshaCasualtiesMP.Krokosha_OnWillRenderObject_ForceForMPComponent"
    Despawner = "KrokoshaCasualtiesMP.ItemDespawnerIfUntouched"
    Spider = "KrokoshaCasualtiesMP.KrokoshaSpiderTrackerComponent"
    Voice = "KrokoshaCasualtiesMP.Voicechat"
    MpPatch = "KrokoshaCasualtiesMP.Item_Update_MultiplayerPatch"
    NetBody = "KrokoshaCasualtiesMP.NetBody"
    Util = "KrokoshaCasualtiesUtils.Util"
    ScavTracker = "KrokoshaCasualtiesMP.KrokoshaScavMultiGameObjectNetworkTracker"
}

foreach ($kv in $checks.GetEnumerator()) {
    $t = FindType $kv.Value
    Write-Output ("{0}: {1}" -f $kv.Key, $(if ($t) { "OK" } else { "MISSING" }))
}

$nb = FindType "KrokoshaCasualtiesMP.NetBody"
if ($nb) {
    $listType = [Type]::GetType("System.Collections.Generic.List``1").MakeGenericType($nb)
    $fill = $nb.GetMethods([Reflection.BindingFlags]"Static,Public,NonPublic") | Where-Object { $_.Name -eq "GetBodiesInRadius" -and $_.GetParameters().Count -eq 3 }
    Write-Output ("GetBodiesInRadius fill: {0}" -f $(if ($fill) { "OK" } else { "MISSING" }))
    Write-Output ("is_player prop: {0}" -f $(if ($nb.GetProperty("is_player")) { "OK" } else { "MISSING" }))
    Write-Output ("body prop: {0}" -f $(if ($nb.GetProperty("body")) { "OK" } else { "MISSING" }))
}

$util = FindType "KrokoshaCasualtiesUtils.Util"
if ($util) {
    $m = $util.GetMethod("IsBodyLocal", [Reflection.BindingFlags]"Static,Public,NonPublic")
    Write-Output ("IsBodyLocal: {0}" -f $(if ($m) { "OK" } else { "MISSING" }))
}
