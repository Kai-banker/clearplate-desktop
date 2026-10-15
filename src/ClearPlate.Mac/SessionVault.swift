import Foundation
import CryptoKit
import Security

// Keep a successfully unlocked key only for this process. Failed/cancelled reads
// are never cached; the next user action can retry the normal Keychain prompt.
final class SessionKeyCache {
    private let lock = NSLock()
    private var cached: SymmetricKey?
    func get(load: () throws -> SymmetricKey) throws -> SymmetricKey {
        lock.lock(); defer { lock.unlock() }
        if let cached { return cached }
        let key = try load(); cached = key; return key
    }
}

enum SessionVault {
    struct Envelope: Codable { let version: Int; let sealed: Data }
    private static let cache = SessionKeyCache()
    static func key() throws -> SymmetricKey {
        try cache.get { try readKey() }
    }
    private static func readKey() throws -> SymmetricKey {
#if TESTING
        // Synthetic isolated tests never access the user's keychain.
        return SymmetricKey(data: Data(repeating: 0x11, count: 32))
#else
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: "org.clearplate.desktop.session-encryption",
            kSecAttrAccount as String: "local-session-key"]
        var lookup = query; lookup[kSecReturnData as String] = true; lookup[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(lookup as CFDictionary, &item)
        if status == errSecSuccess, let bytes = item as? Data, bytes.count == 32 { return SymmetricKey(data: bytes) }
        guard status == errSecItemNotFound else { throw PlateError.message("钥匙串密钥不可读取，请解锁登录钥匙串并允许本工具访问。") }
        let generated = SymmetricKey(size: .bits256)
        var added = query; added[kSecValueData as String] = generated.withUnsafeBytes { Data($0) }
        added[kSecAttrLabel as String] = "ClearPlate local session encryption"
        guard SecItemAdd(added as CFDictionary, nil) == errSecSuccess else { throw PlateError.message("无法保存会话加密密钥，原会话没有替换。") }
        return generated
#endif
    }
    static func encode(_ config: SessionConfig) throws -> Data {
        let sealed = try AES.GCM.seal(JSONEncoder().encode(config), using: key())
        guard let bytes = sealed.combined else { throw PlateError.message("会话加密失败。") }
        return try JSONEncoder().encode(Envelope(version: 1, sealed: bytes))
    }
    static func decode(_ bytes: Data) throws -> SessionConfig {
        if let envelope = try? JSONDecoder().decode(Envelope.self, from: bytes) {
            guard envelope.version == 1 else { throw PlateError.message("会话文件版本不受支持。") }
            return try JSONDecoder().decode(SessionConfig.self, from: AES.GCM.open(AES.GCM.SealedBox(combined: envelope.sealed), using: key()))
        }
        // Upgrade compatibility: the old local JSON remains readable; next save encrypts it.
        return try JSONDecoder().decode(SessionConfig.self, from: bytes)
    }
}
