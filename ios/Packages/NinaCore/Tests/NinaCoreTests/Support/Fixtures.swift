import Foundation
@testable import NinaCore

enum Fixtures {
    static let userId = UUID(uuidString: "3C1C0D0E-5B61-4D6E-8A3A-0A0C5D1D9F01")!
    static let babyId = UUID(uuidString: "7D0A5C9E-1C0B-4D3A-B5F4-2F9D0E6A8B21")!
    static let deviceId = UUID(uuidString: "6F1D1C5E-4A3B-4F0E-9A52-0B5B3C8A7E11")!

    static let userJSON = """
    {"id":"3c1c0d0e-5b61-4d6e-8a3a-0a0c5d1d9f01","email":"ana@example.org","email_verified":true,
     "display_name":"Ana","locale":"pt-BR","timezone":"America/Sao_Paulo","status":"ACTIVE",
     "has_password":true,"identities":[{"provider":"APPLE","linked_at":"2026-10-08T17:00:00Z"}],
     "created_at":"2026-10-08T17:00:00Z"}
    """

    static func tokenJSON(access: String = "access-2", refresh: String = "refresh-2", expiresIn: Int = 900) -> String {
        """
        {"token_type":"Bearer","access_token":"\(access)","expires_in":\(expiresIn),"refresh_token":"\(refresh)",
         "refresh_expires_at":"2026-11-07T18:00:00Z","session_id":"4a0d6c1e-2b3f-4c5d-8e9f-0a1b2c3d4e5f",
         "user":\(userJSON)}
        """
    }

    static let babyJSON = """
    {"id":"7d0a5c9e-1c0b-4d3a-b5f4-2f9d0e6a8b21","display_name":"Nina","birth_date":"2026-01-10",
     "due_date":"2026-01-24","sex":null,"timezone":"America/Sao_Paulo","photo_ref":null,"my_role":"OWNER",
     "age":{"as_of":"2026-10-08","chronological":{"days":271,"weeks":38,"months":8},
            "corrected":{"days":257,"weeks":36,"months":8},"displayed":"CHRONOLOGICAL"},
     "age_calculation":{"chronological_days":271,"corrected_days":257,"correction_applied":true},
     "version":3,"created_at":"2026-10-08T17:00:00Z","updated_at":"2026-10-08T17:10:00.250Z"}
    """

    static let membershipJSON = """
    {"id":"11111111-1111-1111-1111-111111111111","baby_id":"7d0a5c9e-1c0b-4d3a-b5f4-2f9d0e6a8b21",
     "user":null,"invited_email":"avo@example.org","role":"CAREGIVER","status":"PENDING",
     "invited_at":"2026-10-08T17:00:00Z","invitation_expires_at":"2026-10-15T17:00:00Z","accepted_at":null}
    """

    static let referenceNow = Date(timeIntervalSince1970: 1_790_000_000) // 2026-09-21

    static func session(accessExpiresIn: TimeInterval = 600, now: Date = Date(),
                        access: String = "access-1", refresh: String = "refresh-1") -> StoredSession {
        StoredSession(accessToken: access, accessTokenExpiresAt: now.addingTimeInterval(accessExpiresIn),
                      refreshToken: refresh, refreshTokenExpiresAt: now.addingTimeInterval(86_400 * 30),
                      sessionId: UUID(), userId: userId)
    }

    static func baby(role: Role = .owner, id: UUID = babyId, name: String = "Nina",
                     birth: CivilDate = CivilDate(string: "2026-01-10")!, due: CivilDate? = nil,
                     version: Int = 3) -> Baby {
        Baby(id: id, displayName: name, birthDate: birth, dueDate: due, sex: nil,
             timezone: "America/Sao_Paulo", myRole: role, version: version,
             createdAt: Date(timeIntervalSince1970: 1_790_000_000), updatedAt: Date(timeIntervalSince1970: 1_790_000_000))
    }

    static func user() -> User {
        User(id: userId, email: "ana@example.org", emailVerified: true, displayName: "Ana", locale: "pt-BR",
             timezone: "America/Sao_Paulo", status: .active, hasPassword: true, identities: [],
             createdAt: Date(timeIntervalSince1970: 1_790_000_000))
    }

    static func membership(id: UUID = UUID(), role: Role = .caregiver, status: MembershipStatus = .active,
                           userId: UUID? = nil, name: String = "Ana") -> Membership {
        Membership(id: id, babyId: babyId, user: userId.map { UserRef(id: $0, displayName: name) },
                   invitedEmail: nil, role: role, status: status,
                   invitedAt: Date(timeIntervalSince1970: 1_790_000_000))
    }
}

/// Ambiente de cliente com rede stubada e tokens em memória.
struct TestEnvironment {
    let client: APIClient
    let tokens: InMemoryTokenStore
    let ended: EndedRecorder

    init(session: StoredSession? = Fixtures.session(), now: @escaping @Sendable () -> Date = { Date() }) {
        let tokens = InMemoryTokenStore(session: session)
        let ended = EndedRecorder()
        self.tokens = tokens
        self.ended = ended
        self.client = APIClient(
            baseURL: URL(string: "https://api.test.invalid/v1")!,
            transport: URLSessionTransport(session: StubServer.makeSession()),
            tokenStore: tokens,
            deviceProvider: StaticDeviceInfoProvider(info: DeviceInfo(deviceId: Fixtures.deviceId, platform: .ios,
                                                                      deviceLabel: "Test", appVersion: "1.0.0")),
            now: now,
            onSessionEnded: { ended.record($0) })
    }
}

final class EndedRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var reasons: [SessionEndReason] = []
    func record(_ reason: SessionEndReason) { lock.lock(); reasons.append(reason); lock.unlock() }
    var all: [SessionEndReason] { lock.lock(); defer { lock.unlock() }; return reasons }
}
