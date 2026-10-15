import Foundation
import Security

final class MockHTTP: URLProtocol {
    static var mode = "ok", requests = 0
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        Self.requests += 1
        if Self.mode == "offline" { client?.urlProtocol(self, didFailWithError: URLError(.notConnectedToInternet)); return }
        let status = Self.mode == "401" ? 401 : Self.mode == "500" ? 500 : 200
        let token = request.value(forHTTPHeaderField: "x-token") ?? ""
        let id = Self.mode == "wrong" || token == "fake-other" ? "B" : "A"
        var data: [String: Any] = ["user_id": id, "nickname": "测试账号", "clearplate_num_success": 10, "today_clearplate_num": 2, "last_clearplate_clock_time": 1000]
        if Self.mode == "rotation", token == "fake-old" { data["token"] = "fake-new" }
        if Self.mode == "wrong-rotation", token == "fake-old" { data["token"] = "fake-other" }
        let code = Self.mode == "expired" ? 10003 : 10007
        let body = try! JSONSerialization.data(withJSONObject: ["code": code, "data": data])
        client?.urlProtocol(self, didReceive: HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: "HTTP/1.1", headerFields: nil)!, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: body); client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() { }
}

@main struct Checks {
    @MainActor static func main() async throws {
        var count = 0
        func check(_ good: Bool, _ label: String) { guard good else { fatalError(label) }; count += 1 }
        func date(_ value: String) -> Date { ISO8601DateFormatter().date(from: value)! }
        let cases: [(String, String?)] = [
            ("2026-10-15T07:59:59+08:00", nil), ("2026-10-15T08:00:00+08:00", "08:00"),
            ("2026-10-15T08:04:59+08:00", "08:00"), ("2026-10-15T08:05:00+08:00", nil),
            ("2026-10-15T12:00:00+08:00", "12:00"), ("2026-10-15T12:05:00+08:00", nil),
            ("2026-10-15T18:00:00+08:00", "18:00"), ("2026-10-15T18:04:59+08:00", "18:00"), ("2026-10-15T18:05:00+08:00", nil),
            ("2026-10-15T00:00:00Z", "08:00")]
        for (time, expected) in cases { check(PlateSchedule.dueSlot(at: date(time)) == expected, "schedule boundary") }
        check(PlateSchedule.key("08:00", at: date("2026-10-15T08:00:00+08:00")) != PlateSchedule.key("08:00", at: date("2026-10-16T08:00:00+08:00")), "day guard")
        let reference = date("2026-10-15T12:00:00+08:00")
        func status(last: Double, remaining: Int = 2) throws -> UserStatus {
            try UserStatus(["user_id": "A", "nickname": "测试", "clearplate_num_success": 10,
                "today_clearplate_num": remaining, "last_clearplate_clock_time": last])
        }
        check(try PreflightRules.reason(status(last: reference.addingTimeInterval(-7199).timeIntervalSince1970), now: reference) != nil, "two hour lower edge blocked")
        check(try PreflightRules.reason(status(last: reference.addingTimeInterval(-7200).timeIntervalSince1970), now: reference) == nil, "exactly two hours allowed")
        check(try PreflightRules.reason(status(last: reference.addingTimeInterval(-7201).timeIntervalSince1970), now: reference) == nil, "over two hours allowed")
        check(try PreflightRules.reason(status(last: 0, remaining: 0), now: reference) != nil, "daily quota blocks upload")
        check(try PreflightRules.reason(status(last: 0), now: reference) == nil, "no previous success allowed")
        let trust = MacCaptureRecovery.trustSettings() as NSArray
        check(trust.count == 1, "single scoped trust constraint")
        let constraint = trust[0] as! NSDictionary
        check(constraint[kSecTrustSettingsPolicyString] as? String == "clearplate-api.guangpanxingdong.com", "OS SSL hostname constraint explicit")
        check(constraint[kSecTrustSettingsAllowedError] == nil, "no ignored certificate errors")
        do {
            let mutex = try MacCaptureRecovery.RecoveryLock()
            check(mutex.closesOnExec, "recovery lock is not inherited by watchdog/helper")
            defer { withExtendedLifetime(mutex) {} }
            let path = MacCaptureRecovery.file.deletingLastPathComponent().appendingPathComponent("capture-recovery.lock")
            let other = open(path.path, O_RDWR)
            check(other >= 0 && flock(other, LOCK_EX | LOCK_NB) != 0, "recovery lock excludes concurrent capture/cleanup")
            close(other)
        }
        do {
            let mutex = try MacCaptureRecovery.RecoveryLock()
            defer { withExtendedLifetime(mutex) {} }
            count += 1
        }
        let original = ["HTTPEnable": 0, "HTTPProxy": "previous.example", "HTTPPort": 8080, "HTTPSEnable": 0, "Unrelated": 7, "ProxyAutoConfigEnable": 1] as [String: Any]
        let installed = MacCaptureRecovery.installed(port: 23456)
        var temporary = original; installed.forEach { temporary[$0.key] = $0.value }
        check(NSDictionary(dictionary: MacCaptureRecovery.merged(current: temporary, original: original, installed: installed)).isEqual(to: original), "restore whole owned snapshot")
        temporary["HTTPProxy"] = "user.example"; temporary["HTTPPort"] = 9000
        let userModified = MacCaptureRecovery.merged(current: temporary, original: original, installed: installed)
        check(userModified["HTTPProxy"] as? String == "user.example" && userModified["HTTPPort"] as? Int == 9000, "preserve user proxy replacement")
        check(userModified["HTTPSProxy"] == nil && userModified["HTTPSEnable"] as? Int == 0, "independent protocol restoration")
        temporary["ExceptionsList"] = ["user.example"]
        check(MacCaptureRecovery.merged(current: temporary, original: original, installed: installed)["ExceptionsList"] as? [String] == ["user.example"], "preserve user bypass list")
        var config = try LocalStore.emptyConfig().replacingToken("fake-old"); config.account_id = "A"; config.location = "0,0"
        for bad in ["", "code|:|temporary", "fake\r\nheader"] {
            do { _ = try config.replacingToken(bad); fatalError("invalid accepted") } catch { count += 1 }
        }
        let encoded = try SessionVault.encode(config)
        check(!String(decoding: encoded, as: UTF8.self).contains("fake-old"), "token ciphertext")
        check(try SessionVault.decode(encoded).token == "fake-old", "vault roundtrip")
        var tampered = try JSONDecoder().decode(SessionVault.Envelope.self, from: encoded).sealed
        tampered[tampered.startIndex] ^= 1
        do { _ = try SessionVault.decode(JSONEncoder().encode(SessionVault.Envelope(version: 1, sealed: tampered))); fatalError("tamper accepted") } catch { count += 1 }
        check(try SessionVault.decode(JSONEncoder().encode(config)).token == "fake-old", "legacy session readable")
        try LocalStore.save(config)
        check(try LocalStore.load().account_id == "A", "session persistence")
        let permissions = try FileManager.default.attributesOfItem(atPath: LocalStore.session.path)[.posixPermissions] as! NSNumber
        check(permissions.intValue == 0o600, "private session permissions")
        let api = PlateAPI(config, protocolClasses: [MockHTTP.self]); MockHTTP.mode = "rotation"
        _ = try await api.user()
        check(api.config.token == "fake-new" && MockHTTP.requests == 2, "rotation independently verified")
        MockHTTP.mode = "wrong"; let old = api.config.token
        do { _ = try await api.adoptToken("fake-other"); fatalError("wrong account accepted") } catch { count += 1 }
        check(api.config.token == old, "failed candidate preserves original")
        MockHTTP.mode = "wrong-rotation"; let wrong = PlateAPI(config, protocolClasses: [MockHTTP.self])
        _ = try await wrong.user(); check(wrong.config.token == "fake-old" && wrong.tokenSyncWarning != nil, "bad rotation warning")
        MockHTTP.mode = "ok"; var blank = LocalStore.emptyConfig()
        let fresh = PlateAPI(blank, protocolClasses: [MockHTTP.self])
        do { _ = try await fresh.adoptToken("fake-first"); fatalError("initial binding unconfirmed") } catch { count += 1 }
        _ = try await fresh.adoptToken("fake-first", confirmInitial: { $0.accountID == "A" })
        check(fresh.config.account_id == "A", "first binding requires confirmation")
        for mode in ["expired", "401"] {
            MockHTTP.mode = mode
            do { _ = try await api.user(); fatalError("expired accepted") }
            catch let error as PlateError { if case .authenticationRequired = error { count += 1 } else { throw error } }
        }
        MockHTTP.mode = "500"
        do { _ = try await api.user(); fatalError("500 accepted") }
        catch let error as PlateError { if case .authenticationRequired = error { fatalError("500 marked expired") }; count += 1 }
        check(PreflightRetry.isTransientNetwork(URLError(.notConnectedToInternet)), "offline retry classification")
        check(!PreflightRetry.isTransientNetwork(URLError(.serverCertificateUntrusted)), "TLS never offline retry")
        blank.location = "nan,0"; try LocalStore.save(blank)
        let model = PlateModel(); model.timer?.invalidate()
        do { _ = try model.currentConfig(); fatalError("NaN location accepted") } catch { count += 1 }
        model.location = "0,0"; model.needsLogin = true
        check(!model.allowsAttempt("08:00", key: PlateSchedule.key("08:00")), "expired pauses submission")
        model.needsLogin = false; model.capturing = true
        check(!model.allowsAttempt("08:00", key: PlateSchedule.key("08:00")), "capture pauses submission")
        model.capturing = false; model.confirmedSlots.insert(PlateSchedule.key("08:00"))
        check(!model.allowsAttempt("08:00", key: PlateSchedule.key("08:00")), "confirmed slot cannot submit")
        model.attemptedSlots.insert(PlateSchedule.key("12:00")); model.persist()
        let restart = PlateModel(); restart.timer?.invalidate()
        check(restart.attemptedSlots.contains(PlateSchedule.key("12:00")), "uncertain guard survives restart")
        try FileManager.default.removeItem(at: LocalStore.settings)
        try FileManager.default.createDirectory(at: LocalStore.settings, withIntermediateDirectories: false)
        model.confirmedSlots = []; model.attemptedSlots = []
        check(!model.persist() && model.storageFailed, "disk failure stops scheduling")
        check(!model.allowsAttempt("18:00", key: PlateSchedule.key("18:00")), "failed persistence prevents submissions")
        try FileManager.default.removeItem(at: LocalStore.settings)
        try FileManager.default.removeItem(at: LocalStore.session)
        let legacy = LocalStore.session.deletingLastPathComponent().appendingPathComponent("legacy", isDirectory: true)
        try FileManager.default.createDirectory(at: legacy, withIntermediateDirectories: true)
        try JSONEncoder().encode(config).write(to: legacy.appendingPathComponent("session.json"))
        let guarded: [String: Any] = ["attempted_slots": ["2026-10-15@12:00"], "schedule": false]
        try JSONSerialization.data(withJSONObject: guarded).write(to: legacy.appendingPathComponent("settings.json"))
        try LocalStore.importLegacy(legacy)
        check(try LocalStore.load().token == "fake-old", "migration preserves session")
        check(LocalStore.settingsData()["attempted_slots"] as? [String] == ["2026-10-15@12:00"], "migration preserves duplicate guard")
        check(!(try String(contentsOf: LocalStore.session, encoding: .utf8)).contains("fake-old"), "migration encrypts legacy token")
        do { try LocalStore.importLegacy(legacy); fatalError("migration overwrote data") } catch { count += 1 }
        try Data("invalid encrypted session".utf8).write(to: LocalStore.session)
        let locked = PlateModel(); locked.timer?.invalidate()
        check(locked.localSessionBlocked && !locked.allowsAttempt("18:00", key: PlateSchedule.key("18:00")), "unreadable local session pauses submissions")
        var recovered = config; recovered.group_id = "123"; recovered.location = "1,2"
        try LocalStore.save(recovered)
        _ = try locked.currentConfig(requireLocation: false)
        check(!locked.localSessionBlocked && locked.group == "123" && locked.location == "1,2", "session recovery keeps stored settings instead of overwriting with blank fields")
        print("Mac checks passed: \(count). Synthetic requests only; no system proxy/trust changes.")
    }
}
