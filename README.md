# 光盘打卡 · Windows x64

一个使用本人已授权会话的 Windows 桌面工具。以 C# / .NET 10 / Windows Forms 实现，MIT 开源。

## 下载与使用

从 [Releases](https://github.com/KiyaKike/clearplate-desktop/releases) 下载 `ClearPlate-Windows-x64-v1.0.0.zip`，解压后运行 `ClearPlate.exe`。发布包包含运行时，无需另装 .NET。目标为 Windows x64；建议 Windows 11。

首次使用输入从本人正常微信登录请求取得的 `X-Token`，点击“核验并保存”，确认返回的账号属于本人。填写组织 ID 和当天真实位置（纬度,经度），再启用自动计划。组织与位置没有预填任何人的真实数据。导入 token 不会上传照片或打卡。

- 每天北京时间 **08:00、12:00、18:00**，在开始后的 5 分钟内检查，结束时刻不包含在内。程序每 15 秒检查一次。
- 自动计划使用程序内置的 `plate.png` 原图；手动可选择其他 PNG/JPG。日常请使用当天真实光盘照片，并遵守平台要求。内置图来自项目发起人，用于该工具的照片提交功能。
- 每餐一次，打卡至少间隔 **2 小时**。上传前读取平台最近成功时间；间隔不足或当天剩余计数为 0 时不上传。最终结果仍由平台判定。
- 查询账号阶段临时断网不消耗提交机会，可在原窗口内重试。错过窗口不补交。开始上传后失败或结果未知均不会自动重复提交。
- 打卡接口返回 `10007` 且累计成功次数增加，才记为成功；接口只返回“成功”而账号记录未增加时标为结果未确认。
- 每个时段的阶段、时间、结果和错误保存到本地，重启后防重复记录仍生效。
- 保持程序运行、电脑唤醒且联网。最小化会进入托盘；关闭窗口或托盘“退出”会停止计划。本版没有开机启动、系统服务或唤醒电脑功能。

## 会话和本地数据

token 用 Windows **DPAPI / CurrentUser** 加密保存到 `%LOCALAPPDATA%\ClearPlateDesktop\state.json`。该文件还保存本地打卡记录和位置。不要公开、提交到 Git、打包分享或删除这个文件来绕过重复保护。加密数据绑定 Windows 用户环境，换电脑请重新导入自己的会话。

首次导入只读核验并绑定账号。后续导入或平台返回不同 token 时必须再次验证同一账号；错误账号和无效候选不会替换原会话。业务码 `10003` 或 HTTP `401` 会暂停自动提交。

**没有实现脱离微信的过期续期。** 平台未提供已验证的独立刷新流程，也不返回可用于倒计时的 token 到期时间。登录失效后需正常微信登录，再导入本人新 token。小程序 48 小时客户端缓存时长不等于服务器有效期。

请求只发往已核对的打卡 API 和图片上传域名，不采集微信消息、不修改微信缓存、不常驻抓包。上传域名不会收到登录 token。未包含项目发起人的 token、账号资料、真实位置、请求记录或 Mac 本地路径。

## 从源码构建

安装 .NET 10 SDK；在仓库根目录执行：

```powershell
dotnet run --project tests/ClearPlate.Tests/ClearPlate.Tests.csproj -c Release
dotnet publish src/ClearPlate.Windows/ClearPlate.Windows.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64
artifacts\win-x64\ClearPlate.exe --smoke-test
```

测试使用模拟 HTTP，不连接真实账号、不上传或打卡。1.0.0 已使用 .NET 10 SDK 在 macOS 交叉编译为 Windows x64，自包含发布成功，46 项核心检查通过。尚未在 Windows 机器运行界面和真实打卡；可在 Windows 执行上述 `--smoke-test` 检查窗口与 DPAPI。Windows smoke 检查会创建窗口并验证内置照片和 DPAPI 加密读写，使用临时假会话。`docs/windows-ci.yml.example` 提供 Windows 自动构建、窗口启动和 DPAPI 检查的工作流示例。当前发布凭据没有 GitHub `workflow` 权限，因此示例尚未启用；有相应权限的维护者可将其放到 `.github/workflows/windows.yml`。

依赖下载若遇到 NuGet 网络问题，可为 `dotnet publish` 增加微软公开包源参数：

```powershell
--source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json
```

## 结构

| 目录 | 内容 |
| --- | --- |
| `src/ClearPlate.Core` | 调度、接口、会话验证、重复保护和结果核验 |
| `src/ClearPlate.Windows` | Windows Forms 界面、托盘、DPAPI、本地保存和内置照片 |
| `tests/ClearPlate.Tests` | 无外部测试框架依赖的模拟请求检查 |
| `docs/windows-ci.yml.example` | 需要 workflow 权限才能启用的 Windows 构建示例 |

这是非官方客户端，平台接口变化可能使功能失效。开源许可不授予任何平台账号或 API 权限。

规则参考：[小程序间隔提示](https://wximg.guangpanxingdong.com/a-uni/rr3xs/rr3hs_hour_limit.png)。技术参考：[.NET 单文件发布](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)、[Windows 数据保护](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata)。
