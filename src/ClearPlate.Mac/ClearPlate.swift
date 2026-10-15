import SwiftUI
import AppKit
import UniformTypeIdentifiers

struct SessionConfig: Codable {
    var headers: [String: String]
    var group_id: String
    var location: String
    var api_url: String
    var account_id: String?
    var token_updated_at: Double?
    var token: String { headers.first { $0.key.lowercased() == "x-token" }?.value ?? "" }
    func replacingToken(_ token: String) throws -> SessionConfig {
        let token = token.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !token.isEmpty, token.count <= 4096, !token.hasPrefix("code|:|"),
              token.utf8.allSatisfy({ (33...126).contains(Int($0)) }) else {
            throw PlateError.message("请输入完整的登录 token，临时登录 code 不能直接用作 token。")
        }
        var copy = self
        copy.headers = headers.filter { $0.key.lowercased() != "x-token" }
        copy.headers["x-token"] = token
        return copy
    }
}

enum PlateError: LocalizedError {
    case message(String)
    case authenticationRequired
    case preflightWait(String)
    var errorDescription: String? {
        switch self {
        case .message(let s), .preflightWait(let s): return s
        case .authenticationRequired: return "登录会话已失效，请在微信正常登录光盘打卡，然后点击“从微信自动获取 token”。"
        }
    }
}

enum DefaultPhoto {
    static func url() throws -> URL {
        guard let url = Bundle.main.url(forResource: "光盘", withExtension: "png") else {
            throw PlateError.message("程序内置照片缺失，请重新打开完整的光盘打卡.app。")
        }
        return url
    }
    static let preview: NSImage? = (try? url()).flatMap { NSImage(contentsOf: $0) }
}

enum LocalStore {
    static let directory = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("ClearPlateDesktop", isDirectory: true)
    static let session = URL(fileURLWithPath: Bundle.main.object(forInfoDictionaryKey: "ClearPlateSessionPath") as? String
        ?? directory.appendingPathComponent("session.json").path)
    static func prepare() throws {
        try FileManager.default.createDirectory(at: session.deletingLastPathComponent(), withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700])
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: session.deletingLastPathComponent().path)
    }
    static func emptyConfig() -> SessionConfig {
        SessionConfig(headers: ["x-version": "8.2.8", "x-oauth-type": "1", "x-app-platform": "wx", "x-requested-with": "XMLHttpRequest", "user-agent": "ClearPlateDesktop/1.2"],
            group_id: "", location: "", api_url: "https://clearplate-api.guangpanxingdong.com/index.php")
    }
    static var settings: URL { session.deletingLastPathComponent().appendingPathComponent("settings.json") }
    static func load() throws -> SessionConfig {
        if !FileManager.default.fileExists(atPath: session.path) { return emptyConfig() }
        return try SessionVault.decode(Data(contentsOf: session))
    }
    static func save(_ config: SessionConfig) throws {
        try prepare()
        try SessionVault.encode(config).write(to: session, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: session.path)
    }
    static func importLegacy(_ folder: URL) throws {
        guard !FileManager.default.fileExists(atPath: session.path), !FileManager.default.fileExists(atPath: settings.path) else {
            throw PlateError.message("新版已存在本地数据，没有覆盖。")
        }
        let config = try SessionVault.decode(Data(contentsOf: folder.appendingPathComponent("session.json")))
        let records = try Data(contentsOf: folder.appendingPathComponent("settings.json"))
        guard (try JSONSerialization.jsonObject(with: records)) is [String: Any] else { throw PlateError.message("旧设置结构无效。") }
        let encrypted = try SessionVault.encode(config)
        try prepare()
        do {
            try records.write(to: settings, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: settings.path)
            try encrypted.write(to: session, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: session.path)
        } catch {
            try? FileManager.default.removeItem(at: settings); try? FileManager.default.removeItem(at: session)
            throw error
        }
    }
    static func settingsData() -> [String: Any] {
        guard let data = try? Data(contentsOf: settings), let value = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return [:] }
        return value
    }
    static func saveSettings(_ value: [String: Any]) throws {
        try prepare()
        let data = try JSONSerialization.data(withJSONObject: value)
        try data.write(to: settings, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: settings.path)
    }
}

struct UserStatus {
    let successes: Int
    let dailyCounter: Int
    let nickname: String
    let accountID: String
    let lastCheck: Date?
    init(_ data: [String: Any]) throws {
        func integer(_ value: Any?) -> Int? {
            if let s = value as? String { return Int(s) }
            return (value as? NSNumber)?.intValue
        }
        guard let success = integer(data["clearplate_num_success"]), let today = integer(data["today_clearplate_num"]) else {
            throw PlateError.message("账号返回结构发生变化，暂时不能核对打卡结果。")
        }
        self.successes = success; self.dailyCounter = today; self.nickname = data["nickname"] as? String ?? "当前账号"
        guard let id = data["user_id"], let accountID = id as? String ?? (id as? NSNumber)?.stringValue, !accountID.isEmpty else {
            throw PlateError.message("账号返回没有用户标识，无法安全核对登录 token。")
        }
        self.accountID = accountID
        let last = (data["last_clearplate_clock_time"] as? NSNumber)?.doubleValue
            ?? Double(data["last_clearplate_clock_time"] as? String ?? "") ?? 0
        self.lastCheck = last > 0 ? Date(timeIntervalSince1970: last) : nil
    }
}

