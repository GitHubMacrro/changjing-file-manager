<#
.SYNOPSIS
  运行畅径 M10 回归 harness（tests\ChangJing.Regression）

.DESCRIPTION
  - 构建 tests\ChangJing.Regression（引用真实 ChangJing.App）
  - 进程内加载真实 MainWindow + 真实主题，运行 M5~M9 覆盖项
  - 测试数据只在 %TEMP%\cj-reg-*，结束清理（保留名用 \\?\ 删除）
  - 默认连续跑 2 次，全过才算通过

.PARAMETER Times
  运行次数（默认 2）

.PARAMETER SkipPerf
  跳过性能子套件（快速回归）

.PARAMETER Arch
  目标架构 x64（默认）或 x86；x86 时强制 32 位构建并运行

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tests\run-regression.ps1
#>
[CmdletBinding()]
param(
  [int]$Times = 2,
  [switch]$SkipPerf,
  [ValidateSet('x64', 'x86')]
  [string]$Arch = 'x64'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'ChangJing.Regression\ChangJing.Regression.csproj'

Write-Host "== ChangJing regression harness ==" -ForegroundColor Cyan

if ($SkipPerf) {
  $env:CJ_REG_SKIP_PERF = '1'
}
else {
  Remove-Item Env:\CJ_REG_SKIP_PERF -ErrorAction SilentlyContinue
}

if ($Arch -eq 'x86') {
  # 强制 32 位进程。本机只装了 x64 桌面运行时，故用自包含 win-x86（无需另装 x86 运行时）；
  # 输出到独立目录避免与 x64 产物混淆
  $buildArgs = @(
    $project, '-c', 'Release', '-v', 'q',
    '-p:PlatformTarget=x86', '-p:RuntimeIdentifier=win-x86', '-p:SelfContained=true',
    '-p:BaseOutputPath=bin\x86\'
  )
  $exe = Join-Path $PSScriptRoot 'ChangJing.Regression\bin\x86\Release\net8.0-windows10.0.17763.0\win-x86\ChangJing.Regression.exe'
}
else {
  $buildArgs = @($project, '-c', 'Release', '-v', 'q')
  $exe = Join-Path $PSScriptRoot 'ChangJing.Regression\bin\Release\net8.0-windows10.0.17763.0\ChangJing.Regression.exe'
}

Write-Host "Building $project (Arch=$Arch) ..."
dotnet build @buildArgs
if ($LASTEXITCODE -ne 0) {
  Write-Host "BUILD FAILED" -ForegroundColor Red
  exit 1
}

if (-not (Test-Path -LiteralPath $exe)) {
  Write-Host "Executable not found: $exe" -ForegroundColor Red
  exit 1
}

$failed = 0
for ($i = 1; $i -le $Times; $i++) {
  Write-Host ""
  Write-Host "----- run $i / $Times -----" -ForegroundColor Yellow
  & $exe
  $code = $LASTEXITCODE
  if ($code -ne 0) {
    Write-Host "run $i FAILED (exit $code)" -ForegroundColor Red
    $failed++
  }
  else {
    Write-Host "run $i PASSED (exit 0)" -ForegroundColor Green
  }
}

# Fallback cleanup: remove leftover %TEMP%\cj-reg-* (reserved names / long paths need \\?\).
Get-ChildItem -Path $env:TEMP -Directory -Filter 'cj-reg-*' -ErrorAction SilentlyContinue | ForEach-Object {
  $longPath = '\\?\' + $_.FullName
  try {
    cmd /c rd /s /q "$longPath" 2>$null
  }
  catch {
    Write-Host "cleanup failed: $($_.FullName)" -ForegroundColor DarkYellow
  }
}

Write-Host ""
if ($failed -eq 0) {
  Write-Host "ALL $Times RUNS PASSED" -ForegroundColor Green
  exit 0
}
else {
  Write-Host "$failed / $Times RUNS FAILED" -ForegroundColor Red
  exit 1
}
