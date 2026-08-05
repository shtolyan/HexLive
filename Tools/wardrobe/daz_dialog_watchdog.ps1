<#
.SYNOPSIS
    Dismisses DAZ Studio's harmless "missing content" dialogs during a run.

.DESCRIPTION
    DazScript runs on DAZ's Qt MAIN thread, and a modal dialog owns that thread
    for as long as it is up. So one "Missing Files" box — a texture the product
    shipped without — freezes the script server, and from the outside a wait for
    a human click is indistinguishable from a hang. Scripting cannot suppress
    the prompt: DAZ has no API for it.

    What still works is Win32. The dialog is an ordinary window, enumerable and
    clickable from outside even while the thread is blocked. This watchdog polls
    for one and posts a click to its OK button — no screenshots, no model in the
    loop, no moving the physical mouse, and it works with the window unfocused.

    TWO RULES MAKE IT SAFE RATHER THAN RECKLESS:

    1. An ALLOW list by window text, and a DENY list that overrides it. DAZ also
       asks "overwrite this file?" — and we write FBX files. A watchdog that
       clicks everything would one day confirm something it should not, and the
       week after we would be hunting an export that quietly clobbered someone's
       file. Anything unrecognised is left alone, logged, and reported.

    2. EVERY dismissal is logged. Otherwise we simply stop seeing that content
       is installed incomplete, and it resurfaces later as white shoes — which
       is exactly how the Flair pumps shipped, their missing map showing up not
       only on the icon but on the character.

    This is pain relief, not a cure: the scene still loads without the texture.
    Install the missing content too — the log says which.

.PARAMETER LogPath
    Where dismissals and unrecognised dialogs are recorded. Defaults beside the
    wardrobe downloads.

.PARAMETER WatchOnly
    Report what would be clicked, click nothing. Use this first on a new dialog.

.PARAMETER TimeoutSeconds
    Stop after this long. 0 = run until the process is stopped.

.EXAMPLE
    powershell -File daz_dialog_watchdog.ps1 -WatchOnly -TimeoutSeconds 30
#>
[CmdletBinding()]
param(
    [string]$LogPath = "$env:LOCALAPPDATA\Temp\wardrobe\daz-dialogs.log",
    [switch]$WatchOnly,
    [int]$TimeoutSeconds = 0,
    [int]$PollMilliseconds = 500
)

# Recognised as a harmless report that content is missing. Matched against the
# dialog's title and its child WIDGET NAMES, case-insensitively.
#
# Not its body text — there is none to read. DAZ is a Qt application, and Qt
# gives a native window only to the top-level dialog; every label and button
# inside is drawn, not a child HWND. What GetWindowText returns for those
# children is Qt's objectName, so `MissingAssetsDlgUnknownFilesTEdt` is
# available and the path it displays is not. That makes the object names the
# more reliable signal anyway — a title can be localised, `MissingAssetsDlg`
# cannot.
#
# Which file is missing therefore has to come from DAZ's own log
# (%APPDATA%\DAZ 3D\Studio4\log.txt), not from here.
$Allow = @(
    # ЕДИНСТВЕННОЕ и множественное: DAZ показывает «Missing Files» списком,
    # но на один потерянный файл — «Missing File». Второе встало прогоном, потому
    # что шаблон требовал 's'.
    'missing files?',
    'missingassetsdlg',
    'the files listed below could not be found',
    'could not find file',
    'file not found',
    'unable to (find|locate|load) (the )?(file|image|texture)',
    # Приветственный экран DAZ. Не отчёт об ошибке, но ведёт себя как окно: висит
    # поверх и ждёт человека, а прогон в это время стоит. Закрыть его безопасно —
    # он ничего не спрашивает и ничего не перезаписывает.
    '^welcome$',
    'welcomedlg'
)

# Not dialogs at all — DAZ's own progress box and friends. Listed so they stay
# out of the log instead of drowning the things worth reading.
$Ignore = @('^Progress$')

# Wins over $Allow, always. These change something on disk or in the scene, and
# a machine must never answer them.
$Deny = @(
    'overwrite', 'replace', 'already exists',
    'delete', 'remove', 'discard',
    'save', 'unsaved', 'quit', 'exit', 'close without',
    'purchase', 'install', 'update', 'license', 'activate'
)

