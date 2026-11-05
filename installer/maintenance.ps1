[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Shutdown', 'CleanupConfig', 'CleanupLogs', 'CleanupTasks')]
    [string]$Mode,
    [Parameter(Mandatory)]
    [string]$AppPath,
    [string]$DataPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-NormalizedPath {
    param([Parameter(Mandatory)][string]$Path)
    return [IO.Path]::GetFullPath($Path.Trim([char]34)).TrimEnd([IO.Path]::DirectorySeparatorChar)
}

function Assert-NotReparsePoint {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "拒绝处理重解析点：$Path"
    }
}

function Get-AppProcesses {
    param([Parameter(Mandatory)][string]$ExpectedPath)
    foreach ($process in Get-Process -ErrorAction SilentlyContinue) {
        try {
            if ((Get-NormalizedPath $process.MainModule.FileName) -ieq $ExpectedPath) {
                $process
            }
        }
        catch {
            continue
        }
    }
}

function Stop-WithRestartManager {
    param([Parameter(Mandatory)][string]$ExpectedPath)

    if (Test-Path -LiteralPath $ExpectedPath -PathType Leaf) {
        $request = Start-Process -FilePath $ExpectedPath -ArgumentList '--shutdown' -WindowStyle Hidden -PassThru
        if (-not $request.WaitForExit(10000)) {
            throw '退出请求进程在 10 秒内未返回。'
        }
    }

    if (-not ('CampusPortalRestartManager' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class CampusPortalRestartManager
{
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint handle, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint handle, uint fileCount,
        string[] fileNames, uint applicationCount, IntPtr applications,
        uint serviceCount, string[] serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmShutdown(uint handle, uint actionFlags, IntPtr callback);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint handle);

    public static void ShutdownFileUsers(string path)
    {
        uint handle;
        var key = new StringBuilder(33);
        int result = RmStartSession(out handle, 0, key);
        if (result != 0) throw new InvalidOperationException("RmStartSession: " + result);
        try
        {
            result = RmRegisterResources(handle, 1, new[] { path }, 0,
                IntPtr.Zero, 0, null);
            if (result != 0) throw new InvalidOperationException("RmRegisterResources: " + result);
            result = RmShutdown(handle, 0, IntPtr.Zero);
            if (result != 0) throw new InvalidOperationException("RmShutdown: " + result);
        }
        finally
        {
            RmEndSession(handle);
        }
    }
}
'@
    }

    [CampusPortalRestartManager]::ShutdownFileUsers($ExpectedPath)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $remaining = @(Get-AppProcesses -ExpectedPath $ExpectedPath)
        if ($remaining.Count -eq 0) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    $ids = ($remaining.Id -join ', ')
    throw "安装目录中的驻留进程未在 15 秒内退出（PID：$ids）。"
}

function Get-ValidatedDataPath {
    if ([string]::IsNullOrWhiteSpace($DataPath)) {
        throw '未提供用户数据目录。'
    }
    $expected = Get-NormalizedPath (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CampusPortalAutoLogin')
    $actual = Get-NormalizedPath $DataPath
    if ($actual -ine $expected) {
        throw "拒绝处理非当前卸载账户的数据目录：$actual"
    }
    Assert-NotReparsePoint $actual
    return $actual
}

function Remove-Config {
    $root = Get-ValidatedDataPath
    foreach ($name in @('config.json', 'secrets.dat')) {
        $path = Join-Path $root $name
        if (Test-Path -LiteralPath $path) {
            Assert-NotReparsePoint $path
            Remove-Item -LiteralPath $path -Force -ErrorAction Stop
        }
    }
}

function Remove-Logs {
    $root = Get-ValidatedDataPath
    $logs = Get-NormalizedPath (Join-Path $root 'logs')
    if ($logs -ine (Get-NormalizedPath (Join-Path $root 'logs'))) {
        throw "日志目录校验失败：$logs"
    }
    if (Test-Path -LiteralPath $logs) {
        Assert-NotReparsePoint $logs
        foreach ($child in Get-ChildItem -LiteralPath $logs -Recurse -Force -ErrorAction Stop) {
            Assert-NotReparsePoint $child.FullName
        }
        Remove-Item -LiteralPath $logs -Recurse -Force -ErrorAction Stop
    }
}

function Remove-Tasks {
    $expectedApp = Get-NormalizedPath $AppPath
    $tasks = Get-ScheduledTask -ErrorAction Stop | Where-Object {
        $_.TaskPath -eq '\' -and
        ($_.TaskName -eq 'CampusPortalAutoLogin' -or $_.TaskName -like 'CampusPortalAutoLogin-*')
    }
    foreach ($task in $tasks) {
        $matches = $false
        foreach ($action in $task.Actions) {
            try {
                $actionPath = Get-NormalizedPath $action.Execute.Trim([char]34)
                if ($actionPath -ieq $expectedApp) { $matches = $true }
            }
            catch {
                continue
            }
        }
        if ($matches) {
            Unregister-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -Confirm:$false -ErrorAction Stop
        }
    }
}

try {
    $normalizedApp = Get-NormalizedPath $AppPath
    switch ($Mode) {
        'Shutdown' { Stop-WithRestartManager $normalizedApp }
        'CleanupConfig' { Remove-Config }
        'CleanupLogs' { Remove-Logs }
        'CleanupTasks' { Remove-Tasks }
    }
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 20
}
