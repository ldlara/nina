import Foundation
@testable import NinaCore

final class FakeAuthRepository: AuthRepository, @unchecked Sendable {
    var stored = false
    var loginResult: Result<User, Error> = .success(Fixtures.user())
    var verifyResult: Result<User, Error> = .success(Fixtures.user())
    var registerResult: Result<VerificationPending, Error> = .success(VerificationPending(resendAfterSeconds: 60))
    var signInResults: [Result<User, Error>] = [.success(Fixtures.user())]
    private(set) var loginCalls: [(String, String)] = []
    private(set) var registerCalls: [RegisterRequest] = []
    private(set) var verifyCalls: [(String, String)] = []
    private(set) var signInConsents: [[ConsentAcceptance]?] = []
    private(set) var loggedOut = false

    func hasStoredSession() -> Bool { stored }
    func register(_ request: RegisterRequest) async throws -> VerificationPending {
        registerCalls.append(request)
        return try registerResult.get()
    }
    func verifyEmail(email: String, code: String) async throws -> User {
        verifyCalls.append((email, code))
        return try verifyResult.get()
    }
    func login(email: String, password: String) async throws -> User {
        loginCalls.append((email, password))
        return try loginResult.get()
    }
    func signIn(with credential: SocialCredential, provider: IdentityProvider, consents: [ConsentAcceptance]?,
                locale: String?, timezone: String?) async throws -> User {
        signInConsents.append(consents)
        let result = signInResults.count > 1 ? signInResults.removeFirst() : signInResults[0]
        return try result.get()
    }
    func requestPasswordReset(email: String) async throws {}
    func currentUser() async throws -> User { Fixtures.user() }
    func logout() async { loggedOut = true }
}

final class FakeConsentRepository: ConsentRepository, @unchecked Sendable {
    var documents: [LegalDocument] = FakeConsentRepository.defaultDocuments
    var recordError: Error?
    private(set) var recorded: [ConsentInput] = []

    static let defaultDocuments: [LegalDocument] = [
        LegalDocument(purposeKey: .termsOfUse, version: "1.2.0", url: URL(string: "https://nina.app/termos")!,
                      contentHash: nil, effectiveAt: Date(timeIntervalSince1970: 1_790_000_000), required: true),
        LegalDocument(purposeKey: .privacyPolicy, version: "1.1.0", url: URL(string: "https://nina.app/privacidade")!,
                      contentHash: nil, effectiveAt: Date(timeIntervalSince1970: 1_790_000_000), required: true)
    ]

    func legalDocuments() async throws -> [LegalDocument] { documents }

    func record(_ input: ConsentInput) async throws -> ConsentRecord {
        if let recordError { throw recordError }
        recorded.append(input)
        return ConsentRecord(id: UUID(), purposeKey: input.purposeKey, documentVersion: input.documentVersion,
                             status: "GRANTED", grantedAt: Date())
    }
}

final class FakeBabyRepository: BabyRepository, @unchecked Sendable {
    var cached: [Baby] = []
    var refreshResult: Result<BabyListResult, Error> = .success(BabyListResult(babies: [], isFromCache: false))
    var createResult: Result<Baby, Error>?
    var updateResult: Result<Baby, Error>?
    private(set) var created: [(BabyCreate, UUID)] = []
    private(set) var patches: [BabyUpdate] = []
    private(set) var cleared = false
    var events: [String] = []

    func cachedBabies() async -> [Baby] { cached }
    func refreshBabies() async throws -> BabyListResult { try refreshResult.get() }
    func createBaby(_ create: BabyCreate, idempotencyKey: UUID) async throws -> Baby {
        events.append("createBaby")
        created.append((create, idempotencyKey))
        return try (createResult ?? .success(Fixtures.baby(id: create.id ?? UUID(), name: create.displayName))).get()
    }
    func updateBaby(_ baby: Baby, patch: BabyUpdate) async throws -> Baby {
        patches.append(patch)
        return try (updateResult ?? .success(baby)).get()
    }
    func clearLocalData() async { cleared = true }
}

final class FakeCaregiverRepository: CaregiverRepository, @unchecked Sendable {
    var list: Result<[Membership], Error> = .success([])
    var inviteResult: Result<Membership, Error>?
    var removeError: Error?
    var previewResult: Result<InvitationPreview, Error> = .success(
        InvitationPreview(inviterDisplayName: "Ana", babyLabel: "N.", role: .caregiver,
                          expiresAt: Date(timeIntervalSince1970: 1_790_600_000)))
    var acceptResult: Result<Baby, Error> = .success(Fixtures.baby(role: .caregiver))
    private(set) var inviteKeys: [UUID] = []
    private(set) var removed: [UUID] = []
    private(set) var declined = false

    func caregivers(babyId: UUID) async throws -> [Membership] { try list.get() }
    func invite(babyId: UUID, email: String, role: InvitableRole, idempotencyKey: UUID) async throws -> Membership {
        inviteKeys.append(idempotencyKey)
        return try (inviteResult ?? .success(Membership(id: UUID(), babyId: babyId, invitedEmail: email,
                                                        role: role == .readOnly ? .readOnly : .caregiver, status: .pending,
                                                        invitedAt: Date()))).get()
    }
    func resendInvitation(babyId: UUID, membershipId: UUID) async throws -> Membership {
        Fixtures.membership(id: membershipId, status: .pending)
    }
    func changeRole(babyId: UUID, membershipId: UUID, role: InvitableRole) async throws -> Membership {
        Fixtures.membership(id: membershipId, role: role == .readOnly ? .readOnly : .caregiver)
    }
    func remove(babyId: UUID, membershipId: UUID) async throws {
        if let removeError { throw removeError }
        removed.append(membershipId)
    }
    func inspectInvitation(token: String) async throws -> InvitationPreview { try previewResult.get() }
    func acceptInvitation(token: String) async throws -> Baby { try acceptResult.get() }
    func declineInvitation(token: String) async throws { declined = true }
}
