import Foundation
import AppKit
import Security
import SystemConfiguration

// Snapshot contains public certificate and proxy settings only, never a token/private key.
enum MacCaptureRecovery {
    static var file: URL { LocalStore.session.deletingLastPathComponent().appendingPathComponent("capture-recovery.plist") }
    static let keys = ["HTTPEnable", "HTTPProxy", "HTTPPort", "HTTPSEnable", "HTTPSProxy", "HTTPSPort", "ProxyAutoConfigEnable", "ProxyAutoDiscoveryEnable", "ExceptionsList"]
    static func installed(port: Int) -> [String: Any] {
        ["HTTPEnable": 1, "HTTPProxy": "127.0.0.1", "HTTPPort": port,
         "HTTPSEnable": 1, "HTTPSProxy": "127.0.0.1", "HTTPSPort": port,
         "ProxyAutoConfigEnable": 0, "ProxyAutoDiscoveryEnable": 0, "ExceptionsList": ["localhost", "127.0.0.1"]]
    }
    // Restore each field only while still owned, retaining changes made by the user.
    static func merged(current: [String: Any], original: [String: Any], installed: [String: Any]) -> [String: Any] {
        var result = current
        for prefix in ["HTTP", "HTTPS"] {
            guard equal(current[prefix + "Proxy"], installed[prefix + "Proxy"]), equal(current[prefix + "Port"], installed[prefix + "Port"]) else { continue }
            for suffix in ["Proxy", "Port", "Enable"] {
                let key = prefix + suffix
                if equal(current[key], installed[key]) { result[key] = original[key] }
            }
        }
        for key in ["ProxyAutoConfigEnable", "ProxyAutoDiscoveryEnable", "ExceptionsList"] {
            if equal(current[key], installed[key]) { result[key] = original[key] }
        }
        return result
    }
    static func equal(_ a: Any?, _ b: Any?) -> Bool {
        if a == nil || b == nil { return a == nil && b == nil }
        return (a as? NSObject)?.isEqual(b) == true
    }
    final class AuthorizedPreferences {
        let value: SCPreferences
        let authorization: AuthorizationRef
        init() throws {
            var reference: AuthorizationRef?
            guard AuthorizationCreate(nil, nil, [], &reference) == errAuthorizationSuccess, let reference else {
                throw PlateError.message("不能创建网络设置授权。")
            }
            guard let prefs = SCPreferencesCreateWithAuthorization(nil, "ClearPlate temporary capture" as CFString, nil, reference) else {
                AuthorizationFree(reference, [])
                throw PlateError.message("不能读取网络设置。")
            }
            authorization = reference; value = prefs
        }
        deinit { AuthorizationFree(authorization, []) }
    }
    static func preferences() throws -> AuthorizedPreferences { try AuthorizedPreferences() }
    static func commit(_ prefs: SCPreferences, path: String, proxies: [String: Any]) throws {
        guard SCPreferencesPathSetValue(prefs, path as CFString, proxies as CFDictionary),
              SCPreferencesCommitChanges(prefs), SCPreferencesApplyChanges(prefs) else {
            throw PlateError.message("网络设置未能保存；系统可能需要管理员授权。")
        }
    }
    final class RecoveryLock {
        private let descriptor: Int32
        init() throws {
            try LocalStore.prepare()
            let lock = file.deletingLastPathComponent().appendingPathComponent("capture-recovery.lock")
            let opened = open(lock.path, O_CREAT | O_RDWR | O_CLOEXEC, 0o600)
            guard opened >= 0 else { throw PlateError.message("无法创建恢复互斥锁。") }
            guard flock(opened, LOCK_EX) == 0 else { close(opened); throw PlateError.message("无法锁定恢复记录。") }
            descriptor = opened
        }
#if TESTING
        var closesOnExec: Bool { fcntl(descriptor, F_GETFD) & FD_CLOEXEC != 0 }
#endif
        deinit { flock(descriptor, LOCK_UN); close(descriptor) }
    }
    static func trustSettings() -> CFArray {
        // Chromium skips macOS trust records containing PolicyString. The root itself
        // retains its critical API DNS name constraint and only the API leaf is issued.
        let policy = SecPolicyCreateSSL(true, nil)
        let trust: [[String: Any]] = [[kSecTrustSettingsPolicy as String: policy,
            kSecTrustSettingsResult as String: NSNumber(value: SecTrustSettingsResult.trustRoot.rawValue)]]
        return trust as CFArray
    }
    static func activate(port: Int, der: Data, alive: () -> Bool) throws -> String {
        let mutex = try RecoveryLock()
        defer { withExtendedLifetime(mutex) {} }
        guard !FileManager.default.fileExists(atPath: file.path), let cert = SecCertificateCreateWithData(nil, der as CFData),
              (SecCertificateCopySubjectSummary(cert) as String?)?.hasPrefix("ClearPlate temporary login capture ") == true else {
            throw PlateError.message("恢复记录尚未处理或临时证书异常。")
        }
        let authorized = try preferences(); let prefs = authorized.value
        defer { withExtendedLifetime(authorized) {} }
        let store = SCDynamicStoreCreate(nil, "ClearPlate capture" as CFString, nil, nil)
        var service: String?
        if let store {
            for family in ["IPv4", "IPv6"] {
                if let state = SCDynamicStoreCopyValue(store, "State:/Network/Global/\(family)" as CFString) as? [String: Any], let id = state["PrimaryService"] as? String { service = id; break }
            }
        }
        guard let service else { throw PlateError.message("当前没有可识别的活动网络，请联网后重试。") }
        guard let network = SCNetworkServiceCopy(prefs, service as CFString), SCNetworkServiceGetEnabled(network) else {
            throw PlateError.message("当前活动网络不支持安全保存代理设置。")
        }
        let path = "/NetworkServices/\(service)/Proxies"
        guard SCPreferencesLock(prefs, true) else { throw PlateError.message("不能锁定网络设置。") }
        defer { SCPreferencesUnlock(prefs) }
        SCPreferencesSynchronize(prefs)
        let original = SCPreferencesPathGetValue(prefs, path as CFString) as? [String: Any] ?? [:]
        guard (original["SOCKSEnable"] as? NSNumber)?.boolValue != true,
              (original["HTTPProxyAuthenticated"] as? NSNumber)?.boolValue != true,
              (original["HTTPSProxyAuthenticated"] as? NSNumber)?.boolValue != true else {
            throw PlateError.message("当前网络使用 SOCKS 或认证代理，暂不自动修改；请先停止该代理后重试。")
        }
        let installed = installed(port: port), id = UUID().uuidString
        try LocalStore.prepare()
        let record: [String: Any] = ["id": id, "path": path, "original": original, "installed": installed, "certificate": der]
        try PropertyListSerialization.data(fromPropertyList: record, format: .binary, options: 0).write(to: file, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
        guard let main = Bundle.main.executableURL else { throw PlateError.message("恢复守护进程无法启动。") }
        let watchdog = Process(); watchdog.executableURL = main
        watchdog.arguments = ["--capture-watchdog", String(getpid()), id]
        watchdog.standardInput = FileHandle.nullDevice; watchdog.standardOutput = FileHandle.nullDevice; watchdog.standardError = FileHandle.nullDevice
        try watchdog.run()
        // Public certificate only; no keychain private keys and no global trust-all policy.
        let added = SecItemAdd([kSecClass: kSecClassCertificate, kSecValueRef: cert] as CFDictionary, nil)
        guard added == errSecSuccess || added == errSecDuplicateItem else { throw PlateError.message("临时证书未能添加到用户钥匙串。") }
        guard SecTrustSettingsSetTrustSettings(cert, .user, trustSettings()) == errSecSuccess else {
            throw PlateError.message("临时证书信任未获授权，未开始采集。")
        }
        guard alive() else { throw PlateError.message("采集已超时，未启用代理。") }
        var changed = original
        installed.forEach { changed[$0.key] = $0.value }
        try commit(prefs, path: path, proxies: changed)
        return id
    }
    static func certificateLookup(_ cert: SecCertificate) -> [String: Any] {
        [kSecClass as String: kSecClassCertificate,
         kSecMatchSubjectWholeString as String: (SecCertificateCopySubjectSummary(cert) as String?) ?? "",
         kSecReturnRef as String: true, kSecMatchLimit as String: kSecMatchLimitAll]
    }
    static func removeStoredCertificate(_ cert: SecCertificate,
        lookup: (CFDictionary, UnsafeMutablePointer<CFTypeRef?>?) -> OSStatus = SecItemCopyMatching,
        delete: (CFDictionary) -> OSStatus = SecItemDelete) -> OSStatus {
        guard (SecCertificateCopySubjectSummary(cert) as String?)?.hasPrefix("ClearPlate temporary login capture ") == true else { return errSecParam }
        var found: CFTypeRef?
        let status = lookup(certificateLookup(cert) as CFDictionary, &found)
        if status == errSecItemNotFound { return errSecSuccess }
        guard status == errSecSuccess else { return status }
        let bytes = SecCertificateCopyData(cert) as Data
        let exact = (found as? [SecCertificate] ?? []).filter { (SecCertificateCopyData($0) as Data) == bytes }
        if exact.isEmpty { return errSecSuccess }
        // MatchItemList must contain actual references returned by CopyMatching.
        return delete([kSecClass as String: kSecClassCertificate, kSecMatchItemList as String: exact] as CFDictionary)
    }
    static func restore(expected: String? = nil) throws {
        guard FileManager.default.fileExists(atPath: file.path) else { return }
        let mutex = try RecoveryLock()
        defer { withExtendedLifetime(mutex) {} }
        guard FileManager.default.fileExists(atPath: file.path) else { return }
        guard let record = try PropertyListSerialization.propertyList(from: Data(contentsOf: file), format: nil) as? [String: Any],
              let id = record["id"] as? String, let path = record["path"] as? String, path.hasPrefix("/NetworkServices/"), path.hasSuffix("/Proxies"),
              let original = record["original"] as? [String: Any], let installed = record["installed"] as? [String: Any],
              let der = record["certificate"] as? Data, let cert = SecCertificateCreateWithData(nil, der as CFData),
              (SecCertificateCopySubjectSummary(cert) as String?)?.hasPrefix("ClearPlate temporary login capture ") == true else {
            throw PlateError.message("恢复记录无法解析，请检查系统代理及临时证书。")
        }
        if let expected, expected != id { return }
        var failed = false
        do {
            let authorized = try preferences(); let prefs = authorized.value
        defer { withExtendedLifetime(authorized) {} }
            if let current = SCPreferencesPathGetValue(prefs, path as CFString) as? [String: Any],
               !NSDictionary(dictionary: current).isEqual(to: merged(current: current, original: original, installed: installed)) {
                guard SCPreferencesLock(prefs, true) else { throw PlateError.message("不能锁定恢复网络设置。") }
                defer { SCPreferencesUnlock(prefs) }
                SCPreferencesSynchronize(prefs)
                if let latest = SCPreferencesPathGetValue(prefs, path as CFString) as? [String: Any] {
                    let restored = merged(current: latest, original: original, installed: installed)
                    if !NSDictionary(dictionary: latest).isEqual(to: restored) { try commit(prefs, path: path, proxies: restored) }
                }
            }
        } catch { failed = true }
        let removedTrust = SecTrustSettingsRemoveTrustSettings(cert, .user)
        if removedTrust != errSecSuccess && removedTrust != errSecItemNotFound && removedTrust != errSecNoTrustSettings { failed = true }
        let removedCert = removeStoredCertificate(cert)
        if removedCert != errSecSuccess && removedCert != errSecItemNotFound { failed = true }
        guard !failed else { throw PlateError.message("临时代理或证书尚未完全恢复，请允许系统授权并重新启动本工具；恢复记录已保留。") }
        try FileManager.default.removeItem(at: file)
    }
    static func watchdog(arguments: [String]) {
        guard arguments.count == 4, let parent = Int32(arguments[2]) else { return }
        for _ in 0..<210 {
            if kill(parent, 0) != 0 { break }
            Thread.sleep(forTimeInterval: 1)
        }
        try? restore(expected: arguments[3]) // Failed recovery remains on disk for the next GUI launch.
    }
}

@MainActor final class MacTokenCapture {
    static weak var active: MacTokenCapture?
    var onStatus: ((String) -> Void)?
    var onToken: ((String) -> Void)?
    var onEnded: (() -> Void)?
    var process: Process?
    var input: Pipe?
    var output: Pipe?
    var timer: Timer?
    var pending = Data()
    var id: String?
    var stopped = false
    var ready = false
    func start() {
        Self.active = self
        #if arch(arm64)
        let architecture = "arm64"
#else
        let architecture = "x64"
#endif
        guard let executable = Bundle.main.url(forResource: "ClearPlate.MacCapture-" + architecture, withExtension: nil) else {
            onStatus?("采集组件缺失，请重新下载完整安装包。"); _ = stop(); return
        }
        let process = Process(); let input = Pipe(), output = Pipe()
        process.executableURL = executable; process.arguments = ["--stdio-capture"]
        process.standardInput = input; process.standardOutput = output; process.standardError = FileHandle.nullDevice
        self.process = process; self.input = input; self.output = output
        output.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let bytes = handle.availableData
            Task { @MainActor in self?.consume(bytes) }
        }
        process.terminationHandler = { [weak self] _ in
            Task { @MainActor in
                guard let self, !self.stopped else { return }
                self.onStatus?("采集组件已结束，正在恢复设置。")
                _ = self.stop()
            }
        }
        do { try process.run() }
        catch { onStatus?("采集组件无法启动。"); _ = stop(); return }
        timer = Timer.scheduledTimer(withTimeInterval: 180, repeats: false) { [weak self] _ in
            Task { @MainActor in
                self?.onStatus?("3 分钟内未获取到 token。请确认微信正常登录并重新打开小程序；该版本微信可能不采用系统代理。")
                _ = self?.stop()
            }
        }
    }
    func consume(_ bytes: Data) {
        guard !stopped else { return }
        if bytes.isEmpty { return }
        pending.append(bytes)
        if pending.count > 32768 { onStatus?("采集组件返回异常。"); _ = stop(); return }
        while let newline = pending.firstIndex(of: 10) {
            let line = pending.prefix(upTo: newline); pending.removeSubrange(...newline)
            guard let message = try? JSONSerialization.jsonObject(with: line) as? [String: Any], let type = message["type"] as? String else { continue }
            if type == "ready", !ready, let port = message["port"] as? Int, (1024...65535).contains(port),
               let b64 = message["certificate"] as? String, let der = Data(base64Encoded: b64) {
                do {
                    ready = true
                    id = try MacCaptureRecovery.activate(port: port, der: der, alive: { self.process?.isRunning == true })
                    onStatus?("临时采集已启用。请在微信打开光盘首页或“我的”，无需额外打卡。")
                    if let url = URL(string: "weixin://dl/business/?appid=wx6a4976bd627fc4ca&path=pages/index/dataHomePage") { _ = NSWorkspace.shared.open(url) }
                } catch { onStatus?(error.localizedDescription); _ = stop() }
            } else if type == "token", ready, let token = message["token"] as? String {
                onToken?(token)
            } else if type == "diagnostic", let code = message["code"] as? String {
                onStatus?(Self.diagnosticMessage(code))
            }
        }
    }
    static func diagnosticMessage(_ code: String) -> String {
        switch code {
        case "CLIENT_TLS": return "微信与本地代理的 TLS 握手未完成（CLIENT_TLS）。请停止连接后重试；不代表 token 已失效。"
        case "CLIENT_REQUEST": return "微信已连接，但请求没有完整传输（CLIENT_REQUEST）。"
        case "CLIENT_RESPONSE": return "平台已响应，微信端接收时断开了连接（CLIENT_RESPONSE）。"
        case "UPSTREAM_TLS": return "本地代理连接平台时 HTTPS 校验失败（UPSTREAM_TLS）；微信端握手已通过。"
        case "UPSTREAM_DNS": return "本地代理无法解析平台域名（UPSTREAM_DNS）。请检查网络。"
        case "UPSTREAM_TIMEOUT": return "本地代理等待平台响应超时（UPSTREAM_TIMEOUT）。"
        case "UPSTREAM_NETWORK": return "本地代理连接平台失败（UPSTREAM_NETWORK），不能据此判断微信拒绝证书。"
        case "NO_CANDIDATE": return "光盘接口已连通，尚未获得可用登录状态。请在微信正常登录并打开首页或“我的”。"
        default:
            if code.hasPrefix("HTTP_"), let status = Int(code.dropFirst(5)), (100...599).contains(status) {
                return "平台返回 HTTP \(status)（\(code)），微信端握手已通过，尚未同步登录状态。"
            }
            return "连接诊断暂不可用，请停止连接后重试。"
        }
    }
    @discardableResult func stop() -> Bool {
        if stopped { return true }
        stopped = true; timer?.invalidate(); timer = nil
        output?.fileHandleForReading.readabilityHandler = nil
        try? input?.fileHandleForWriting.write(contentsOf: Data("stop\n".utf8))
        try? input?.fileHandleForWriting.close()
        var restored = true
        do { try MacCaptureRecovery.restore(expected: id) }
        catch { restored = false; onStatus?(error.localizedDescription) }
        if process?.isRunning == true { process?.terminate() }
        pending.removeAll(); Self.active = nil; onEnded?()
        return restored
    }
}
