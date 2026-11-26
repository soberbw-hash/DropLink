# 补丁说明

以下补丁均由 `tools/prepare-sources.py` 应用到锁定的 WinAWDL 源码：

- `engine-default-directory.patch`：指定接收目录时不再提前验证默认目录。
- `atomic-file-publish.patch`：实验性同卷暂存和硬链接发布，Linux 离线测试通过；尚无 Windows/NTFS 或崩溃、并发验证，多文件之间不构成原子事务。
- `engine-proxy-mode.patch`：连接本机 USB 数据代理并让接收端通过 `awdl_netd` 广播；已在本机真实链路中联调。便携版实际运行的 `engine.py` 另含自动接收、Toast、Live Photo 静态副本和设置文件支持，见 `tools/portable/backend/airdrop/engine.py`。

补丁修改 MIT 许可的 WinAWDL AirDrop 源码。原始版权声明见上游 `airdrop/LICENSE`，锁定版本见[依赖准备脚本](../tools/prepare-sources.py)，许可见[第三方说明](../docs/LICENSES.md)。
# Windows 拒绝上传响应

`windows-reject-response.patch` 只修改上传尚未读取请求体即被拒绝的两条路径：先发送拒绝响应，再在限定时间内读取不超过 64 KiB 的已声明请求体，以避免 Windows 上未读数据导致客户端收不到响应；`Expect: 100-continue`、无效及超限长度不等待。它不接受或保存被拒绝的上传。
