[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [string]$Version = '1.0.5',
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'CampusPortalAutoLogin.sln'
$testProject = Join-Path $repoRoot 'tests\CampusPortalAutoLogin.Tests\CampusPortalAutoLogin.Tests.csproj'
$appProject = Join-Path $repoRoot 'src\CampusPortalAutoLogin\CampusPortalAutoLogin.csproj'
$expectedPublishDir = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\publish\win-x64'))
$publishDir = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\publish\$Runtime"))
$installerDir = Join-Path $repoRoot 'artifacts\installer'
$installerScript = Join-Path $repoRoot 'installer\CampusPortalAutoLogin.iss'
$nugetSource = 'https://api.nuget.org/v3/index.json'

function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList
    )

    Write-Host "> $FilePath $($ArgumentList -join ' ')"
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "命令执行失败，退出码 $LASTEXITCODE：$FilePath"
    }
}

function Resolve-Iscc {
    param([string]$RequestedPath)

    $candidates = @(
        $RequestedPath,
        (Join-Path $repoRoot '.tools\InnoSetup\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw '未找到 Inno Setup 6 的 ISCC.exe。请从 https://jrsoftware.org/isdl.php 安装官方版本，或通过 -IsccPath 指定路径。'
}

Push-Location $repoRoot
try {
    Invoke-Checked dotnet @('restore', $solution, '--runtime', $Runtime, '--source', $nugetSource)
    Invoke-Checked dotnet @('build', $solution, '--configuration', $Configuration, '--no-restore')
    Invoke-Checked dotnet @('run', '--project', $testProject, '--configuration', $Configuration, '--no-build')

    $repoRootWithSeparator = [IO.Path]::GetFullPath($repoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (($publishDir -ine $expectedPublishDir) -or
        (-not $publishDir.StartsWith($repoRootWithSeparator, [StringComparison]::OrdinalIgnoreCase))) {
        throw "拒绝清理非预期发布目录：$publishDir"
    }

    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }

    Invoke-Checked dotnet @(
        'publish', $appProject,
        '--configuration', $Configuration,
        '--runtime', $Runtime,
        '--self-contained', 'true',
        '--output', $publishDir,
        '--no-restore',
        "/p:Version=$Version"
    )

    $resolvedIscc = Resolve-Iscc $IsccPath
    New-Item -ItemType Directory -Force -Path $installerDir | Out-Null
    Invoke-Checked $resolvedIscc @(
        "/DMyAppVersion=$Version",
        "/DPublishDir=$publishDir",
        "/DOutputDir=$installerDir",
        $installerScript
    )

    Write-Host "构建完成：$installerDir"
}
finally {
    Pop-Location
}
