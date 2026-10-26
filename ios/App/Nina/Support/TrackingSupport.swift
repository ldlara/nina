import Foundation
import NinaCore

/// Autor das escritas locais (`created_by`/`last_modified_by`). O repositório roda fora da main actor, então o
/// usuário atual passa por uma caixa com trava, atualizada quando a sessão muda. O servidor reatribui a autoria no sync.
final class CurrentActorBox: @unchecked Sendable {
    private let lock = NSLock()
    private var value: UserRef?

    var current: UserRef? {
        lock.lock(); defer { lock.unlock() }
        return value
    }

    func set(_ user: User?) {
        lock.lock(); defer { lock.unlock() }
        value = user.map { UserRef(id: $0.id, displayName: $0.displayName ?? "") }
    }
}

/// Timer de mamada em UserDefaults (sobrevive ao app fechado). Contém só lados e horários, nunca nome do bebê.
final class UserDefaultsFeedingTimerStore: FeedingTimerStore, @unchecked Sendable {
    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) { self.defaults = defaults }

    private func key(_ babyId: UUID) -> String { "feeding.timer." + babyId.uuidString.lowercased() }

    func load(babyId: UUID) -> BreastfeedingTimerState? {
        guard let data = defaults.data(forKey: key(babyId)) else { return nil }
        return try? NinaStorageJSON.makeDecoder().decode(BreastfeedingTimerState.self, from: data)
    }

    func save(_ state: BreastfeedingTimerState) {
        guard let data = try? NinaStorageJSON.makeEncoder().encode(state) else { return }
        defaults.set(data, forKey: key(state.babyId))
    }

    func clear(babyId: UUID) { defaults.removeObject(forKey: key(babyId)) }
}
