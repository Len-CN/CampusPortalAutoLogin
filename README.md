# 校园网自动登录

面向 Windows 10/11 x64 的校园网托盘应用，支持连通性检测、校园门户认证、网络变化触发、定时登录和当前用户登录时启动。

本软件面向**南京理工大学紫金学院校园网**，由个人维护，非学校官方软件。

当前版本：**1.0.5**。适配南京理工大学紫金学院已验证的 ePortal 认证流程，支持校园网、中国移动、中国联通和中国电信的门户服务选择；学校调整网络门户后，兼容性可能需要更新验证。

- 密码使用 Windows DPAPI `CurrentUser` 加密保存在本机。
- 验证码或运营商附加凭据需要在浏览器中人工完成。
- 日志脱敏，只保留最近一轮探测与登录，下一轮覆盖。

## 构建

需要 .NET SDK 8 或更高版本，以及 Inno Setup 6。构建脚本只使用官方 NuGet 源，并生成包含 .NET 运行时的 `win-x64` 自包含安装包：

```powershell
.\scripts\build.ps1
```

如未自动找到 `ISCC.exe`：

```powershell
.\scripts\build.ps1 -IsccPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

脚本依次执行 `dotnet restore`、解决方案 Release 构建、项目自带的 custom test harness、`dotnet publish` 和 Inno Setup 编译。发布目录为 `artifacts\publish\win-x64`，安装程序输出到 `artifacts\installer`。任何命令失败时脚本返回非零。

仅构建和测试无需安装 Inno Setup：

```powershell
dotnet restore CampusPortalAutoLogin.sln
dotnet build CampusPortalAutoLogin.sln -c Release --no-restore
dotnet run --project tests/CampusPortalAutoLogin.Tests -c Release --no-build
```

离线测试使用合成门户页面和模拟 HTTP 响应，不需要真实校园网账号。仓库不包含本机配置、真实门户页面、运行日志、设计参考包或构建产物。

## 文档

- [使用说明](docs/使用说明.md)
- [配置说明](docs/配置说明.md)
- [隐私说明](docs/隐私说明.md)
- [已知限制](docs/已知限制.md)
- [协议说明](docs/协议说明.md)

发布前须在真实校园网完成手工验收；验证码只能在原门户浏览器页面人工输入。

## 许可与第三方内容

Copyright (c) 2026 Len-CN。

本项目的原创代码、文档、图标及其他原创素材统一采用 **GNU General Public License v3.0（GPL-3.0-only，仅第 3 版）**，完整条款见 [LICENSE](LICENSE)。允许依照许可证使用、修改、分发和商业使用；分发修改版或其他受 GPL 覆盖的作品时，须遵守相应的源码提供、版权声明保留及相同许可要求。本项目不提供任何担保。

第三方文件和依赖保留各自的许可及署名要求，详见 [第三方说明](THIRD_PARTY_NOTICES.md)。
