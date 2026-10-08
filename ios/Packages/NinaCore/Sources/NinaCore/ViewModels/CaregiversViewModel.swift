import Foundation
import Observation

@Observable
@MainActor
public final class CaregiversViewModel {
    public private(set) var memberships: [Membership] = []
    public private(set) var state: LoadState = .idle
    public private(set) var isBusy = false
    public private(set) var banner: UserMessage?
    public private(set) var inviteFieldError: UserMessage?

    public var inviteEmail = ""
    public var inviteRole: InvitableRole = .caregiver

    public let baby: Baby

    @ObservationIgnored private let repository: CaregiverRepository
    @ObservationIgnored private let currentUserId: UUID
    @ObservationIgnored private var inviteKey = UUID()
    @ObservationIgnored private let onLeftBaby: @MainActor () -> Void

    public init(baby: Baby, currentUserId: UUID, repository: CaregiverRepository,
                onLeftBaby: @escaping @MainActor () -> Void = {}) {
        self.baby = baby
        self.currentUserId = currentUserId
        self.repository = repository
        self.onLeftBaby = onLeftBaby
    }

    /// Owner convida, reenvia, cancela, remove e altera papéis (UX 4.7).
    public var canManage: Bool { baby.myRole.isOwner }

    public var activeMembers: [Membership] {
        memberships.filter { $0.status == .active }.sorted(by: Self.order)
    }
    public var pendingInvitations: [Membership] {
        memberships.filter { $0.status == .pending }.sorted(by: Self.order)
    }

    private static func order(_ a: Membership, _ b: Membership) -> Bool {
        let rank: (Role) -> Int = { role in
            switch role {
            case .owner: return 0
            case .caregiver: return 1
            case .readOnly: return 2
            case .unknown: return 3
            }
        }
        if rank(a.role) != rank(b.role) { return rank(a.role) < rank(b.role) }
        return a.invitedAt < b.invitedAt
    }

    public func isCurrentUser(_ membership: Membership) -> Bool { membership.user?.id == currentUserId }

    /// Owner remove não-Owner; qualquer um sai do próprio vínculo, exceto o Owner (precisa transferir a propriedade).
    public func canRemove(_ membership: Membership) -> Bool {
        if membership.role == .owner { return false }
        if canManage { return true }
        return isCurrentUser(membership)
    }

    public func canChangeRole(_ membership: Membership) -> Bool {
        canManage && membership.role != .owner && membership.status == .active
    }

    public func load() async {
        state = .loading
        do {
            memberships = try await repository.caregivers(babyId: baby.id)
            state = .loaded
        } catch {
            state = .failed(ErrorMapper.message(for: error))
        }
    }

    @discardableResult
    public func invite() async -> Bool {
        banner = nil
        inviteFieldError = nil
        guard canManage else {
            banner = UserMessage("error.forbidden_role")
            return false
        }
        let email = inviteEmail.trimmingCharacters(in: .whitespacesAndNewlines)
        guard Validators.isPlausibleEmail(email) else {
            inviteFieldError = UserMessage("validation.email_invalid")
            return false
        }
        isBusy = true
        defer { isBusy = false }
        do {
            let created = try await repository.invite(babyId: baby.id, email: email, role: inviteRole,
                                                      idempotencyKey: inviteKey)
            memberships.append(created)
            inviteEmail = ""
            inviteKey = UUID()
            return true
        } catch {
            banner = ErrorMapper.message(for: error)
            if let message = ErrorMapper.fieldMessages(for: error)["email"] { inviteFieldError = message }
            return false
        }
    }

    public func resend(_ membership: Membership) async {
        await mutate { [self] in
            let updated = try await repository.resendInvitation(babyId: baby.id, membershipId: membership.id)
            replace(updated)
        }
    }

    public func changeRole(_ membership: Membership, to role: InvitableRole) async {
        await mutate { [self] in
            let updated = try await repository.changeRole(babyId: baby.id, membershipId: membership.id, role: role)
            replace(updated)
        }
    }

    /// Remove cuidador, cancela convite pendente ou (próprio vínculo) sai do bebê.
    public func remove(_ membership: Membership) async {
        guard canRemove(membership) else { return }
        let leaving = isCurrentUser(membership)
        await mutate { [self] in
            try await repository.remove(babyId: baby.id, membershipId: membership.id)
            memberships.removeAll { $0.id == membership.id }
            if leaving { onLeftBaby() }
        }
    }

    private func replace(_ membership: Membership) {
        if let index = memberships.firstIndex(where: { $0.id == membership.id }) {
            memberships[index] = membership
        } else {
            memberships.append(membership)
        }
    }

    private func mutate(_ work: () async throws -> Void) async {
        banner = nil
        isBusy = true
        defer { isBusy = false }
        do {
            try await work()
        } catch {
            banner = ErrorMapper.message(for: error)
        }
    }
}
