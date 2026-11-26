# Windows 便携包

下载包的顶层包含 `DropLink.exe`、`使用说明.txt` 和 `runtime/`。程序从自身位置找到运行组件。首次打开时创建本机配置和密钥。

## 构建与核验

使用 Windows、.NET 10 SDK 和已组装的后端目录。下面的 `D:\DropLink-stage` 是构建暂存目录，包含 `backend`、`licenses` 和驱动工具。

```powershell
powershell -File tools/publish.ps1 -Target D:\DropLink-stage\app
powershell -File tools/package-portable.ps1 -Source D:\DropLink-stage -OutFile D:\DropLink-portable.zip
Expand-Archive D:\DropLink-portable.zip D:\DropLink-check
python tools/check-portable.py D:\DropLink-check\DropLink
D:\DropLink-check\DropLink\DropLink.exe --diagnose D:\DropLink-check\report.txt
```

确认报告中的六个后端文件均为 OK。真实 iPhone 投送、驱动切换及回退需另外做真机验证。

打包脚本从模板取默认设置和硬件策略，按清单收录发布组件。个人设置、历史、已收文件、日志、私钥和本机驱动备份只属于使用者。

[用户说明](使用说明.txt) · [驱动恢复](../../docs/DRIVER_RECOVERY.md) · [许可说明](../../docs/LICENSES.md)

## 生成公开升级包

在仓库根目录执行下列命令。第一个参数是带有新版 DropLink.exe 的紧凑便携目录，最后两个参数指向本次 publish 实际使用的 .NET 运行时包目录。版本可在 src/DropLink/obj/project.assets.json 的 downloadDependencies 中核对。

```powershell
$packs = "$env:USERPROFILE/.nuget/packages"
$runtimeVersion = '10.0.15'
python tools/package-update.py D:/DropLink-check/DropLink D:/DropLink-Windows-x64-update.zip "$packs/microsoft.netcore.app.runtime.win-x64/$runtimeVersion" "$packs/microsoft.windowsdesktop.app.runtime.win-x64/$runtimeVersion"
```

升级 ZIP 从源码模板收录脚本和默认设置，从便携目录收录程序和图标。验收时将 ZIP 覆盖到一份已有便携版的副本，执行上面的目录检查和自检，再核对原配置、历史和已收文件。