enum PreflightRules {
    static func reason(_ user: UserStatus, now: Date = Date()) -> String? {
        if user.dailyCounter <= 0 { return "当天剩余打卡次数为 0，没有上传照片。" }
        if let last = user.lastCheck, now.timeIntervalSince(last) < 7200 { return "距平台上次成功打卡不足 2 小时，没有上传照片。" }
        return nil
    }
}

final class PlateRedirectGuard: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

final class PlateAPI: @unchecked Sendable {
    var config: SessionConfig
    let network: URLSession
    var tokenUpdated = false
    var tokenSyncWarning: String?
    init(_ config: SessionConfig, protocolClasses: [AnyClass]? = nil) {
        self.config = config
        let c = URLSessionConfiguration.ephemeral
        c.timeoutIntervalForRequest = 20
        c.timeoutIntervalForResource = 45
        c.httpCookieStorage = nil
        if let protocolClasses { c.protocolClasses = protocolClasses }
        self.network = URLSession(configuration: c, delegate: PlateRedirectGuard(), delegateQueue: nil)
    }
    static func day(_ date: Date = Date(), format: String = "yyyy-MM-dd") -> String {
        let f = DateFormatter(); f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "Asia/Shanghai"); f.dateFormat = format
        return f.string(from: date)
    }
    static func form(_ values: [String: String]) -> Data {
        let allowed = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~")
        let s = values.keys.sorted().map { key in
            "\(key.addingPercentEncoding(withAllowedCharacters: allowed)!)=\(values[key]!.addingPercentEncoding(withAllowedCharacters: allowed)!)"
        }.joined(separator: "&")
        return Data(s.utf8)
    }
    func api(_ route: String, form: [String: String]? = nil, using credentials: SessionConfig? = nil) async throws -> [String: Any] {
        let credentials = credentials ?? config
        guard var url = URLComponents(string: credentials.api_url), url.scheme == "https", url.host == "clearplate-api.guangpanxingdong.com", url.path == "/index.php", url.port == nil || url.port == 443, url.user == nil, url.password == nil else {
            throw PlateError.message("会话配置中的 API 地址不符合本次验证的接口。")
        }
        url.queryItems = [URLQueryItem(name: "r", value: route)]
        var request = URLRequest(url: url.url!)
        let allowed = Set(["user-agent", "x-token", "x-access-token", "x-version", "x-oauth-type", "x-app-platform", "x-requested-with", "referer", "charset"])
        for (key, value) in credentials.headers where allowed.contains(key.lowercased()) { request.setValue(value, forHTTPHeaderField: key) }
        request.httpMethod = form == nil ? "GET" : "POST"
        if let form { request.httpBody = Self.form(form); request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type") }
        let (bytes, response) = try await network.data(for: request)
        if (response as? HTTPURLResponse)?.statusCode == 401 { throw PlateError.authenticationRequired }
        guard let response = response as? HTTPURLResponse, response.statusCode == 200 else {
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            throw PlateError.message("接口返回 HTTP \(code)，没有确认业务成功。")
        }
        guard let result = try? JSONSerialization.jsonObject(with: bytes) as? [String: Any] else {
            throw PlateError.message("接口返回了非 JSON 内容，暂时停止提交。")
        }
        if (result["code"] as? NSNumber)?.intValue == 10003 { throw PlateError.authenticationRequired }
        return result
    }
    private func rawUser(using credentials: SessionConfig) async throws -> (UserStatus, [String: Any]) {
        let result = try await api("/user/user-info", using: credentials)
        guard (result["code"] as? NSNumber)?.intValue == 10007, let data = result["data"] as? [String: Any] else {
            throw PlateError.message("账号查询没有返回成功，未更新 token。")
        }
        return (try UserStatus(data), data)
    }
    func user() async throws -> UserStatus {
        let (user, data) = try await rawUser(using: config)
        if let bound = config.account_id, bound != user.accountID {
            throw PlateError.message("当前 token 属于其他账号，已停止操作。")
        }
        if config.account_id == nil {
            config.account_id = user.accountID
            try LocalStore.save(config)
        }
        if let issued = data["token"] as? String, !issued.isEmpty, issued != config.token {
            do { return try await adoptToken(issued) }
            catch { tokenSyncWarning = "平台返回的新 token 未通过核验，已保留原会话。" }
        }
        return user
    }
    func adoptToken(_ token: String, confirmInitial: @MainActor (UserStatus) -> Bool = { _ in false }) async throws -> UserStatus {
        let bound = config.account_id
        var candidate = try config.replacingToken(token)
        let (verified, _) = try await rawUser(using: candidate)
        if let bound, verified.accountID != bound {
            throw PlateError.message("新 token 属于其他账号，原会话已保留。")
        }
        if bound == nil {
            guard await confirmInitial(verified) else { throw PlateError.message("已取消首次账号绑定，未保存会话。") }
            candidate.account_id = verified.accountID
        }
        candidate.token_updated_at = Date().timeIntervalSince1970
        try LocalStore.save(candidate)
        tokenUpdated = candidate.token != config.token
        config = candidate
        return verified
    }
    func upload(_ bytes: Data, mime: String, ext: String) async throws -> String {
        let prefix = "clockImg/" + Self.day(format: "yyyy/MM/dd") + "/"
        let result = try await api("/upload/my-upload", form: ["files": prefix])
        guard (result["code"] as? NSNumber)?.intValue == 10007, let data = result["data"] as? [String: Any],
              let host = data["host"] as? String, let destination = URL(string: host),
              destination.scheme == "https", destination.host == "clearplateimg.guangpanxingdong.com",
              let dir = data["dir"] as? String, dir == prefix,
              let policy = data["policy"] as? String, let access = data["accessid"] as? String,
              let signature = data["signature"] as? String, let expires = data["expire"] as? NSNumber,
              expires.doubleValue > Date().timeIntervalSince1970 + 2 else {
            throw PlateError.message("上传凭证不符合已核对的格式或已过期，没有上传照片。")
        }
        let filename = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased() + "." + ext
        let key = dir + filename, boundary = "ClearPlate-" + UUID().uuidString
        var body = Data()
        func append(_ s: String) { body.append(Data(s.utf8)) }
        for (name, value) in [("key", key), ("policy", policy), ("OSSAccessKeyId", access),
                              ("success_action_status", "200"), ("signature", signature), ("Content-Type", mime)] {
            append("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n")
        }
        append("--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"\(filename)\"\r\nContent-Type: \(mime)\r\n\r\n")
        body.append(bytes); append("\r\n--\(boundary)--\r\n")
        var request = URLRequest(url: destination)
        request.httpMethod = "POST"; request.httpBody = body
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        let (_, response) = try await network.data(for: request)
        guard let response = response as? HTTPURLResponse, response.statusCode == 200 else {
            throw PlateError.message("照片上传没有获得已验证的 HTTP 200 返回，未提交打卡。")
        }
        return "/" + key
    }
}

