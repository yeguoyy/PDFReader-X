# PDFReaderX 打包脚本
# 用法:
#   .\scripts\publish.ps1 pack   # 免安装目录包 + zip（dist\PDFReaderX-win-x64-<版本>.zip）
#   .\scripts\publish.ps1 dist   # 在 pack 基础上生成安装包（需要安装 Inno Setup 6）
param(
    [ValidateSet('pack', 'dist')]
    [string] $Target = 'pack'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# 版本号从 App 工程读取，输出目录与压缩包均带版本号，旧版本产物不会被覆盖
$csprojXml = [xml](Get-Content -Raw (Join-Path $root 'src/PDFReaderX.App/PDFReaderX.App.csproj'))
$version = $csprojXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1 -ExpandProperty Version
if (-not $version) { throw '无法从 PDFReaderX.App.csproj 读取 Version' }

$distDir = Join-Path $root 'dist'
$outDir = Join-Path (Join-Path $distDir 'win-x64') $version
$zip = Join-Path $distDir "PDFReaderX-win-x64-$version.zip"

Write-Host '[1/3] dotnet publish (Release / win-x64 / 单文件自包含) ...'
dotnet publish (Join-Path $root 'src/PDFReaderX.App/PDFReaderX.App.csproj') -c Release -r win-x64 -o $outDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }

Write-Host '清理发布目录中的 PDB ...'
Get-ChildItem -LiteralPath $outDir -Filter '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host "[2/3] 生成免安装 zip (v$version) ..."
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
tar -a -c -f $zip -C $outDir 'PDFReaderX.App.exe'
if ($LASTEXITCODE -ne 0) { throw 'zip 打包失败' }

if ($Target -eq 'dist') {
    Write-Host '[3/3] 生成 Inno Setup 安装包 ...'
    $iscc = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw '未找到 Inno Setup 6，请先安装: https://jrsoftware.org/isinfo.php' }
    & $iscc (Join-Path $root 'scripts/installer.iss')
    if ($LASTEXITCODE -ne 0) { throw 'ISCC 失败' }
} else {
    Write-Host '[3/3] 跳过安装包（要生成安装包请运行: .\scripts\publish.ps1 dist）'
}

Write-Host ''
Write-Host "完成！版本: v$version"
Write-Host "免安装包: $zip"
if ($Target -eq 'dist') { Write-Host "安装包: $distDir\installer\PDFReaderX-Setup-$version.exe" }
