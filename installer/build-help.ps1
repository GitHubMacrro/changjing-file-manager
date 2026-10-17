#requires -Version 5.1
<#
.SYNOPSIS
    由 docs\使用手册.md 生成 installer\使用说明.html（自包含、无外部依赖）。

.DESCRIPTION
    - 单文件 HTML，样式内嵌，不引用外部资源；
    - 图片默认省略（仅保留其替代文字所在段落），保证自包含；
    - 支持的 Markdown 子集：标题、段落、无序 / 有序列表、表格、引用块、
      分隔线、粗体 **、行内代码 `、代码块 ```；
      原生 HTML 标签（如 <kbd>）原样透传。

.PARAMETER ManualPath
    使用手册 Markdown 路径，默认 ..\docs\使用手册.md。

.PARAMETER OutputPath
    输出 HTML 路径，默认 .\使用说明.html。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\build-help.ps1
#>
[CmdletBinding()]
param(
    [string]$ManualPath,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot 在部分调用方式下于参数绑定期为空，故此处再解析
$scriptDir = $PSScriptRoot
if ([string]::IsNullOrEmpty($scriptDir)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrEmpty($ManualPath)) {
    $ManualPath = Join-Path $scriptDir '..\docs\使用手册.md'
}

if ([string]::IsNullOrEmpty($OutputPath)) {
    $OutputPath = Join-Path $scriptDir '使用说明.html'
}

$ManualPath = (Resolve-Path -LiteralPath $ManualPath).Path
$lines = Get-Content -LiteralPath $ManualPath -Encoding UTF8

function Convert-Inline([string]$text) {
    $s = $text
    # 行内代码
    $s = [regex]::Replace($s, '`([^`]+)`', '<code>$1</code>')
    # 粗体
    $s = [regex]::Replace($s, '\*\*([^*]+)\*\*', '<strong>$1</strong>')
    return $s
}

$body = New-Object System.Text.StringBuilder
$inCode = $false
$inTable = $false
$listType = ''

function Close-List {
    if ($script:listType -eq 'ul') { [void]$body.AppendLine('</ul>') }
    elseif ($script:listType -eq 'ol') { [void]$body.AppendLine('</ol>') }
    $script:listType = ''
}

function Close-Table {
    if ($script:inTable) { [void]$body.AppendLine('</tbody></table>'); $script:inTable = $false }
}

foreach ($raw in $lines) {
    $line = $raw.TrimEnd()

    # 代码块
    if ($line -match '^```') {
        Close-List
        Close-Table
        if (-not $inCode) { [void]$body.AppendLine('<pre><code>'); $inCode = $true }
        else { [void]$body.AppendLine('</code></pre>'); $inCode = $false }
        continue
    }

    if ($inCode) {
        [void]$body.AppendLine([System.Net.WebUtility]::HtmlEncode($line))
        continue
    }

    # 空行
    if ($line.Trim().Length -eq 0) {
        Close-List
        Close-Table
        continue
    }

    # 分隔线
    if ($line -match '^---+$') {
        Close-List
        Close-Table
        [void]$body.AppendLine('<hr>')
        continue
    }

    # 标题
    if ($line -match '^(#{1,6})\s+(.*)$') {
        Close-List
        Close-Table
        $level = [Math]::Min($matches[1].Length, 6)
        [void]$body.AppendLine("<h$level>$(Convert-Inline $matches[2])</h$level>")
        continue
    }

    # 表格
    if ($line -match '^\|(.+)\|\s*$') {
        Close-List
        $cells = ($matches[1] -split '\|') | ForEach-Object { $_.Trim() }
        # 分隔行
        if ($cells.Count -gt 0 -and ($cells[0] -match '^:?-+:?$')) {
            continue
        }

        if (-not $inTable) {
            [void]$body.AppendLine('<table><thead><tr>')
            foreach ($c in $cells) { [void]$body.AppendLine("<th>$(Convert-Inline $c)</th>") }
            [void]$body.AppendLine('</tr></thead><tbody>')
            $inTable = $true
        }
        else {
            [void]$body.AppendLine('<tr>')
            foreach ($c in $cells) { [void]$body.AppendLine("<td>$(Convert-Inline $c)</td>") }
            [void]$body.AppendLine('</tr>')
        }
        continue
    }
    else {
        Close-Table
    }

    # 引用块
    if ($line -match '^>\s?(.*)$') {
        Close-List
        [void]$body.AppendLine("<blockquote><p>$(Convert-Inline $matches[1])</p></blockquote>")
        continue
    }

    # 图片：省略，仅保留替代文字
    if ($line -match '^!\[([^\]]*)\]\([^)]*\)\s*$') {
        Close-List
        continue
    }

    # 无序列表
    if ($line -match '^\s*[-*]\s+(.*)$') {
        if ($listType -ne 'ul') { Close-List; [void]$body.AppendLine('<ul>'); $listType = 'ul' }
        [void]$body.AppendLine("<li>$(Convert-Inline $matches[1])</li>")
        continue
    }

    # 有序列表
    if ($line -match '^\s*\d+\.\s+(.*)$') {
        if ($listType -ne 'ol') { Close-List; [void]$body.AppendLine('<ol>'); $listType = 'ol' }
        [void]$body.AppendLine("<li>$(Convert-Inline $matches[1])</li>")
        continue
    }

    # 普通段落
    Close-List
    [void]$body.AppendLine("<p>$(Convert-Inline $line)</p>")
}

Close-List
Close-Table
if ($inCode) { [void]$body.AppendLine('</code></pre>') }

$html = @"
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>畅径 使用手册</title>
<style>
  :root { --fg:#1f2328; --muted:#57606a; --line:#d0d7de; --accent:#0969da; --code-bg:#f6f8fa; --note-bg:#f6f8fa; }
  * { box-sizing: border-box; }
  body {
    margin: 0; padding: 2rem 1.25rem 4rem;
    font-family: "Microsoft YaHei", "PingFang SC", "Segoe UI", system-ui, sans-serif;
    color: var(--fg); background:#fff; line-height: 1.75; font-size: 16px;
  }
  .wrap { max-width: 880px; margin: 0 auto; }
  h1 { font-size: 1.9rem; margin: 0 0 .6rem; }
  h2 { font-size: 1.3rem; margin: 2.2rem 0 .6rem; padding-bottom: .3rem; border-bottom: 2px solid var(--line); }
  h3 { font-size: 1.05rem; margin: 1.3rem 0 .4rem; }
  p, li { margin: .5rem 0; }
  code, kbd {
    font-family: Consolas, "Cascadia Mono", Menlo, monospace;
    background: var(--code-bg); border: 1px solid var(--line);
    border-radius: 4px; padding: .1em .35em; font-size: .9em;
  }
  pre { background: var(--code-bg); border: 1px solid var(--line); border-radius: 6px; padding: .8rem 1rem; overflow: auto; }
  pre code { background: none; border: none; padding: 0; }
  table { border-collapse: collapse; width: 100%; margin: .8rem 0; font-size: .95rem; }
  th, td { border: 1px solid var(--line); padding: .45rem .7rem; text-align: left; vertical-align: top; }
  th { background: var(--code-bg); white-space: nowrap; }
  tbody tr:nth-child(even) { background: #fafbfc; }
  ul, ol { padding-left: 1.4rem; }
  blockquote { margin: 1rem 0; padding: .6rem 1rem; border-left: 4px solid var(--accent); background: var(--note-bg); border-radius: 4px; }
  blockquote p { margin: 0; }
  hr { border: none; border-top: 1px solid var(--line); margin: 2rem 0; }
  @media print {
    body { padding: 0; font-size: 11pt; }
    .wrap { max-width: 100%; }
    h2 { break-after: avoid; }
    table, blockquote, pre { break-inside: avoid; }
  }
</style>
</head>
<body>
<div class="wrap">
$($body.ToString())
</div>
</body>
</html>
"@

$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($OutputPath, $html, $utf8)
Write-Host "已生成：$OutputPath"
