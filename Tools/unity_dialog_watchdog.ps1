<#
    Сторож модальных окон Unity.

    ЗАЧЕМ. Модалка в Unity держит ГЛАВНЫЙ ПОТОК, а на нём же сидит мост
    UnityMCP — значит любое окно «Yes/No» останавливает не только редактор, но и
    всю автоматизацию: команда висит, пока человек не подойдёт и не нажмёт.
    Один такой диалог на сборке контента съел десять минут вслепую.

    Устроен как брат сторожа DAZ (Tools/wardrobe/daz_dialog_watchdog.ps1), и
    правила у них те же — они выстраданы, а не придуманы:

      * СПИСОК РАЗРЕШЁННЫХ — только те окна, которые опознаны точно. Незнакомое
        окно не трогаем: это может быть вопрос, на который нельзя отвечать
        наугад.
      * ЗАПРЕЩАЮЩИЙ СПИСОК ПОВЕРХ РАЗРЕШАЮЩЕГО. Unity спрашивает и «перезаписать
        файл?», и «удалить ассеты?» — на такое отвечает человек.
      * ЛОГ КАЖДОГО НАЖАТИЯ. Иначе исчезнувшие вопросы всплывут позже как
        необъяснимо пропавший контент.

    Отличие от DAZ: у Unity диалоги — обычные Win32 окна с настоящими кнопками,
    поэтому здесь кнопка НАЖИМАЕТСЯ (BM_CLICK), а не окно закрывается. Закрытие
    крестиком у вопроса «Yes/No» означало бы «No», то есть тихую отмену сборки.

    Запуск (в фоне, рядом с длинной операцией):
        powershell -NoProfile -File Tools/unity_dialog_watchdog.ps1 -Seconds 900
#>
param(
    [int]$Seconds = 600,
    [string]$LogPath = "$env:TEMP\hexlive\unity_dialog_watchdog.log"
)

# Окна, на которые отвечать МОЖНО, и какой кнопкой. Список намеренно короткий:
# сюда попадает только то, что действительно встретилось и разобрано.
$Allow = @(
    # Сборка контента Addressables поверх уже собранного.
    @{ Match = 'Addressables.*(rebuild|overwrite|continue)'; Button = 'Yes' },
    @{ Match = 'Clean.*Addressables|Addressables.*Clean';    Button = 'Yes' },
    # «Скрипты изменились, перезагрузить домен?» — да, иначе меню зовёт старый код.
    @{ Match = 'script.*(changed|reload)';                   Button = 'Reload' },
    # Импорт длинных операций иногда спрашивает подтверждение продолжения.
    @{ Match = 'Hold on|Importing|Please wait';              Button = 'Continue' }
)

# Побеждает всегда. Это вопросы про НЕОБРАТИМОЕ.
$Deny = @(
    'delete', 'удал',
    'overwrite.*file', 'replace.*file',
    'quit', 'exit', 'close.*without.*saving',
    'revert', 'discard',
    'unsaved.*changes', 'save.*changes'
)

$signature = @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class UWin {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
    // CharSet.Unicode обязателен: иначе StringBuilder маршалится как ANSI и от
    // заголовка приезжает первая буква (эти грабли уже ловили в стороже DAZ).
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr wp, IntPtr lp);

    public static string Text(IntPtr h) {
        var sb = new StringBuilder(1024);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string Class(IntPtr h) {
        var sb = new StringBuilder(256);
        GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static List<IntPtr> TopLevel(uint pid) {
        var found = new List<IntPtr>();
        EnumWindows((h, p) => {
            uint owner; GetWindowThreadProcessId(h, out owner);
            if (owner == pid && IsWindowVisible(h)) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static List<IntPtr> Children(IntPtr parent) {
        var found = new List<IntPtr>();
        EnumChildWindows(parent, (h, p) => { found.Add(h); return true; }, IntPtr.Zero);
        return found;
    }
}
'@

if (-not ('UWin' -as [type])) { Add-Type -TypeDefinition $signature -Language CSharp }

New-Item -ItemType Directory -Force -Path (Split-Path $LogPath) | Out-Null

function Write-Line([string]$level, [string]$message) {
    $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $line = "$stamp  $level  $message"
    Add-Content -Path $LogPath -Value $line -Encoding utf8
    Write-Output $line
}

# Текст окна целиком: полезное часто лежит не в заголовке, а в подписи внутри.
function Get-DialogText([IntPtr]$hwnd) {
    $parts = New-Object System.Collections.Generic.List[string]
    $parts.Add([UWin]::Text($hwnd))
    foreach ($child in [UWin]::Children($hwnd)) {
        $t = [UWin]::Text($child)
        if ($t) { $parts.Add($t) }
    }
    return ($parts -join ' | ')
}

# Кнопка с нужной надписью. Ищется по КЛАССУ Button и тексту: нажать именно ту,
# что решили нажать, а не «первую попавшуюся».
function Find-Button([IntPtr]$hwnd, [string]$caption) {
    foreach ($child in [UWin]::Children($hwnd)) {
        if ([UWin]::Class($child) -ne 'Button') { continue }
        $text = [UWin]::Text($child) -replace '&', ''
        if ($text -and ($text -imatch "^$caption$")) { return $child }
    }
    return [IntPtr]::Zero
}

$BM_CLICK = 0x00F5
$deadline = (Get-Date).AddSeconds($Seconds)
$seen = @{}

Write-Line 'СТАРТ' "сторож окон Unity на $Seconds с; лог: $LogPath"

while ((Get-Date) -lt $deadline) {
    $unity = Get-Process Unity -ErrorAction SilentlyContinue
    foreach ($process in $unity) {
        foreach ($hwnd in [UWin]::TopLevel([uint32]$process.Id)) {
            # Главное окно редактора — не диалог. Отличаем по наличию кнопок:
            # у диалога они есть, у главного окна детей-Button нет.
            $buttons = @([UWin]::Children($hwnd) | Where-Object { [UWin]::Class($_) -eq 'Button' })
            if ($buttons.Count -eq 0) { continue }

            $text = Get-DialogText $hwnd
            if (-not $text) { continue }

            $key = "$hwnd|$text"
            if ($seen.ContainsKey($key)) { continue }

            $denied = $Deny | Where-Object { $text -imatch $_ }
            if ($denied) {
                if (-not $seen.ContainsKey($key)) {
                    Write-Line 'НЕ ТРОГАЮ' "запрещено правилом «$denied»: $text"
                    $seen[$key] = $true
                }
                continue
            }

            $rule = $Allow | Where-Object { $text -imatch $_.Match } | Select-Object -First 1
            if (-not $rule) {
                Write-Line 'НЕЗНАКОМО' "окно не в списке разрешённых, оставляю человеку: $text"
                $seen[$key] = $true
                continue
            }

            $button = Find-Button $hwnd $rule.Button
            if ($button -eq [IntPtr]::Zero) {
                Write-Line 'НЕТ КНОПКИ' "«$($rule.Button)» не найдена: $text"
                $seen[$key] = $true
                continue
            }

            [void][UWin]::SendMessageW($button, $BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero)
            Write-Line 'НАЖАЛ' "«$($rule.Button)»: $text"
            $seen[$key] = $true
        }
    }

    Start-Sleep -Milliseconds 700
}

Write-Line 'КОНЕЦ' 'время сторожа вышло'