$signature = @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class Win {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    // CharSet.Unicode is not decoration: DllImport marshals a StringBuilder as
    // ANSI by default, so the W functions' UTF-16 output came back as its first
    // byte only — every window title read as a single letter ("Missing Files"
    // arrived as "M") and nothing ever matched the list.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr wp, IntPtr lp);

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

if (-not ('Win' -as [type])) { Add-Type -TypeDefinition $signature -Language CSharp }

New-Item -ItemType Directory -Force -Path (Split-Path $LogPath) | Out-Null

function Write-Line([string]$level, [string]$message) {
    $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $line = "$stamp  $level  $message"
    Add-Content -Path $LogPath -Value $line -Encoding utf8
    Write-Output $line
}

# The whole dialog as one string: DAZ puts the useful part (which file is
# missing) in a child label, not in the title.
function Get-DialogText([IntPtr]$hwnd) {
    $parts = New-Object System.Collections.Generic.List[string]
    $parts.Add([Win]::Text($hwnd))
    foreach ($child in [Win]::Children($hwnd)) {
        $t = [Win]::Text($child)
        if ($t) { $parts.Add($t) }
    }
    return ($parts -join ' | ')
}

# A native OK button, if this dialog has one. Qt ones do not — see the note on
# $Allow — but a plain Win32 message box would, and clicking the actual button
# is more precise than closing the window, so it is tried first.
function Find-OkButton([IntPtr]$hwnd) {
    foreach ($child in [Win]::Children($hwnd)) {
        if ([Win]::Class($child) -ne 'Button') { continue }
        # Exactly OK. Never Yes, never Cancel, never "OK to All".
        if (([Win]::Text($child) -replace '&', '') -eq 'OK') { return $child }
    }
    return [IntPtr]::Zero
}

$BM_CLICK = 0x00F5
$WM_CLOSE = 0x0010
$deadline = if ($TimeoutSeconds -gt 0) { (Get-Date).AddSeconds($TimeoutSeconds) } else { [datetime]::MaxValue }
$seen = @{}
$closed = @{}

Write-Line 'START' ("сторож запущен" + $(if ($WatchOnly) { " (только наблюдение)" } else { "" }))

while ((Get-Date) -lt $deadline) {
    $daz = Get-Process -Name 'DAZStudio' -ErrorAction SilentlyContinue
    foreach ($process in $daz) {
        foreach ($hwnd in [Win]::TopLevel([uint32]$process.Id)) {
            $title = [Win]::Text($hwnd)
            # The main window is not a dialog. It is the one with the app name.
            if ($title -match 'daz studio' -or -not $title) { continue }
            if ($Ignore | Where-Object { $title -imatch $_ }) { continue }

            $text = Get-DialogText $hwnd
            $key = "$hwnd|$text"

            if ($closed.ContainsKey($key)) {
                if ((Get-Date) -lt $closed[$key]) { continue }
                $closed.Remove($key) | Out-Null
                $seen.Remove($key) | Out-Null
            }

            $denied = $Deny | Where-Object { $text -imatch $_ }
            $allowed = $Allow | Where-Object { $text -imatch $_ }

            if ($denied) {
                if (-not $seen.ContainsKey($key)) {
                    $seen[$key] = $true
                    Write-Line 'ОСТАВЛЕНО' "окно требует решения человека ($($denied -join ', ')): $title — $text"
                }
                continue
            }

            if (-not $allowed) {
                if (-not $seen.ContainsKey($key)) {
                    $seen[$key] = $true
                    Write-Line 'НЕЗНАКОМО' "не в белом списке, не трогаю: $title — $text"
                }
                continue
            }

            $ok = Find-OkButton $hwnd
            $how = if ($ok -ne [IntPtr]::Zero) { 'кнопкой OK' } else { 'закрытием окна' }

            if ($WatchOnly) {
                if (-not $seen.ContainsKey($key)) {
                    $seen[$key] = $true
                    Write-Line 'НАЖАЛ БЫ' "$how : $title"
                }
                continue
            }

            # PostMessage, not SendMessage: the dialog runs on the very thread
            # we are trying to free, and a synchronous send would wait on it.
            if ($ok -ne [IntPtr]::Zero) {
                [void][Win]::PostMessageW($ok, $BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero)
            }
            else {
                # Qt draws its buttons rather than giving them windows, so there
                # is nothing to click. WM_CLOSE is what the title bar's X sends,
                # and on a one-button information box that is the same answer as
                # OK. It is also the SAFE direction: on anything asking a real
                # question, closing means "no" — though the deny list means we
                # never get here for one of those.
                [void][Win]::PostMessageW($hwnd, $WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero)
            }

            Write-Line 'ЗАКРЫТО' "$how : $title — что именно не нашлось, смотрите в логе DAZ (%APPDATA%\DAZ 3D\Studio4\log.txt)"
            # The close is posted, not synchronous, so the window is still there
            # on the next pass a few hundred milliseconds later. Without this it
            # gets closed — and logged — two or three times over.
            $seen[$key] = $true
            $closed[$key] = (Get-Date).AddSeconds(3)
        }
    }
    Start-Sleep -Milliseconds $PollMilliseconds
}

Write-Line 'STOP' 'сторож остановлен'
