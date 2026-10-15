# Mac 版

下载 `ClearPlate-macOS-universal-v1.2.4.zip`，解压后将 `光盘打卡.app` 放在应用程序目录。支持 Apple Silicon / Intel，要求 macOS 15 或更高；采集组件自带 .NET 10 运行时，不需要额外安装。

该包使用本地 ad-hoc 签名，未使用 Apple Developer ID 签名或公证。若 macOS 阻止首次打开，请先核对发布页的 SHA256，再在“系统设置 → 隐私与安全性”允许该应用打开；无需关闭全局 Gatekeeper。

请将整个 `光盘打卡.app` 放入 `/Applications`（“应用程序”），再从 Finder 双击应用启动，系统会注册应用。不要只运行 `Contents/MacOS/ClearPlate` 中的可执行文件。安装到应用程序目录不会更改已有的会话和打卡记录。源码中的 v1.2.5 已加入内置多分辨率图标；生成脚本为 `scripts/create-mac-icon.swift`，构建时自动生成 ICNS 并包含到签名应用包中。

主界面仅保留“立即打卡”“连接微信”“更多”。“立即打卡”使用默认照片；“选择照片打卡”、只读账号检查、打开小程序和手动导入均收在“更多”中。读取本机会话需要钥匙串授权时暂停计划，不消耗时段；授权后可在“更多 → 检查账号状态”恢复原配置。

## 自动获取 token

1. 点击“连接微信”，先只读检查已保存的登录；仍有效时直接显示已连接，不设置代理或证书。没有会话或平台明确返回登录失效时，才显示采集说明，选择“同意并开始”后继续；默认选项是取消。
2. macOS 可能提示授权网络设置以及用户钥匙串证书信任，请按系统提示操作。
3. 在已正常登录的 Mac 微信中关闭、重新打开光盘小程序，进入首页或“我的”；无需额外打卡。
4. 成功捕获后先恢复代理、移除临时证书，再单独查询平台核验账号并保存。首次使用还需确认平台返回的昵称属于本人。后续只允许同一账号。

如确需重新获取微信当前登录，可在“更多 → 重新获取微信登录…”强制启动采集说明。网络故障、HTTP 500 或账号不一致不会自动启动采集。单独“打开光盘小程序”不启动采集。采集最多 3 分钟，也可随时点击“停止连接”。采集期间暂停提交计划。过期后仍需正常微信登录；不支持脱离微信自动重新登录。

仅解密 `clearplate-api.guangpanxingdong.com`，其他 HTTPS 原样加密转发，不保存原始请求或流量日志。监听地址限本机，证书具有关键 DNS 名称约束，用户信任仅用于 SSL，域名范围由证书的关键 DNS 名称约束限制；不使用 Chromium 会忽略的系统主机限定信任规则；根证书约 6 分钟有效，服务证书约 5 分钟有效，私钥仅在采集组件内存中。上游连接仍正常验证平台 HTTPS 证书。

临时改动仅涉及当前主要活动网络服务的 HTTP/HTTPS 代理、自动代理开关和绕过列表，不改变 DNS、VPN 或其他服务。当前 SOCKS 或认证代理启用时拒绝启动。代理恢复记录只含原设置和公钥证书，权限为 600。恢复每个字段前核对是否仍是工具设置，保留用户采集期间修改的设置。

正常结束、3 分钟超时、关闭窗口、退出时恢复；在首次修改之前启动独立恢复进程，父进程退出或 210 秒后尝试恢复，下次启动也检查恢复记录。异常退出时恢复仍可能需要系统授权，不能保证无交互恢复。恢复失败会保留记录并提示，下次启动如果仍恢复失败则不启动计划。请重新打开工具并允许系统恢复授权；如系统持续拒绝授权，请在网络设置恢复原代理并在钥匙串移除名称以 `ClearPlate temporary login capture` 开头的本次短效证书，勿删除其他证书。

**试验功能**：v1.2.3 的真实 Mac 微信采集已由使用者验证成功，界面确认只读核验保存并移除临时代理和证书。v1.2.4 增加已保存登录的优先复用；模拟账号及隔离 TLS 测试不修改系统设置。其他微信版本可能不使用系统代理或拒绝临时证书。Intel 版本已交叉编译，未在 Intel 实机验证。

## 数据和旧版升级

本机数据位于 `~/Library/Application Support/ClearPlateDesktop/`：

