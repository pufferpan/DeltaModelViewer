param([string]$ModelZip, [string]$TextureZip, [string]$DataRoot)
# 安装程序数据解压：模型/贴图 → <安装目录>\Data\{Models,Textures}
# 仅当对应数据目录缺失时解压（升级/重装且已有数据时自动跳过）
$ErrorActionPreference = 'Stop'
if (-not $DataRoot) {
    $DataRoot = Join-Path $env:LOCALAPPDATA 'DeltaModelViewer\Data'
}
New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null
if (-not (Test-Path (Join-Path $DataRoot 'Models'))) {
    Expand-Archive -LiteralPath $ModelZip -DestinationPath (Join-Path $DataRoot 'Models') -Force
}
if (-not (Test-Path (Join-Path $DataRoot 'Textures'))) {
    Expand-Archive -LiteralPath $TextureZip -DestinationPath (Join-Path $DataRoot 'Textures') -Force
}
Write-Host '数据解压完成'
