param(
    [string] $DestinationPath = "",
    [string] $GitHubRepoUrl = "https://github.com/ThalesNotTychus/Hyperion.git",
    [string] $GitAuthorName = "ThalesNotTychus",
    [string] $GitAuthorEmail = "ThalesNotTychus@users.noreply.github.com"
)

$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
    $DestinationPath = [IO.Path]::GetFullPath((Join-Path $sourceRoot '..\Hyperion-opensource'))
}

if (Test-Path -LiteralPath $DestinationPath) {
    throw "目标目录已存在，请先确认或更换路径: $DestinationPath"
}

Write-Host "==> 正在导出零历史敏感记录、零个人路径残留的干净开源仓库..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $DestinationPath | Out-Null

function Copy-CleanTree([string] $RelSubDir) {
    $srcDir = Join-Path $sourceRoot $RelSubDir
    if (!(Test-Path -LiteralPath $srcDir)) { return }
    $prefixLen = $sourceRoot.TrimEnd('\', '/').Length + 1
    Get-ChildItem -LiteralPath $srcDir -Recurse -File | ForEach-Object {
        $relFromRoot = $_.FullName.Substring($prefixLen)
        if ($relFromRoot -match '(^|[\\/])(bin|obj|\.vs|packages|data|dist|artifacts)([\\/]|$)') { return }
        if ($_.Name -match '\.(user|suo|pdb)$' -or $relFromRoot -ieq 'docs\LAUNCH_PLAYBOOK.md') { return }
        $dstFile = Join-Path $DestinationPath $relFromRoot
        $dstParent = Split-Path -Parent $dstFile
        if (!(Test-Path -LiteralPath $dstParent)) {
            New-Item -ItemType Directory -Force -Path $dstParent | Out-Null
        }
        Copy-Item -LiteralPath $_.FullName -Destination $dstFile -Force
    }
}

foreach ($dir in @('.github', 'src', 'tests', 'scripts', 'docs')) {
    Copy-CleanTree $dir
}

foreach ($rootFile in @('.gitignore', '.gitattributes', '.editorconfig', 'Hyperion.sln', 'Directory.Build.props', 'CodexMultiLauncher.root', 'LICENSE', 'README.md', 'README.zh-CN.md')) {
    $srcFile = Join-Path $sourceRoot $rootFile
    if (Test-Path -LiteralPath $srcFile -PathType Leaf) {
        Copy-Item -LiteralPath $srcFile -Destination (Join-Path $DestinationPath $rootFile) -Force
    }
}

git -C $DestinationPath init -b main | Out-Null
git -C $DestinationPath config user.name $GitAuthorName
git -C $DestinationPath config user.email $GitAuthorEmail
git -C $DestinationPath add -A
git -C $DestinationPath commit -m "feat: initial open-source release of Hyperion" | Out-Null

if (![string]::IsNullOrWhiteSpace($GitHubRepoUrl)) {
    git -C $DestinationPath remote add origin $GitHubRepoUrl
    Write-Host "==> 已绑定 GitHub 远程地址: $GitHubRepoUrl" -ForegroundColor Green
    Write-Host "==> 提交身份已隔离为: $GitAuthorName <$GitAuthorEmail>" -ForegroundColor Green
}
Write-Host "==> 干净开源仓库已生成至: $DestinationPath" -ForegroundColor Green