enum PlateSchedule {
    static let times = ["08:00", "12:00", "18:00"]
    static func dueSlot(at date: Date = Date()) -> String? {
        let time = PlateAPI.day(date, format: "HH:mm")
        return times.first { slot in
            let end = String(slot.prefix(3)) + "05"
            return time >= slot && time < end
        }
    }
    static func key(_ slot: String, at date: Date = Date()) -> String {
        PlateAPI.day(date) + "@" + slot
    }
    static func suggestedSlot(at date: Date = Date()) -> String {
        if let due = dueSlot(at: date) { return due }
        let time = PlateAPI.day(date, format: "HH:mm")
        return times.first { $0 >= time } ?? times.last!
    }
}

enum PreflightRetry {
    static func isTransientNetwork(_ error: Error) -> Bool {
        let error = error as NSError
        guard error.domain == NSURLErrorDomain else { return false }
        return [URLError.notConnectedToInternet, .networkConnectionLost, .timedOut,
                .cannotFindHost, .cannotConnectToHost, .dnsLookupFailed].contains { $0.rawValue == error.code }
    }
}

@MainActor final class PlateModel: ObservableObject {
    @Published var group = ""
    @Published var location = ""
    @Published var schedule = false { didSet { persist() } }
    @Published var busy = false
    @Published var status = "默认使用内置的光盘.png；每天 08:00、12:00、18:00 各自动打卡一次。"
    @Published var account = "会话凭据保存在本机"
    @Published var success = false
    @Published var selectedSlot = PlateSchedule.suggestedSlot()
    @Published var attemptedSlots = Set<String>()
    @Published var confirmedSlots = Set<String>()
    @Published var slotDetails = [String: [String: String]]()
    @Published var needsLogin = false
    @Published var showLoginAlert = false
    @Published var showTokenImport = false
    @Published var tokenDraft = ""
    @Published var localSessionBlocked = false
    @Published var storageFailed = false
    @Published var capturing = false
    var capture: MacTokenCapture?
    var timer: Timer?
    var legacyConfirmedDay = ""
    init() {
        do {
            let c = try LocalStore.load(); group = c.group_id; location = c.location
        } catch { localSessionBlocked = true }
        let s = LocalStore.settingsData()
        attemptedSlots = Set(s["attempted_slots"] as? [String] ?? [])
        confirmedSlots = Set(s["confirmed_slots"] as? [String] ?? [])
        slotDetails = s["slot_details"] as? [String: [String: String]] ?? [:]
        attemptedSlots.formUnion(confirmedSlots)
        legacyConfirmedDay = s["legacy_confirmed_day"] as? String ?? s["confirmed_day"] as? String ?? ""
        needsLogin = s["needs_login"] as? Bool ?? false
        if let slot = s["selected_slot"] as? String, PlateSchedule.times.contains(slot) { selectedSlot = slot }
        schedule = s["schedule"] as? Bool ?? false
        if confirmedSlots.contains(PlateSchedule.key(selectedSlot)) {
            success = true; status = "今天 \(selectedSlot) 时段已核对成功，其他时段仍按计划执行。"
        } else if attemptedSlots.contains(PlateSchedule.key(selectedSlot)) {
            status = attemptExplanation(selectedSlot, key: PlateSchedule.key(selectedSlot))
        }
        if needsLogin { status = PlateError.authenticationRequired.localizedDescription }
        if localSessionBlocked { status = "本机会话暂不可读取。请允许系统钥匙串授权，然后在“更多”中检查账号状态；自动提交已暂停。" }
        timer = Timer.scheduledTimer(withTimeInterval: 15, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
    }
    @discardableResult func persist() -> Bool {
        do {
            try LocalStore.saveSettings(["schedule": schedule, "selected_slot": selectedSlot,
            "attempted_slots": attemptedSlots.sorted(), "confirmed_slots": confirmedSlots.sorted(),
            "legacy_confirmed_day": legacyConfirmedDay, "needs_login": needsLogin,
            "slot_details": slotDetails])
            storageFailed = false; return true
        } catch {
            storageFailed = true
            status = "本地记录保存失败，已停止自动提交，请检查磁盘和目录权限后重启工具。"
            return false
        }
    }
    @discardableResult func recordSlot(_ key: String, stage: String, outcome: String = "执行中", message: String = "", error: Error? = nil, at date: Date = Date()) -> Bool {
        var detail = slotDetails[key] ?? [:]
        let time = PlateAPI.day(date, format: "yyyy-MM-dd HH:mm:ss")
        detail["started_at"] = detail["started_at"] ?? time
        detail["updated_at"] = time
        detail["stage"] = stage
        detail["outcome"] = outcome
        if let error {
            let nsError = error as NSError
            detail["error_domain"] = nsError.domain
            detail["error_code"] = String(nsError.code)
        } else {
            detail.removeValue(forKey: "error_domain")
            detail.removeValue(forKey: "error_code")
        }
        var safeMessage = message
        if let config = try? LocalStore.load() {
            for secret in [config.token, config.location] where !secret.isEmpty {
                safeMessage = safeMessage.replacingOccurrences(of: secret, with: "[已隐藏]")
            }
        }
        detail["message"] = String(safeMessage.prefix(600))
        slotDetails[key] = detail
        return persist()
    }
    func attemptExplanation(_ slot: String, key: String) -> String {
        if let detail = slotDetails[key] {
            let time = detail["updated_at"] ?? "时间未记录"
            let stage = detail["stage"] ?? "阶段未记录"
            let outcome = detail["outcome"] ?? "结果未确认"
            let message = detail["message"] ?? ""
            if outcome == "等待网络" {
                return "\(slot) 上次记录：\(time)，查询账号时网络故障，尚未上传照片或提交打卡。\(message) 网络恢复后仅在 \(slot) 开始后的 5 分钟窗口内自动重试；错过窗口不会补交。"
            }
            if stage == "查询账号", outcome == "未成功", message == "The Internet connection appears to be offline." {
                return "\(slot) 上次记录：\(time)，查询账号时网络离线，尚未上传照片或提交打卡。此记录由旧版标记为已尝试，未自动补交；新版已修正提交前网络故障的重试逻辑。"
            }
            if outcome == "执行中" {
                return "\(slot) 上次记录：\(time)，执行到「\(stage)」。尚未确认结果，本时段不会自动重复提交，请先在小程序核对。"
            }
            return "\(slot) 上次记录：\(time)，\(outcome)，阶段「\(stage)」。\(message) 本时段不会自动重复提交，请先在小程序核对。"
        }
        return "\(slot) 已有尝试记录，但旧版未保存发生时间、执行阶段和首次错误；不能据此判断平台拒绝原因。本时段不会自动重复提交，请先在小程序核对。"
    }
    var selectedSlotRecord: String? {
        let key = PlateSchedule.key(selectedSlot)
        guard attemptedSlots.contains(key) || slotDetails[key] != nil else { return nil }
        if confirmedSlots.contains(key) {
            if let time = slotDetails[key]?["updated_at"] { return "\(selectedSlot) 已于 \(time) 核对成功。" }
            return "\(selectedSlot) 已核对成功；旧版未保存执行时间。"
        }
        return attemptExplanation(selectedSlot, key: key)
    }
    var dailyOverview: String {
        PlateSchedule.times.map { slot in
            let key = PlateSchedule.key(slot)
            let result = confirmedSlots.contains(key) ? "成功" : attemptedSlots.contains(key)
                ? (slotDetails[key]?["outcome"] == "未成功" ? "未成功" : "已尝试（未确认）")
                : (slotDetails[key]?["outcome"] == "等待网络" ? "未提交（网络故障）" : "待执行")
            return slot + " " + result
        }.joined(separator: " · ")
    }
    func allowsAttempt(_ slot: String, key: String) -> Bool {
        guard !busy, !capturing, !storageFailed, !localSessionBlocked, PlateSchedule.times.contains(slot) else { return false }
        if FileManager.default.fileExists(atPath: MacCaptureRecovery.file.path) {
            status = "临时网络设置尚未恢复，请重新启动工具并允许系统恢复授权。"; return false
        }
        if needsLogin { status = PlateError.authenticationRequired.localizedDescription; showLoginAlert = true; return false }
        if confirmedSlots.contains(key) { status = "这个 \(slot) 时段已核对成功，已停止重复提交。"; return false }
        if attemptedSlots.contains(key) { status = attemptExplanation(slot, key: key); return false }
        return true
    }
    func tick() {
        guard schedule && !busy && !capturing && !needsLogin && !storageFailed && !localSessionBlocked, !FileManager.default.fileExists(atPath: MacCaptureRecovery.file.path), let slot = PlateSchedule.dueSlot() else { return }
        let key = PlateSchedule.key(slot)
        guard !attemptedSlots.contains(key), !confirmedSlots.contains(key) else { return }
        selectedSlot = slot; submitDefaultPhoto(slot: slot)
    }
    func currentConfig(requireLocation: Bool = true) throws -> SessionConfig {
        var config: SessionConfig
        do { config = try LocalStore.load() }
        catch { localSessionBlocked = true; throw error }
        if localSessionBlocked {
            group = config.group_id; location = config.location; localSessionBlocked = false
        }
        let parts = location.split(separator: ",").compactMap { Double($0.trimmingCharacters(in: .whitespaces)) }
        guard !requireLocation || (parts.count == 2 && parts.allSatisfy { $0.isFinite } && abs(parts[0]) <= 90 && abs(parts[1]) <= 180) else {
            throw PlateError.message("请填写真实打卡位置，格式为纬度,经度。")
        }
        guard group.isEmpty || Int(group) != nil else { throw PlateError.message("组织 ID 应为整数，或与原请求一致留空。") }
        config.group_id = group; config.location = location
        try LocalStore.save(config)
        return config
    }
    func check() {
        guard !busy, !capturing else { return }; busy = true; success = false; status = "正在直接读取账号状态…"
        Task {
            defer { busy = false }
            do {
                let api = PlateAPI(try currentConfig(requireLocation: false)), user = try await api.user()
                account = user.nickname
                needsLogin = false; persist()
                status = api.tokenUpdated ? "平台新 token 已自动核验并保存。累计成功打卡 \(user.successes) 次。"
                    : "会话可用，平台尚未下发新 token。累计成功打卡 \(user.successes) 次。"
                if let warning = api.tokenSyncWarning { status += " " + warning }
            } catch { handle(error) }
        }
    }
    func handle(_ error: Error) {
        status = error.localizedDescription
        if let plateError = error as? PlateError, case .authenticationRequired = plateError {
            needsLogin = true; showLoginAlert = true; persist()
        }
    }
    func openMiniProgram() {
        if let url = URL(string: "weixin://dl/business/?appid=wx6a4976bd627fc4ca&path=pages/index/dataHomePage") {
            if !NSWorkspace.shared.open(url) { status = "未能直接打开小程序，请在微信中手动打开光盘打卡。" }
        }
    }
    func confirmAccount(_ user: UserStatus) -> Bool {
        let alert = NSAlert(); alert.messageText = "确认首次绑定账号"
        alert.informativeText = "平台返回账号：\(user.nickname)。确认这是你本人账号后才保存会话。"
        alert.addButton(withTitle: "取消"); alert.addButton(withTitle: "确认本人账号")
        return alert.runModal() == .alertSecondButtonReturn
    }
    func startCapture() {
        guard !busy, !capturing else { return }
        let alert = NSAlert(); alert.messageText = "从微信自动获取 token（试验功能）"
        alert.informativeText = "将临时启用当前网络的本地代理并为约 6 分钟有效的证书设置 SSL 信任。证书自身包含光盘 API 的关键 DNS 域名限制，仅签发该 API 的服务证书。系统可能要求授权网络设置及钥匙串操作。只处理光盘接口，不保存请求日志；其他 HTTPS 原样加密转发。采集最多 3 分钟，停止或退出时恢复；异常退出由恢复进程及下次启动处理，恢复失败会提示。请正常登录微信并打开首页或“我的”，无需额外打卡。微信不接受代理或证书时可能采集失败。"
        alert.addButton(withTitle: "取消"); alert.addButton(withTitle: "同意并开始")
        guard alert.runModal() == .alertSecondButtonReturn else { return }
        capturing = true; status = "正在准备临时采集…"
        let collector = MacTokenCapture(); capture = collector
        collector.onStatus = { [weak self] message in self?.status = message }
        collector.onToken = { [weak self] token in self?.acceptCaptured(token) }
        collector.onEnded = { [weak self] in self?.capturing = false; self?.capture = nil }
        collector.start()
    }
    func stopCapture() { capture?.stop(); capture = nil; capturing = false }
    func acceptCaptured(_ token: String) {
        guard capturing else { return }
        // Restore before issuing the independent verification request.
        guard capture?.stop() == true else { capturing = false; capture = nil; return }
        capture = nil; capturing = false; busy = true
        Task {
            defer { busy = false }
            do {
                let api = PlateAPI(try currentConfig(requireLocation: false))
                let user = try await api.adoptToken(token, confirmInitial: confirmAccount)
                account = user.nickname; needsLogin = false; showLoginAlert = false; persist()
                status = "微信 token 已只读核验并保存，临时代理和证书已移除。"
            } catch { status = "自动获取的 token 未通过核验，原会话已保留。" + error.localizedDescription }
        }
    }
    func importToken() {
        guard !busy, !capturing else { return }; busy = true
        Task {
            defer { busy = false }
            do {
                let api = PlateAPI(try currentConfig(requireLocation: false))
                let user = try await api.adoptToken(tokenDraft, confirmInitial: confirmAccount)
                account = user.nickname; needsLogin = false; persist()
                tokenDraft = ""; showTokenImport = false; showLoginAlert = false
                status = api.tokenUpdated ? "新 token 已核验为原账号并保存，自动打卡已恢复。" : "导入的是原 token，会话仍有效；没有改变它的服务器有效期。"
            } catch { status = "新 token 核验失败，未替换原会话。" + error.localizedDescription }
        }
    }
    func choosePhoto() {
        let slot = selectedSlot
        guard allowsAttempt(slot, key: PlateSchedule.key(slot)) else { return }
        NSApplication.shared.activate(ignoringOtherApps: true)
        let panel = NSOpenPanel(); panel.title = "选择当天真实光盘照片"
        panel.allowedContentTypes = [.jpeg, .png]; panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        guard panel.runModal() == .OK, let url = panel.url else { status = "已取消选图，没有提交打卡。"; return }
        submitPhoto(url, slot: slot)
    }
    func submitDefaultPhoto(slot: String? = nil) {
        let slot = slot ?? selectedSlot
        guard allowsAttempt(slot, key: PlateSchedule.key(slot)) else { return }
        do { submitPhoto(try DefaultPhoto.url(), slot: slot) }
        catch { status = error.localizedDescription }
    }
    func submitPhoto(_ url: URL, slot: String) {
        let key = PlateSchedule.key(slot)
        guard allowsAttempt(slot, key: key) else { return }
        attemptedSlots.insert(key)
        guard recordSlot(key, stage: "准备照片") else { return }
        busy = true; success = false
        Task {
            defer { busy = false }
            var uploadStarted = false
            var submissionStarted = false
            var submissionRejected = false
            var stage = "准备照片"
            do {
                let config = try currentConfig(), api = PlateAPI(config)
                let data = try Data(contentsOf: url)
                guard data.count >= 1024 && data.count <= 20 * 1024 * 1024, let image = NSImage(data: data), image.size.width > 0 else {
                    throw PlateError.message("请选择 1 KB 至 20 MB 的有效 JPG 或 PNG 照片。")
                }
                status = "正在核对 \(slot) 时段打卡前的账号记录…"
                stage = "查询账号"; recordSlot(key, stage: stage)
                let before = try await api.user(); account = before.nickname
                if let reason = PreflightRules.reason(before) { throw PlateError.preflightWait(reason) }
                status = "正在上传照片…"
                stage = "上传照片"; guard recordSlot(key, stage: stage) else { throw PlateError.message("本地记录保存失败，没有开始上传。") }
                let ext = url.pathExtension.lowercased() == "png" ? "png" : "jpg"
                uploadStarted = true
                let path = try await api.upload(data, mime: ext == "png" ? "image/png" : "image/jpeg", ext: ext)
                status = "照片已上传，正在提交打卡…"
                stage = "提交打卡"; guard recordSlot(key, stage: stage) else { throw PlateError.message("本地记录保存失败，没有提交打卡。") }
                submissionStarted = true
                let result = try await api.api("/user-clear-plate/clear-plate", form: ["group_id": config.group_id, "path_url": path, "location": config.location])
                let code = (result["code"] as? NSNumber)?.intValue ?? 0
                let message = result["msg"] as? String ?? "未返回说明"
                submissionRejected = code != 10007
                if code == 12012 { throw PlateError.message("图片校验失败（12012），没有成功打卡。请换当天真实光盘照片。") }
                guard code == 10007 else { throw PlateError.message("提交未成功，接口返回 \(code)：\(message)。") }
                stage = "核对成功记录"; recordSlot(key, stage: stage)
                var confirmed = false
                for attempt in 0..<3 {
                    if attempt > 0 { try await Task.sleep(nanoseconds: 1_000_000_000) }
                    let after = try await api.user()
                    if after.successes > before.successes { confirmed = true; break }
                }
                guard confirmed else { throw PlateError.message("提交返回 \(code)：\(message)。尚未查到新增成功记录，请在小程序核对后再操作。") }
                confirmedSlots.insert(key)
                recordSlot(key, stage: stage, outcome: "成功", message: "接口返回 10007，且累计成功次数增加。")
                success = true
                status = "\(slot) 时段打卡成功，已核对账号记录。其他时段仍按计划执行。"
            } catch {
                if !uploadStarted, localSessionBlocked {
                    attemptedSlots.remove(key)
                    recordSlot(key, stage: stage, outcome: "等待本机会话", message: error.localizedDescription, error: error)
                    status = "本机会话无法读取，没有上传或提交。请允许钥匙串授权后检查账号状态。"
                    return
                }
                if !uploadStarted, let failure = error as? PlateError, case .preflightWait = failure {
                    attemptedSlots.remove(key)
                    recordSlot(key, stage: stage, outcome: "等待条件", message: error.localizedDescription)
                    status = error.localizedDescription; return
                }
                if !uploadStarted, stage == "查询账号", PreflightRetry.isTransientNetwork(error) {
                    attemptedSlots.remove(key)
                    recordSlot(key, stage: stage, outcome: "等待网络", message: error.localizedDescription, error: error)
                    status = attemptExplanation(slot, key: key)
                    return
                }
                recordSlot(key, stage: stage, outcome: submissionStarted && !submissionRejected ? "结果未确认" : "未成功", message: error.localizedDescription, error: error)
                if !uploadStarted, let plateError = error as? PlateError, case .authenticationRequired = plateError {
                    attemptedSlots.remove(key); persist()
                }
                handle(error)
            }
        }
    }
}

struct PlateView: View {
    @StateObject var model = PlateModel()
    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack(alignment: .top) {
                Image(systemName: "leaf.circle.fill").font(.system(size: 48)).foregroundStyle(.green)
                VStack(alignment: .leading, spacing: 4) {
                    Text("光盘打卡").font(.system(size: 26, weight: .semibold))
                    Text("每天 08:00 / 12:00 / 18:00 · 北京时间").foregroundStyle(.secondary)
                }
                Spacer()
                Text("v1.2.3 试验版").font(.caption).padding(6).background(Color.orange.opacity(0.12)).cornerRadius(6)
            }
            Divider()
            Text(model.account).font(.headline)
            Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 12) {
                GridRow { Text("组织 ID"); TextField("与原打卡请求一致", text: $model.group) }
                GridRow { Text("打卡位置"); TextField("真实纬度,经度", text: $model.location) }
            }
            .textFieldStyle(.roundedBorder).disabled(model.busy || model.capturing)
            HStack(spacing: 12) {
                if let image = DefaultPhoto.preview {
                    Image(nsImage: image).resizable().scaledToFit().frame(width: 72, height: 72)
                }
                VStack(alignment: .leading, spacing: 4) {
                    Text("默认照片：光盘.png").font(.headline)
                    Text("原图已内置，自动打卡无需再选照片。")
                        .font(.callout).foregroundStyle(.secondary)
                }
            }
            Toggle("每天三个时段自动用默认照片打卡", isOn: $model.schedule)
            Text(model.dailyOverview).font(.callout).foregroundStyle(.secondary)
            Text("请保持程序运行、电脑唤醒。照片仍由平台正常校验。")
                .font(.callout).foregroundStyle(.secondary)
            Picker("手动打卡时段", selection: $model.selectedSlot) {
                ForEach(PlateSchedule.times, id: \.self) { slot in Text(slot).tag(slot) }
            }.pickerStyle(.segmented).disabled(model.busy || model.capturing)
                .onChange(of: model.selectedSlot) { model.persist() }
            if let record = model.selectedSlotRecord {
                Text(record).font(.callout).textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            HStack(spacing: 12) {
                Button("立即打卡") { model.submitDefaultPhoto() }
                    .buttonStyle(.borderedProminent).tint(.green)
                    .help("使用默认照片提交当前所选时段")
                    .disabled(model.busy || model.capturing || model.localSessionBlocked)
                Button("连接微信") { model.startCapture() }
                    .help("打开小程序并自动同步本人登录状态")
                    .disabled(model.busy || model.capturing)
                Menu("更多") {
                    Button("选择照片打卡…") { model.choosePhoto() }.disabled(model.localSessionBlocked)
                    Button("检查账号状态") { model.check() }
                    Divider()
                    Button("打开光盘小程序") { model.openMiniProgram() }
                    Button("手动导入登录凭据…") { model.showTokenImport = true }
                }.disabled(model.busy || model.capturing)
                if model.capturing { Button("停止连接") { model.stopCapture() } }
                if model.busy { ProgressView().controlSize(.small) }
            }
            Text(model.status).font(.callout).foregroundStyle(model.success ? Color.green : Color.primary)
                .frame(maxWidth: .infinity, minHeight: 55, alignment: .leading)
                .padding(12).background(Color.gray.opacity(0.08)).cornerRadius(8)
            Text("平台新 token 自动核验并保存；登录失效需重新授权。查询账号时网络故障可在原窗口内重试，开始上传后不自动重复提交。")
                .font(.caption).foregroundStyle(.secondary)
        }
        .padding(28).frame(width: 570)
        .alert("登录会话已失效", isPresented: $model.showLoginAlert) {
            Button("自动获取 token") { model.startCapture() }
            Button("打开微信") { model.openMiniProgram() }
            Button("稍后处理", role: .cancel) {}
        } message: {
            Text("需在微信正常登录，然后点击自动获取 token。程序已暂停自动提交，核验原账号后恢复；服务器有效期不能由本程序延长。")
        }
        .sheet(isPresented: $model.showTokenImport) {
            VStack(alignment: .leading, spacing: 16) {
                Text("手动导入登录凭据").font(.headline)
                Text("从本人正常登录请求中取得 X-Token。只向光盘平台核验，账号一致后才替换原会话。")
                SecureField("粘贴完整 X-Token", text: $model.tokenDraft).textFieldStyle(.roundedBorder)
                Text(model.status).font(.callout)
                HStack {
                    Button("取消") { model.tokenDraft = ""; model.showTokenImport = false }
                    Spacer()
                    Button("核验并保存") { model.importToken() }.buttonStyle(.borderedProminent)
                }.disabled(model.busy || model.capturing)
            }.padding(24).frame(width: 460)
        }
    }
}

