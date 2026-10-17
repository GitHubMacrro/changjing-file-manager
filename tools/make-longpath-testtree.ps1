<#
.SYNOPSIS
    生成「畅径」长路径手工测试目录树。

.DESCRIPTION
    默认根目录 %TEMP%\ChangJingTest。为绕过 260 字符限制，脚本内部**只用扩展路径（\\?\ / \\?\UNC\）**
    调用 .NET 文件 API，因此可以创建总长 300 / 500 / 1000 / 1500 字符的多级目录、
    接近 255 字符的文件名、中文与 emoji 名、以点或空格结尾的名字、隐藏文件、空文件夹，
    以及一个含 5000 个文件的大目录。

    脚本可重复运行（已存在的目录与文件会被复用 / 覆盖）。

.PARAMETER Root
    测试树根目录（普通路径）。默认 %TEMP%\ChangJingTest。

.PARAMETER Clean
    删除整个测试树后退出（用扩展路径递归删除，支持超长路径）。

.PARAMETER ManyFileCount
    大目录中的文件数量，默认 5000。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\make-longpath-testtree.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\make-longpath-testtree.ps1 -Clean

.NOTES
    文件必须以「UTF-8 带 BOM」保存，否则 Windows PowerShell 5.1 会把中文与 emoji 读成乱码。
    脚本只写 %TEMP% 下的测试目录，不动其它位置。
#>

[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:TEMP 'ChangJingTest'),
    [switch]$Clean,
    [int]$ManyFileCount = 5000
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------------------------------------------------------------- 工具函数

