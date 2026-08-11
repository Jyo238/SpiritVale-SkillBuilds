$ErrorActionPreference = 'SilentlyContinue'
Write-Host ''
Write-Host '  正在產生診斷報告...' -ForegroundColor Cyan

function Find-Game {
    $steam = $null
    try { $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath } catch {}
    if (-not $steam) { try { $steam = (Get-ItemProperty 'HKLM:\SOFTWARE\Wow6432Node\Valve\Steam' -ErrorAction Stop).InstallPath } catch {} }
    $libs = New-Object System.Collections.ArrayList
    if ($steam) {
        [void]$libs.Add($steam)
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"(.+?)"')) {
                [void]$libs.Add($m.Groups[1].Value.Replace('\\', '\'))
            }
        }
    }
    foreach ($l in $libs) {
        $p = Join-Path $l 'steamapps\common\SpiritVale'
        if (Test-Path (Join-Path $p 'SpiritVale.exe')) { return $p }
    }
    return $null
}

$game = Find-Game
$r = New-Object System.Text.StringBuilder
[void]$r.AppendLine('===== SpiritVale Build 切換 診斷報告 =====')
[void]$r.AppendLine('產生時間: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
[void]$r.AppendLine('')

if (-not $game) {
    [void]$r.AppendLine('[!] 找不到 SpiritVale 安裝位置')
} else {
    [void]$r.AppendLine('遊戲位置: ' + $game)
    $exe = Get-Item (Join-Path $game 'SpiritVale.exe')
    [void]$r.AppendLine('遊戲版本: ' + $exe.VersionInfo.ProductVersion + ' (' + $exe.LastWriteTime.ToString('yyyy-MM-dd') + ')')

    $bepCore = Join-Path $game 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'
    if (Test-Path $bepCore) {
        $v = (Get-Item $bepCore).VersionInfo.ProductVersion
        [void]$r.AppendLine('BepInEx: 已安裝 (' + $v + ')')
    } else {
        [void]$r.AppendLine('[!] BepInEx: 未安裝 —— 請重新執行一鍵安裝.bat')
    }

    $interop = Join-Path $game 'BepInEx\interop\Assembly-CSharp.dll'
    if (Test-Path $interop) {
        [void]$r.AppendLine('Interop 組件: 已生成 (' + (Get-Item $interop).LastWriteTime.ToString('yyyy-MM-dd HH:mm') + ')')
    } else {
        [void]$r.AppendLine('[!] Interop 組件: 尚未生成 —— 裝好後要先啟動一次遊戲')
    }

    $plugin = Join-Path $game 'BepInEx\plugins\SpiritValeSkillBuilds\SpiritValeSkillBuilds.dll'
    if (Test-Path $plugin) {
        $p = Get-Item $plugin
        $hash = (Get-FileHash $plugin -Algorithm SHA256).Hash.Substring(0, 16)
        [void]$r.AppendLine('本 Mod: 已安裝 v' + $p.VersionInfo.ProductVersion + ' (SHA256前16碼 ' + $hash + ')')
    } else {
        [void]$r.AppendLine('[!] 本 Mod: 未安裝')
    }

    $others = Get-ChildItem (Join-Path $game 'BepInEx\plugins') -Recurse -Filter '*.dll' | Where-Object Name -ne 'SpiritValeSkillBuilds.dll'
    if ($others) {
        [void]$r.AppendLine('其他外掛: ' + (($others | ForEach-Object { $_.Name }) -join ', '))
    }

    $cfg = Join-Path $game 'BepInEx\config\local.spiritvale.skillbuilds.cfg'
    if (Test-Path $cfg) {
        [void]$r.AppendLine('')
        [void]$r.AppendLine('--- 目前設定 ---')
        foreach ($line in (Get-Content $cfg)) {
            if ($line -match '^\s*[^#\[].*=') { [void]$r.AppendLine('  ' + $line.Trim()) }
        }
    }

    # Build 存檔：預設在 config，但玩家可能用「存檔資料夾」指到雲端資料夾
    $store = Join-Path $game 'BepInEx\config\local.spiritvale.skillbuilds.presets.json'
    if (Test-Path $cfg) {
        foreach ($line in (Get-Content $cfg)) {
            if ($line -match '^\s*存檔資料夾\s*=\s*(.+)$') {
                $d = $Matches[1].Trim()
                if ($d) { $store = Join-Path $d 'local.spiritvale.skillbuilds.presets.json' }
            }
        }
    }
    [void]$r.AppendLine('')
    [void]$r.AppendLine('--- Build 存檔 ---')
    [void]$r.AppendLine('  位置: ' + $store)
    if (Test-Path $store) {
        $fi = Get-Item $store
        [void]$r.AppendLine('  大小: ' + $fi.Length + ' bytes，最後修改 ' + $fi.LastWriteTime)
        try {
            $j = Get-Content $store -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($prop in $j.PSObject.Properties) {
                $n = @($prop.Value | Where-Object { $_ }).Count
                [void]$r.AppendLine('  角色 ' + $prop.Name + '：' + $n + ' 組')
                foreach ($b in $prop.Value) {
                    if (-not $b) { continue }
                    [void]$r.AppendLine('    - ' + $b.Name +
                        ' | 技能 ' + @($b.Skills).Count +
                        ' | 快捷 ' + @($b.Assigned).Count +
                        ' | 裝備 ' + @($b.Equips).Count +
                        ' | 神器 ' + @($b.Artifacts).Count +
                        ' | 魔導書 ' + @($b.Grimoires).Count)
                }
            }
        } catch { [void]$r.AppendLine('  [!] 解析失敗：' + $_.Exception.Message) }
    } else {
        [void]$r.AppendLine('  （尚未建立，代表還沒存過任何 Build）')
    }

    $errLog = Join-Path $game 'BepInEx\ErrorLog.log'
    if (Test-Path $errLog) {
        [void]$r.AppendLine('')
        [void]$r.AppendLine('--- ErrorLog.log（最後 20 行）---')
        Get-Content $errLog -Tail 20 | ForEach-Object { [void]$r.AppendLine('  ' + $_) }
    }

    $log = Join-Path $game 'BepInEx\LogOutput.log'
    if (Test-Path $log) {
        [void]$r.AppendLine('')
        [void]$r.AppendLine('--- LogOutput.log（本 Mod 相關 + 錯誤，最後 60 筆）---')
        Get-Content $log | Where-Object { $_ -match '\[Build\]|Skill Builds|Error|Exception|Fatal|Warning' } |
            Select-Object -Last 60 | ForEach-Object { [void]$r.AppendLine('  ' + $_) }
    } else {
        [void]$r.AppendLine('[!] 找不到 LogOutput.log —— 安裝後尚未啟動過遊戲')
    }
}

[void]$r.AppendLine('')
[void]$r.AppendLine('===== 報告結束（回報問題時請完整貼上）=====')

$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Build 切換_診斷報告.txt'
$r.ToString() | Set-Content -Path $out -Encoding UTF8
Write-Host ''
Write-Host ('  報告已存到桌面：' + $out) -ForegroundColor Green
Write-Host '  已自動開啟，回報問題時請把內容完整貼上。' -ForegroundColor Gray
Start-Process notepad $out
Write-Host ''
Read-Host '按 Enter 關閉'

