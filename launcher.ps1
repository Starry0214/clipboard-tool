# 剪贴板助手启动引导器（框架依赖版运行时检测与自动安装）
# 由"启动剪贴板助手.bat"调用；逻辑放 PowerShell 避免 bat 中文/特殊字符编码问题

$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "ClipboardTool.exe"
$installerUrl = "https://code.starry0214.one/updates/windowsdesktop-runtime-9.0.17-win-x64.exe"
$installer = Join-Path $PSScriptRoot "windowsdesktop-runtime-9.0.17-win-x64.exe"

# 检测 .NET 9 Desktop Runtime（优先注册表，回退 dotnet CLI 输出）
function Test-DotnetRuntime {
    try {
        $key = Get-ItemProperty -Path "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App" -Name Version -ErrorAction Stop
        if ($key.Version -like "9.*") { return $true }
    } catch {
        # 注册表路径不存在（新式安装），走 dotnet CLI
    }
    try {
        $out = & dotnet --list-runtimes 2>$null
        return [bool]($out | Where-Object { $_ -match "WindowsDesktop\.App 9\." })
    } catch {
        return $false
    }
}

if (-not (Test-Path $exe)) {
    Write-Host "未找到 ClipboardTool.exe，请确认与引导器在同一目录。" -ForegroundColor Red
    exit 1
}

if (-not (Test-DotnetRuntime)) {
    Write-Host "未检测到 .NET 9 桌面运行时，需要先安装（约 60MB）。" -ForegroundColor Yellow
    Write-Host "将从更新服务器下载并安装，请稍候…"
    Write-Host ""

    if (-not (Test-Path $installer)) {
        Write-Host "正在下载运行时安装包…"
        try {
            Invoke-WebRequest -Uri $installerUrl -OutFile $installer -UseBasicParsing -TimeoutSec 600
        } catch {
            Write-Host "下载失败：$($_.Exception.Message)" -ForegroundColor Red
            Write-Host "请手动下载安装：$installerUrl"
            Write-Host "安装完成后重新运行本程序即可。"
            exit 1
        }
    }

    Write-Host "正在安装运行时（如弹出 UAC 请允许）…"
    $proc = Start-Process -FilePath $installer -ArgumentList "/install", "/quiet", "/norestart" -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        Write-Host "安装失败（退出码 $($proc.ExitCode)），可能缺少管理员权限。" -ForegroundColor Red
        Write-Host "请右键本程序选择"以管理员身份运行"，或手动安装：$installerUrl"
        exit 1
    }

    if (-not (Test-DotnetRuntime)) {
        Write-Host "安装后仍未检测到运行时，请重启后重试或手动安装。" -ForegroundColor Red
        exit 1
    }
    Write-Host "运行时安装完成。"
    Write-Host ""
}

Start-Process -FilePath $exe
exit 0