function ConvertTo-LongPath {
    <# 普通路径 → 扩展路径。已是扩展路径时原样返回。 #>
    param([Parameter(Mandatory)][string]$Path)

    if ($Path.StartsWith('\\?\')) { return $Path }
    if ($Path.StartsWith('\\')) { return '\\?\UNC\' + $Path.Substring(2) }
    return '\\?' + '\' + $Path
}

function Join-Long {
    <# 用扩展路径拼接（不用 Join-Path / Path.Combine，避免任何规范化）。 #>
    param(
        [Parameter(Mandatory)][string]$Parent,
        [Parameter(Mandatory)][string]$Child
    )

    return $Parent.TrimEnd('\') + '\' + $Child
}

function New-LongDirectory {
    param([Parameter(Mandatory)][string]$LongPath)
    [System.IO.Directory]::CreateDirectory($LongPath) | Out-Null
}

function New-LongFile {
    param(
        [Parameter(Mandatory)][string]$LongPath,
        [string]$Content = 'changjing test file'
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($LongPath, $Content, $encoding)
}

function Remove-LongTree {
    <# 递归删除（扩展路径）。先试 Directory.Delete，失败则手工递归。 #>
    param([Parameter(Mandatory)][string]$LongRoot)

    if (-not [System.IO.Directory]::Exists($LongRoot)) { return }

    try {
        [System.IO.Directory]::Delete($LongRoot, $true)
        return
    }
    catch {
        Write-Verbose "Directory.Delete 失败，改用手工递归：$($_.Exception.Message)"
    }

    Remove-LongTreeManual -LongPath $LongRoot
    [System.IO.Directory]::Delete($LongRoot, $false)
}

function Remove-LongTreeManual {
    param([Parameter(Mandatory)][string]$LongPath)

    foreach ($file in [System.IO.Directory]::GetFiles($LongPath)) {
        [System.IO.File]::SetAttributes($file, [System.IO.FileAttributes]::Normal)
        [System.IO.File]::Delete($file)
    }

    foreach ($dir in [System.IO.Directory]::GetDirectories($LongPath)) {
        [System.IO.File]::SetAttributes($dir, [System.IO.FileAttributes]::Normal)
        Remove-LongTreeManual -LongPath $dir
        [System.IO.Directory]::Delete($dir, $false)
    }
}

function New-DeepTree {
    <#
        创建一条多级目录链，使最深一层的文件「完整普通路径」长度尽量等于 TargetLength。
        返回最深文件的普通路径。
    #>
    param(
        [Parameter(Mandatory)][string]$RootLong,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][int]$TargetLength,
        [int]$SegmentLength = 20,
        [int]$LeafReserve = 64
    )

    $current = Join-Long $RootLong $Name
    New-LongDirectory -LongPath $current

    $level = 0
    while ($level -lt 200) {
        $prefix = 'l{0:D3}-' -f $level
        if ($prefix.Length -gt $SegmentLength) { break }
        $segment = $prefix.PadRight($SegmentLength, 'x')

        $candidate = Join-Long $current $segment
        if (($candidate.Length + 1 + $LeafReserve) -gt $TargetLength) { break }

        $current = $candidate
        New-LongDirectory -LongPath $current

        # 每一层放一个小文件，便于手工浏览时看到内容。
        New-LongFile -LongPath (Join-Long $current ('level-{0:D3}.txt' -f $level)) -Content "level $level"

        $level++
    }

    # 最深一层的叶子文件：名字长度补齐到目标总长。
    $nameLength = $TargetLength - $current.Length - 1
    if ($nameLength -lt 5) { $nameLength = 5 }
    if ($nameLength -gt 200) { $nameLength = 200 }

    $leafName = 'leaf-' + ('y' * ($nameLength - 5))
    $leafPath = Join-Long $current $leafName
    New-LongFile -LongPath $leafPath -Content '最深处的内容 / deepest content'

    return $leafPath
}

# ---------------------------------------------------------------- 主流程

$rootFull = [System.IO.Path]::GetFullPath($Root)
$rootLong = ConvertTo-LongPath $rootFull

Write-Host "测试树根目录（普通路径）：$rootFull"
Write-Host "测试树根目录（扩展路径）：$rootLong"
Write-Host ''

if ($Clean) {
    Write-Host '正在删除测试树…'
    Remove-LongTree -LongRoot $rootLong
    if ([System.IO.Directory]::Exists($rootLong)) {
        Write-Warning "删除后目录仍然存在：$rootLong"
    }
    else {
        Write-Host '测试树已删除。'
    }
    return
}

if (-not [System.IO.Directory]::Exists($rootLong)) {
    New-LongDirectory -LongPath $rootLong
}

$summary = New-Object System.Collections.Generic.List[string]

# 1) 多级超长目录：300 / 500 / 1000 / 1500 字符。
$deepTargets = @(300, 500, 1000, 1500)
foreach ($target in $deepTargets) {
    $leaf = New-DeepTree -RootLong $rootLong -Name ("deep-{0}" -f $target) -TargetLength $target
    $leafDisplay = $leaf.Substring(4)
    $line = "deep-{0,-4}  最深文件长度 = {1,5}  {2}" -f $target, $leafDisplay.Length, $leafDisplay
    Write-Host $line
    $summary.Add($line)
}

# 2) 接近 255 字符的文件名（含扩展名）。
$longNameDir = Join-Long $rootLong 'long-names'
New-LongDirectory -LongPath $longNameDir

foreach ($totalNameLength in @(200, 250, 254, 255)) {
    $stemLength = $totalNameLength - 4          # 后缀 ".txt"
    $name = ('n' * $stemLength) + '.txt'
    $filePath = Join-Long $longNameDir $name
    New-LongFile -LongPath $filePath -Content ('name length = ' + $name.Length)
    $line = "long-names 文件名长度 = {0}（上限 255），完整普通路径长度 = {1}" -f $name.Length, ($filePath.Length - 4)
    Write-Host $line
    $summary.Add($line)
}

# 3) 中文与 emoji 名。
$unicodeDir = Join-Long $rootLong 'unicode'
New-LongDirectory -LongPath $unicodeDir

$cnDir = Join-Long $unicodeDir '中文文件夹'
New-LongDirectory -LongPath $cnDir
New-LongFile -LongPath (Join-Long $cnDir '中文文件名-测试.txt') -Content '中文内容'

$emojiDir = Join-Long $unicodeDir ([char]::ConvertFromUtf32(0x1F4C1) + 'emoji-folder-' + [char]::ConvertFromUtf32(0x1F680))
New-LongDirectory -LongPath $emojiDir
New-LongFile -LongPath (Join-Long $emojiDir ('emoji-file-' + [char]::ConvertFromUtf32(0x1F4C4) + '.txt')) -Content 'emoji content'

Write-Host 'unicode     已创建：中文文件夹 / 中文文件名-测试.txt / emoji 文件夹与文件'
$summary.Add('unicode     中文与 emoji 名已创建')

# 4) 以点 / 空格结尾的名字（资源管理器处理不了，本工具应能处理）。
$trailingDir = Join-Long $rootLong 'trailing'
New-LongDirectory -LongPath $trailingDir

$trailingItems = @(
    'trailing-dot.',
    'trailing-space ',
    'trailing-dot-and-space. ',
    'double-dot..'
)

foreach ($item in $trailingItems) {
    New-LongFile -LongPath (Join-Long $trailingDir $item) -Content "name ends with dot/space: [$item]"
}

foreach ($item in @('dir-trailing-dot.', 'dir-trailing-space ')) {
    New-LongDirectory -LongPath (Join-Long $trailingDir $item)
    New-LongFile -LongPath (Join-Long (Join-Long $trailingDir $item) 'inside.txt') -Content 'inside'
}

Write-Host 'trailing    已创建：4 个结尾点/空格的文件 + 2 个结尾点/空格的文件夹'
$summary.Add('trailing    结尾点 / 空格的名字已创建')

# 5) 隐藏文件与隐藏文件夹（外加一个系统文件）。
$hiddenDir = Join-Long $rootLong 'hidden'
New-LongDirectory -LongPath $hiddenDir

$hiddenFile = Join-Long $hiddenDir 'hidden-file.txt'
New-LongFile -LongPath $hiddenFile -Content 'hidden'
[System.IO.File]::SetAttributes($hiddenFile, [System.IO.FileAttributes]::Hidden)

$systemFile = Join-Long $hiddenDir 'system-file.txt'
New-LongFile -LongPath $systemFile -Content 'system'
[System.IO.File]::SetAttributes($systemFile, [System.IO.FileAttributes]::System)

$normalFile = Join-Long $hiddenDir 'visible-file.txt'
New-LongFile -LongPath $normalFile -Content 'visible'

$hiddenSubDir = Join-Long $hiddenDir 'hidden-folder'
New-LongDirectory -LongPath $hiddenSubDir
New-LongFile -LongPath (Join-Long $hiddenSubDir 'inside.txt') -Content 'inside'
[System.IO.File]::SetAttributes($hiddenSubDir, [System.IO.FileAttributes]::Hidden)

Write-Host 'hidden      已创建：隐藏文件 / 系统文件 / 普通文件 / 隐藏文件夹'
$summary.Add('hidden      隐藏与系统条目已创建')

# 6) 空文件夹。
$emptyDir = Join-Long $rootLong 'empty'
New-LongDirectory -LongPath $emptyDir
New-LongDirectory -LongPath (Join-Long (Join-Long $emptyDir 'empty-child') 'empty-grandchild')

Write-Host 'empty       已创建：空文件夹（含嵌套空文件夹）'
$summary.Add('empty       空文件夹已创建')

# 7) 大目录：默认 5000 个文件。
$manyDir = Join-Long $rootLong 'many-files'
New-LongDirectory -LongPath $manyDir

Write-Host ("many-files  正在创建 {0} 个文件…" -f $ManyFileCount)
$content = New-Object System.Text.UTF8Encoding($false)
for ($i = 1; $i -le $ManyFileCount; $i++) {
    $name = 'f{0:D5}.dat' -f $i
    [System.IO.File]::WriteAllText((Join-Long $manyDir $name), "file $i", $content)

    if (($i % 1000) -eq 0) {
        Write-Host ("            … 已完成 {0}" -f $i)
    }
}

$line = "many-files  已创建 {0} 个文件" -f $ManyFileCount
Write-Host $line
$summary.Add($line)

# ---------------------------------------------------------------- 汇总

Write-Host ''
Write-Host '==== 测试树摘要 ===='
foreach ($line in $summary) { Write-Host $line }

Write-Host ''
Write-Host '提示：删除测试树请用 -Clean；删除 1000+ 字符路径的目录时，资源管理器会失败，本脚本用的是扩展路径。'
