#requires -Version 5.1
<#
.SYNOPSIS
    畅径（ChangJing）安装包与便携包构建脚本。

.DESCRIPTION
    1. 发布 win-x64、win-x86 自包含（self-contained）程序到 .build\publish-<arch>\；
    2. 调用 Inno Setup 6 编译两个安装包；
    3. 生成两个便携 zip；
    4. 输出 SHA256SUMS.txt；
    5. 通过 Sign-Artifact 预留代码签名接入点（默认不签名）。

.NOTES
    版本号只在 $Version 一处定义。
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ============================================================================
#  版本号：全流程唯一定义处
# ============================================================================
$Version = '1.1.0'

# ============================================================================
#  路径与常量
# ============================================================================
$InstallerDir = $PSScriptRoot
$Root         = Split-Path -Parent $InstallerDir            # 仓库根 ChangJing
$Project      = Join-Path $Root 'ChangJing.App\ChangJing.App.csproj'
$BuildRoot    = Join-Path $InstallerDir '.build'
$DeliverDir   = Join-Path $Root "deliver\$Version"
$IsccExe      = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
$IssFile      = Join-Path $InstallerDir 'ChangJing.iss'
$HtmlFile     = Join-Path $InstallerDir '使用说明.html'
$LicenseFile  = Join-Path $Root 'LICENSE'
$LicensesDir  = Join-Path $Root 'licenses'
$ArchList     = @('x64', 'x86')

# ============================================================================
#  代码签名：默认关闭。启用后请在下方补全 signtool.exe 调用。
# ============================================================================
$SigningEnabled = $false

function Sign-Artifact {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )
    if (-not $SigningEnabled) {
        Write-Host ("  [签名] 已跳过（未启用）：{0}" -f (Split-Path -Leaf $Path))
        return
    }

    # ==== 接入 signtool.exe 的位置（启用 $SigningEnabled 后补全）=============
    # $signtool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe'
    # & $signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 $Path
    # if ($LASTEXITCODE -ne 0) { throw "代码签名失败：$Path" }
    # ========================================================================
    throw '签名已启用，但尚未接入 signtool.exe，请补全 Sign-Artifact。'
}

function Assert-File {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "预期产物不存在：$Path"
    }
}

function Write-Step {
    param([string]$Text)
    Write-Host ""
    Write-Host ("==== {0} ====" -f $Text) -ForegroundColor Cyan
}

# ============================================================================
#  前置检查
# ============================================================================
Write-Step "前置检查"
foreach ($p in @($Project, $IssFile, $HtmlFile, $LicenseFile, $LicensesDir)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "缺少必要输入：$p" }
}
if (-not (Test-Path -LiteralPath $IsccExe)) { throw "未找到 Inno Setup 编译器：$IsccExe" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '未找到 dotnet CLI。' }
Write-Host ("dotnet 版本：" + (& dotnet --version))

# ============================================================================
#  准备输出目录
# ============================================================================
if (Test-Path -LiteralPath $BuildRoot) { Remove-Item -LiteralPath $BuildRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $BuildRoot  | Out-Null
New-Item -ItemType Directory -Force -Path $DeliverDir | Out-Null

$artifacts = [System.Collections.Generic.List[string]]::new()

# ============================================================================
#  1) 发布两个架构
# ============================================================================
foreach ($arch in $ArchList) {
    Write-Step "发布 win-$arch"
    $publishDir = Join-Path $BuildRoot "publish-$arch"
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    & dotnet publish $Project `
        -c Release `
        -r "win-$arch" `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishReadyToRun=true `
        -p:PublishTrimmed=false `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (win-$arch) 失败，退出码 $LASTEXITCODE" }

    Assert-File (Join-Path $publishDir 'ChangJing.exe')
    Write-Host ("  发布完成：{0}" -f $publishDir)
}

# ============================================================================
#  2) 编译两个安装包
# ============================================================================
foreach ($arch in $ArchList) {
    Write-Step "编译安装包 $arch"
    $publishDir = Join-Path $BuildRoot "publish-$arch"

    & $IsccExe `
        "/DArch=$arch" `
        "/DAppVersion=$Version" `
        "/DOutputDir=$DeliverDir" `
        "/DPublishDir=$publishDir" `
        $IssFile
    if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败（$arch），退出码 $LASTEXITCODE" }

    $setup = Join-Path $DeliverDir "ChangJing-Setup-$Version-$arch.exe"
    Assert-File $setup
    Sign-Artifact -Path $setup
    $artifacts.Add($setup)
    Write-Host ("  安装包：{0}（{1:N2} MB）" -f $setup, ((Get-Item -LiteralPath $setup).Length / 1MB))
}

# ============================================================================
#  3) 生成便携 zip（发布目录 + 使用说明 + 许可证）
# ============================================================================
foreach ($arch in $ArchList) {
    Write-Step "打包便携版 $arch"
    $publishDir  = Join-Path $BuildRoot "publish-$arch"
    $portableDir = Join-Path $BuildRoot "portable-$arch"
    New-Item -ItemType Directory -Force -Path $portableDir | Out-Null

    Copy-Item -Path (Join-Path $publishDir '*') -Destination $portableDir -Recurse -Force
    Copy-Item -LiteralPath $HtmlFile    -Destination $portableDir -Force
    Copy-Item -LiteralPath $LicenseFile -Destination $portableDir -Force
    Copy-Item -LiteralPath $LicensesDir -Destination $portableDir -Recurse -Force

    $zip = Join-Path $DeliverDir "ChangJing-$Version-$arch-portable.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $portableDir '*') -DestinationPath $zip -CompressionLevel Optimal
    Assert-File $zip
    Sign-Artifact -Path $zip
    $artifacts.Add($zip)
    Write-Host ("  便携包：{0}（{1:N2} MB）" -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB))
}

# ============================================================================
#  4) 生成 SHA256SUMS.txt
# ============================================================================
Write-Step "生成 SHA256SUMS.txt"
$sumsFile = Join-Path $DeliverDir 'SHA256SUMS.txt'
$lines = foreach ($a in $artifacts) {
    $h = (Get-FileHash -LiteralPath $a -Algorithm SHA256).Hash.ToLowerInvariant()
    "{0}  {1}" -f $h, (Split-Path -Leaf $a)
}
Set-Content -LiteralPath $sumsFile -Value $lines -Encoding UTF8
Write-Host ("  {0}" -f $sumsFile)

# ============================================================================
#  完成
# ============================================================================
Write-Step "完成"
Get-ChildItem -LiteralPath $DeliverDir -File | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0,-45} {1,12:N0} 字节" -f $_.Name, $_.Length)
}
