# The WitOS session banner (plan step C1.4; @Docs/WitOS-Console-Logos.md): the profile of every interactive PowerShell
# session renders one of the marks of /etc/witos/logo with the facts of the running system beside it. The kernel, the
# root task and the services never print it (ADR 0024: mechanisms only); a redirected output, a non-interactive session
# or WITOS_NO_LOGO=1 sees nothing. Every value of the panel is read at run time; a value that cannot be read is omitted.
# The volumes as PowerShell drives (plan step E1, ADR 0032 decision 5): a file system drive named by each volume's alias
# over its mount point (Data:, Boot:, a USB drive's name below /media), from /etc/mtab, whose first field the libc writes
# as the alias. A drive is PowerShell's, never a System.IO path; every session that runs the profile gets them.
try {
    foreach ($entry in Get-Content -LiteralPath /etc/mtab -ErrorAction Stop) {
        $fields = $entry -split ' '
        if ($fields.Count -lt 2 -or $fields[1] -eq '/' -or $fields[0] -notmatch '^[A-Za-z0-9_-]+$') { continue }
        if (-not (Get-PSDrive -Name $fields[0] -ErrorAction SilentlyContinue)) {
            New-PSDrive -Name $fields[0] -PSProvider FileSystem -Root $fields[1] -Scope Global | Out-Null
        }
    }
} catch { }

if ($env:WITOS_NO_LOGO -or [Console]::IsOutputRedirected -or [Console]::IsInputRedirected) { return }
$logo = '/etc/witos/logo'
if (-not (Test-Path -LiteralPath "$logo/palette.json")) { return }

$esc = [char]27
$dumb = $env:TERM -eq 'dumb' -or [Console]::OutputEncoding.CodePage -ne 65001
$depth = if ($env:NO_COLOR -or $dumb) { 0 } elseif ($env:COLORTERM -in 'truecolor', '24bit') { 24 } elseif ($env:TERM -like '*-256color') { 256 } else { 16 }
$width = try { [Console]::WindowWidth } catch { 80 }
if ($width -le 0) { $width = 80 }
$palette = Get-Content -LiteralPath "$logo/palette.json" -Raw | ConvertFrom-Json
$reset = if ($depth -eq 0) { '' } else { "$esc[0m" }

function Get-WitColour([string] $role) {
    if ($depth -eq 0) { return '' }
    $c = $palette.roles.$role
    if ($depth -eq 24) {
        $h = $c.truecolor.TrimStart('#')
        return "$esc[38;2;$([Convert]::ToInt32($h.Substring(0, 2), 16));$([Convert]::ToInt32($h.Substring(2, 2), 16));$([Convert]::ToInt32($h.Substring(4, 2), 16))m"
    }
    if ($depth -eq 256) { return "$esc[38;5;$($c.'256')m" }
    return "$esc[$($c.'16')m"
}

function Get-WitFields {
    $f = [ordered]@{}
    try { $os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription; if ($os) { $f['OS'] = $os } } catch { }
    try { $f['Kernel'] = "nano-kernel · $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)" } catch { }
    try {
        $versions = (Get-ChildItem -LiteralPath /dotnet/shared/Microsoft.NETCore.App -Directory -ErrorAction Stop | Sort-Object Name -Descending |
            Select-Object -ExpandProperty Name) -join ', '
        if ($versions) { $f['Runtime'] = ".NET $versions" }
    } catch { }
    $f['Shell'] = "PowerShell $($PSVersionTable.PSVersion)"
    try {
        # The volumes by their aliases (ADR 0032), the package at / apart.
        $aliases = (Get-Content -LiteralPath /etc/mtab -ErrorAction Stop | Where-Object { ($_ -split ' ')[1] -ne '/' } |
            ForEach-Object { ($_ -split ' ')[0] }) -join ', '
        if ($aliases) { $f['Volumes'] = $aliases }
    } catch { }
    return $f
}

function Limit([string] $text, [int] $max) {
    if ($max -lt 2) { return '' }
    if ($text.Length -gt $max) { return $text.Substring(0, $max - 1) + '…' }
    return $text
}

$title = try { [Environment]::MachineName } catch { '' }
if (-not $title) { $title = 'witos' }
$label = Get-WitColour $palette.labels

if ($dumb) {
    # No UTF-8 or a dumb terminal: the ASCII mark, uncoloured (rule 2 of the guide).
    if ($width -lt 33) { Write-Host '== WitOS =='; Write-Host ''; return }
    Get-Content -LiteralPath "$logo/ascii.txt" | ForEach-Object { Write-Host $_ }
    Write-Host ''
    return
}
if ($width -lt 33) {
    Write-Host "$(Get-WitColour 'layer2')▓▒░ WitOS ░▒▓$reset"
    Write-Host ''
    return
}

$emblem = Get-Content -LiteralPath "$logo/emblem.txt"
$panel = @()
if ($width -ge 70) {
    $fields = Get-WitFields
    $panel += "$title"
    $panel += '────────────────────────────'
    foreach ($key in $fields.Keys) { if ($panel.Count -lt 7) { $panel += @($key, $fields[$key]) -join "`t" } }
}
for ($i = 0; $i -lt $emblem.Count; $i++) {
    $row = $emblem[$i]
    $colour = Get-WitColour $palette.emblemRows[$i]
    if ($depth -ne 0) { $row = $row -replace '[▓▒░]', '█' } # in colour the tiers are one shade, told apart by colour
    $line = $colour + $row.PadRight(32) + $reset
    if ($i -lt $panel.Count) {
        $text = $panel[$i]
        $available = $width - 36
        if ($i -eq 0) { $text = Limit $text $available }
        elseif ($i -eq 1) { $text = $label + (Limit $text $available) + $reset }
        else {
            $parts = $text -split "`t", 2
            $text = $label + $parts[0].PadRight(9) + $reset + (Limit $parts[1] ($available - 9))
        }
        $line += '   ' + $text
    }
    Write-Host $line
}
Write-Host ''