- `session.json`：AES-GCM 加密的会话，密钥单独保存在用户登录钥匙串。更换 Mac 或钥匙串后需重新获取自己的会话。更新 ad-hoc 签名的应用后，系统可能要求再次允许本工具读取该密钥。不要删除钥匙串中的 `ClearPlate local session encryption` 项。
- `settings.json`：计划、位置、组织和防重复打卡记录，权限为 600。不要公开这些文件或删除记录来重复提交。
- `capture-recovery.plist`：临时采集恢复记录，正常恢复后删除。

旧版明文会话格式可兼容读取，下一次成功保存时加密。迁移旧版时请先退出旧程序，使用以下命令将旧的 `session.json`、`settings.json` 一起导入，直接加密会话并限制权限；目标已有数据时拒绝覆盖：

```sh
"/Applications/光盘打卡.app/Contents/MacOS/ClearPlate" --import-legacy-folder "/path/to/old-data-folder"
```

也可一起复制到上述目录，并限制目录权限为 700、文件权限为 600。切勿只迁移 token 而丢弃时段防重复记录。公开安装包从不包含个人会话、账号、真实位置或本机路径。

Mac 版继续北京时间 08:00 / 12:00 / 18:00，每次 5 分钟执行窗口，上传前检查平台剩余次数和距上次成功至少 2 小时。查询账号时暂时断网可在窗口内重试；上传或提交后未知结果不重复；错过窗口不补交。新版首次安装默认关闭自动计划，配置本人位置、组织并核验账号后再启用。升级保留原设置。关闭最后一个窗口即退出，计划停止；保持应用运行、电脑唤醒且联网。

源码 v1.2.6 修复上传前失败误占用时段的问题：准备照片、读取本机会话或查询账号失败且确定尚未开始上传时，不再锁住手动重试。升级时仅修复旧记录中“准备照片 / 查询账号”阶段的明确终止失败，保留原错误和时间；不会自动补交这些记录。选择对应时段后点击“立即打卡”可手动重试，仍需通过平台次数及两小时间隔检查。已经开始上传、提交结果不明、已成功以及缺少明确阶段的记录继续保留防重复保护。

## 构建和验证

需要 macOS 命令行开发工具、Python 3 和 .NET 10 SDK：

```sh
python3 tests/mac/run.py
dotnet run --project tests/ClearPlate.Tests/ClearPlate.Tests.csproj -c Release
bash scripts/build-mac.sh
python3 tests/mac/helper_tls.py artifacts/mac-arm64-helper/ClearPlate.MacCapture
```

可用 `DOTNET=/path/to/dotnet bash scripts/build-mac.sh` 指定 SDK。构建支持两种 CPU 的统一安装包。测试使用临时目录、假账号及隔离证书信任，不启用系统代理，不修改系统信任，不上传或打卡。

技术依据：[Apple SystemConfiguration 授权会话](https://developer.apple.com/documentation/systemconfiguration/scpreferencescreatewithauthorization(_:_:_:_:))、[.NET 10 macOS 支持列表](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)。

## v1.2.4 授权体验

优先使用已保存的有效登录，日常检查和打卡不需要重新采集。成功读取的会话加密密钥仅在本次进程内复用，失败或取消的读取不会缓存，后续可重新授权；磁盘仍保存 AES-GCM 密文，每次加密生成独立随机 nonce。网络设置的启用和恢复复用同一进程的 AuthorizationRef，是否再次授权仍由 macOS 决定。

重新采集时，证书信任的添加和移除仍可能分别需要系统授权；异常退出后的独立恢复进程也可能单独请求授权。不会保存系统密码或扩大钥匙串访问范围。当前应用使用 ad-hoc 签名，升级后系统可能再次询问会话密钥访问权限，不能承诺安装升级或采集全程只输一次密码。

## v1.2.3 连接修复

修复 Chromium 内核忽略带主机字符串的 Mac 信任规则的问题，保持证书 DNS 约束、SSL 策略、短效有效期和仅签发 API 叶证书。修复临时证书删除方式，按精确 DER 核对后用 Keychain 返回的真实引用移除；不删除其他证书。错误提示现在区分 CLIENT_TLS、CLIENT_REQUEST、平台侧 HTTPS/DNS/超时及 HTTP 状态，诊断仅包含固定代码，不保存请求、token 或原始异常文本。

参考：[Chromium 的 Mac 信任处理](https://chromium.googlesource.com/chromium/src/+/main/net/cert/internal/trust_store_mac.cc)、[Apple 精确删除 Keychain 项](https://developer.apple.com/documentation/security/secitemdelete(_:))。