final class PlateAppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        MacTokenCapture.active?.stop()
        return .terminateNow
    }
}
struct PlateApp: App {
    @NSApplicationDelegateAdaptor(PlateAppDelegate.self) var delegate
    var body: some Scene { WindowGroup { PlateView() }.windowResizability(.contentSize) }
}

@main struct Entry {
    static var instanceLock: Int32 = -1
    @MainActor static func main() {
        signal(SIGPIPE, SIG_IGN)
        let commandOnly = ["--capture-watchdog", "--check-default-photo", "--check-only"].contains { CommandLine.arguments.contains($0) }
        if !commandOnly {
            do { try LocalStore.prepare() } catch { exit(1) }
            instanceLock = open(LocalStore.session.deletingLastPathComponent().appendingPathComponent("instance.lock").path, O_CREAT | O_RDWR | O_CLOEXEC, 0o600)
            if instanceLock < 0 || flock(instanceLock, LOCK_EX | LOCK_NB) != 0 {
                let alert = NSAlert(); alert.messageText = "光盘打卡已经运行"; alert.informativeText = "请使用已打开的窗口，避免重复提交。"; alert.runModal(); exit(1)
            }
        }
        if !CommandLine.arguments.contains("--capture-watchdog"), !CommandLine.arguments.contains("--check-default-photo"), !CommandLine.arguments.contains("--check-only") {
            do { try MacCaptureRecovery.restore() }
            catch {
                let alert = NSAlert(); alert.messageText = "临时采集设置尚未恢复"
                alert.informativeText = "请允许系统恢复授权，然后重新启动工具。未开启自动计划。"
                alert.runModal(); exit(1)
            }
        }
        if CommandLine.arguments.contains("--import-legacy-folder") {
            do {
                guard CommandLine.arguments.count == 3 else { throw PlateError.message("需提供旧版数据目录。") }
                try LocalStore.importLegacy(URL(fileURLWithPath: CommandLine.arguments[2], isDirectory: true))
                print("Legacy session encrypted and scheduling records migrated locally. No network request sent.")
                exit(0)
            } catch { print("Local migration failed: " + error.localizedDescription); exit(1) }
        } else if CommandLine.arguments.contains("--check-default-photo") {
            do {
                let data = try Data(contentsOf: DefaultPhoto.url())
                guard data.starts(with: [137,80,78,71,13,10,26,10]), NSImage(data: data) != nil else {
                    throw PlateError.message("内置照片不是有效 PNG。")
                }
                print("Bundled default PNG verified: \(data.count) bytes. No network request was sent.")
                exit(0)
            } catch { print("Default photo verification failed: \(error.localizedDescription)"); exit(1) }
        } else if CommandLine.arguments.contains("--check-only") {
            Task {
                do {
                    _ = try await PlateAPI(LocalStore.load()).user()
                    print("Direct authenticated GET succeeded; account data parsed. No upload or check-in was sent.")
                    exit(0)
                } catch { print("Read-only verification failed: \(error.localizedDescription)"); exit(1) }
            }
            dispatchMain()
        } else if CommandLine.arguments.contains("--capture-watchdog") {
            MacCaptureRecovery.watchdog(arguments: CommandLine.arguments); exit(0)
        } else { PlateApp.main() }
    }
}
