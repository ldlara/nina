import Foundation
import UIKit
import NinaCore

struct AppConfig {
    let apiBaseURL: URL
    let enableStubGoogle: Bool

    /// `NinaAPIBaseURL` vem do build setting `API_BASE_URL` (project.yml). O domínio é placeholder no contrato.
    static func load(bundle: Bundle = .main) -> AppConfig {
        let raw = bundle.object(forInfoDictionaryKey: "NinaAPIBaseURL") as? String ?? ""
        let url = URL(string: raw) ?? URL(string: "https://api.staging.nina.app/v1")!
        #if DEBUG
        return AppConfig(apiBaseURL: url, enableStubGoogle: true)
        #else
        return AppConfig(apiBaseURL: url, enableStubGoogle: false)
        #endif
    }
}

enum AppDeviceInfo {
    private static let deviceIdKey = "device.id"

    /// `device_id` estável por instalação; rótulo genérico (modelo), sem identificadores de hardware.
    @MainActor
    static func make(defaults: UserDefaults = .standard, bundle: Bundle = .main) -> DeviceInfo {
        let id: UUID
        if let stored = defaults.string(forKey: deviceIdKey), let uuid = UUID(uuidString: stored) {
            id = uuid
        } else {
            id = UUID()
            defaults.set(id.uuidString, forKey: deviceIdKey)
        }
        let device = UIDevice.current
        return DeviceInfo(deviceId: id, platform: .ios, deviceLabel: String(device.model.prefix(80)),
                          appVersion: bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String,
                          osVersion: device.systemVersion)
    }
}

/// Preferências simples em UserDefaults (onboarding visto etc.).
struct UserDefaultsPreferenceStore: PreferenceStore, @unchecked Sendable {
    private let defaults: UserDefaults
    init(defaults: UserDefaults = .standard) { self.defaults = defaults }
    func bool(forKey key: String) -> Bool { defaults.bool(forKey: key) }
    func set(_ value: Bool, forKey key: String) { defaults.set(value, forKey: key) }
}
