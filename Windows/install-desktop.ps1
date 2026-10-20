param([Parameter(Mandatory = $true)][string]$ReportPath)

$ErrorActionPreference = 'Stop'

function Assert-PlainPath([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw "必须使用绝对路径：$Path" }
    $full = [IO.Path]::GetFullPath($Path)
    for ($part = $full; $part; $part = [IO.Path]::GetDirectoryName($part)) {
        if (Test-Path -LiteralPath $part) {
            if ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "拒绝通过链接或重解析点安装：$part"
            }
        }
    }
    return $full
}

function Read-Shortcut([string]$Path) {
    $shortcut = $shell.CreateShortcut($Path)
    try {
        return [pscustomobject]@{ path = $Path; targetPath = $shortcut.TargetPath; arguments = $shortcut.Arguments;
            workingDirectory = $shortcut.WorkingDirectory; iconLocation = $shortcut.IconLocation; windowStyle = $shortcut.WindowStyle }
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
}

function Install-Shortcut([string]$Path, [string]$Arguments, [int]$WindowStyle, [string]$BackupPath) {
    if ([IO.Path]::GetFileName($Path) -cne 'DogSC.lnk') { throw '只能安装DogSC.lnk。' }
    $temporary = Assert-PlainPath (Join-Path ([IO.Path]::GetDirectoryName($Path)) ('.DogSC-' + [Guid]::NewGuid().ToString('N') + '.lnk'))
    $published = $false
    function Check-Shortcut([string]$Candidate) {
        $value = Read-Shortcut (Assert-PlainPath $Candidate)
        if ($value.targetPath -ine $executable -or $value.arguments -cne $Arguments -or $value.workingDirectory -ine $publishDirectory -or
            $value.iconLocation -ine ($executable + ',0') -or $value.windowStyle -ne $WindowStyle) {
            throw "快捷方式回读与预期不一致：$Candidate"
        }
        return $value
    }
    try {
        $shortcut = $shell.CreateShortcut($temporary)
        try {
            $shortcut.TargetPath = $executable
            $shortcut.Arguments = $Arguments
            $shortcut.WorkingDirectory = $publishDirectory
            $shortcut.IconLocation = $executable + ',0'
            $shortcut.WindowStyle = $WindowStyle
            $shortcut.Save()
        }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
        [void](Check-Shortcut $temporary)
        [void](Assert-PlainPath $Path)
        [IO.File]::Move($temporary, $Path, $true)
        $published = $true
        return Check-Shortcut $Path
    }
    catch {
        if ($published) {
            if ([IO.File]::Exists($BackupPath)) { Copy-Item -LiteralPath $BackupPath -Destination $Path -Force }
            else { Remove-Item -LiteralPath $Path -Force }
        }
        throw
    }
    finally { if ([IO.File]::Exists($temporary)) { Remove-Item -LiteralPath $temporary -Force } }
}

$shell = $null
$runKey = $null
$runRemoved = $false
$result = [ordered]@{ success = $false; loggedOnSessionTested = $false; entry = 'current-user Startup/DogSC.lnk'; oldRunRemoved = $false }
try {
    $projectDirectory = Assert-PlainPath $PSScriptRoot
    $artifactDirectory = Assert-PlainPath (Join-Path $projectDirectory 'artifacts')
    $ReportPath = Assert-PlainPath $ReportPath
    if (-not $ReportPath.StartsWith($artifactDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetExtension($ReportPath) -ine '.json') { throw '报告必须是Windows/artifacts内的JSON文件。' }
    $publishDirectory = Assert-PlainPath (Join-Path $artifactDirectory 'win-x64')
    $executable = Assert-PlainPath (Join-Path $publishDirectory 'DogSC.Windows.exe')
    if (-not [IO.File]::Exists($executable)) { throw "没有发布程序：$executable" }
    $desktopFolder = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
    $startupFolder = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
    if (-not $desktopFolder -or -not $startupFolder) { throw '无法确定当前用户的桌面或启动文件夹。' }
    $desktopPath = Assert-PlainPath (Join-Path $desktopFolder 'DogSC.lnk')
    $startupPath = Assert-PlainPath (Join-Path $startupFolder 'DogSC.lnk')
    if (-not [IO.Directory]::Exists($desktopFolder) -or -not [IO.Directory]::Exists($startupFolder) -or
        [IO.Directory]::Exists($desktopPath) -or [IO.Directory]::Exists($startupPath)) { throw '快捷方式目标目录不存在或被同名文件夹占用。' }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($ReportPath))
    $backupDirectory = Assert-PlainPath (Join-Path ([IO.Path]::GetDirectoryName($ReportPath)) ('startup-before-' + [Guid]::NewGuid().ToString('N')))
    [void][IO.Directory]::CreateDirectory($backupDirectory)
    $result.backupDirectory = $backupDirectory
    if ([IO.File]::Exists($ReportPath)) { Copy-Item -LiteralPath $ReportPath -Destination (Join-Path $backupDirectory 'previous-report.json') }
    $shell = New-Object -ComObject WScript.Shell
    foreach ($item in @(@{ name = 'desktop'; path = $desktopPath }, @{ name = 'startup'; path = $startupPath })) {
        $before = if ([IO.File]::Exists($item.path)) {
            Copy-Item -LiteralPath $item.path -Destination (Join-Path $backupDirectory ($item.name + '.lnk'))
            Read-Shortcut $item.path
        } else { $null }
        $result[$item.name + 'Before'] = $before
    }
    $runSubkey = 'Software\Microsoft\Windows\CurrentVersion\Run'
    $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runSubkey, $false)
    $hadRun = $null -ne $runKey -and $runKey.GetValueNames() -contains 'DogSC'
    $runBefore = if ($hadRun) {
        @{ exists = $true; value = $runKey.GetValue('DogSC', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames); kind = $runKey.GetValueKind('DogSC').ToString() }
    } else { @{ exists = $false; value = $null; kind = $null } }
    $result.runBefore = $runBefore
    if ($runKey) { $runKey.Dispose(); $runKey = $null }
    [IO.File]::WriteAllText((Join-Path $backupDirectory 'run-before.json'), ($runBefore | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    $result.startup = Install-Shortcut $startupPath '--background' 7 (Join-Path $backupDirectory 'startup.lnk')
    $result.desktop = Install-Shortcut $desktopPath '' 1 (Join-Path $backupDirectory 'desktop.lnk')
    # The old Run entry remains until both published shortcuts have been read back successfully.
    if ($hadRun) {
        $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runSubkey, $true)
        if ($null -eq $runKey -or $runKey.GetValue('DogSC', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne $runBefore.value -or
            $runKey.GetValueKind('DogSC').ToString() -cne $runBefore.kind) { throw '旧DogSC自启项在安装过程中发生变化，已保留。' }
        $runKey.DeleteValue('DogSC', $true)
        $runRemoved = $true
        if ($runKey.GetValueNames() -contains 'DogSC') { throw '旧DogSC自启项删除后的回读失败。' }
    }
    $result.oldRunRemoved = $runRemoved
    $result.success = $true
    $result.completedAt = [DateTimeOffset]::Now.ToString('o')
    [IO.File]::WriteAllText($ReportPath, ($result | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
}
catch {
    $result.success = $false
    $result.error = $_.Exception.Message
    if ($runRemoved -and $runBefore.exists) {
        try {
            $runKey.SetValue('DogSC', $runBefore.value, [Microsoft.Win32.RegistryValueKind]$runBefore.kind)
            $result.oldRunRestored = $true
            $result.oldRunRemoved = $false
        }
        catch { $result.oldRunRecoveryError = $_.Exception.Message }
    }
    if ($result.backupDirectory) {
        try {
            $result.completedAt = [DateTimeOffset]::Now.ToString('o')
            [IO.File]::WriteAllText($ReportPath, ($result | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
        }
        catch { Write-Warning ('安装失败，报告无法写入；原值备份：' + $result.backupDirectory) }
    }
    throw
}
finally {
    if ($runKey) { $runKey.Dispose() }
    if ($shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}

$result | ConvertTo-Json -Depth 6
