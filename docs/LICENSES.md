# 第三方组件与分发

## GitHub 升级包

Windows x64 升级包含 DropLink 界面、随界面发布的 .NET 运行时、接收脚本和通知工具。它需要已有便携版提供无线后端和嵌入式 Python。升级 ZIP 中的许可证位于 `runtime/licenses/`。

接收脚本基于 WinAWDL 的 MIT 授权实现，保留 WinAWDL 和 airdrop 子目录的版权文本。DropBridge 的许可及组件声明也随包提供。[第三方声明](third-party/)

## 完整无线运行包

| 组件 | 当前依据 |
| --- | --- |
| airbridge_radio.exe | 链接 GPL-2.0 的 devourer，并嵌入 Realtek 固件。上游没有为该固件提供单独的再分发许可。 |
| awdld.exe | 基于 WinAWDL 的 AWDL 实现和 OWL，适用 GPL-3.0-or-later，发布二进制需核对对应源码与补丁。 |
| Python、libusb、lwIP 等 | 各自适用原有条款，完整包需要按实际构建核对版本及声明。 |

完整无线包目前用于本地验证。公开升级包不收录上述无线二进制、固件或 Python 运行时。锁定版本和获取源码的步骤见准备脚本。[构建依赖](../tools/prepare-sources.py) · [上游组件声明](https://github.com/ndpyzwy-0w0/DropBridge/blob/8fbcee72569de2bf3fc8782164996dcc84f9f0c1/NOTICE) · [上游许可矩阵](https://github.com/ndpyzwy-0w0/DropBridge/blob/8fbcee72569de2bf3fc8782164996dcc84f9f0c1/docs/LICENSE_MATRIX.md)
