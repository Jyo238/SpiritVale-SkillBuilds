$ErrorActionPreference = 'Stop'
$scriptPath = $MyInvocation.MyCommand.Path

trap {
    Write-Host ''
    Write-Host ('發生錯誤：' + $_.Exception.Message) -ForegroundColor Red
    Write-Host '移除未完成。可截圖此視窗到回報串尋求協助。' -ForegroundColor Yellow
    Write-Host ''
    Read-Host '按 Enter 關閉'
    exit 1
}

Write-Host ''
Write-Host '================================================' -ForegroundColor Cyan
Write-Host '  SpiritVale Build 切換 - 移除' -ForegroundColor Cyan
Write-Host '================================================' -ForegroundColor Cyan
Write-Host ''

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
if (-not $game) {
    Write-Host '找不到 SpiritVale 安裝位置。' -ForegroundColor Yellow
    Write-Host '請把遊戲資料夾路徑貼上：'
    $game = (Read-Host '路徑').Trim('"').Trim()
}
if (Get-Process 'SpiritVale' -ErrorAction SilentlyContinue) {
    Write-Host '偵測到遊戲正在執行中！請「完全關閉遊戲」後再執行一次。' -ForegroundColor Red
    Write-Host ''; Read-Host '按 Enter 關閉'; return
}

$dst = Join-Path $game 'BepInEx\plugins\SpiritValeSkillBuilds'
if (-not (Test-Path $dst)) {
    Write-Host '沒有找到已安裝的 Mod，無需移除。' -ForegroundColor Yellow
    Write-Host ''; Read-Host '按 Enter 關閉'; return
}

try {
    Remove-Item $dst -Recurse -Force -ErrorAction Stop
} catch {
    Write-Host '移除需要系統管理員權限，正在以管理員身分重新啟動...' -ForegroundColor Yellow
    Write-Host '（等一下跳出的授權視窗請按「是」）' -ForegroundColor Yellow
    try {
        Start-Process powershell -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $scriptPath + '"'))
        return
    } catch {
        Write-Host '未取得管理員授權，移除取消。' -ForegroundColor Red
        Write-Host '請對「一鍵移除.bat」按右鍵 →「以系統管理員身分執行」再試一次。' -ForegroundColor Yellow
        Write-Host ''; Read-Host '按 Enter 關閉'; return
    }
}

if (Test-Path $dst) {
    Write-Host '移除驗證失敗：資料夾仍存在。' -ForegroundColor Red
    Write-Host ''; Read-Host '按 Enter 關閉'; return
}

Write-Host ''
Write-Host '================================================' -ForegroundColor Green
Write-Host '  [OK] 移除成功！（已驗證檔案清除）' -ForegroundColor Green
Write-Host '================================================' -ForegroundColor Green
Write-Host ''
Write-Host '設定檔保留在 BepInEx\config\（檔名含 skillbuilds），' -ForegroundColor Gray
Write-Host '之後重新安裝會沿用；想徹底清除可手動刪除該檔案。' -ForegroundColor Gray
Write-Host ''
Write-Host '若想連 BepInEx 框架一併移除（會影響其他使用 BepInEx 的 Mod）：' -ForegroundColor Gray
Write-Host '  刪除遊戲目錄下的 BepInEx、dotnet 資料夾，以及' -ForegroundColor Gray
Write-Host '  winhttp.dll、doorstop_config.ini、.doorstop_version、changelog.txt 四個檔案。' -ForegroundColor Gray
Write-Host ''
Read-Host '按 Enter 關閉'
