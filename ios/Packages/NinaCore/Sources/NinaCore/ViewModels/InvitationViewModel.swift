import Foundation
import Observation

/// Extrai o token de convite de um link (`nina://invite?token=...` ou `https://.../invite?token=...`).
/// O token só é lido; nunca é registrado em log.
public enum InvitationLink {
    public static func token(from url: URL) -> String? {
        guard let components = URLComponents(url: url, resolvingAgainstBaseURL: false) else { return nil }
        let path = components.path
        let isInviteHost = components.host == "invite"
        guard isInviteHost || path.hasSuffix("/invite") || path == "/invite" else { return nil }
        let value = components.queryItems?.first { $0.name == "token" }?.value
        guard let value, !value.isEmpty else { return nil }
        return value
    }
}

/// Fluxo do convidado (UX 4.7): prévia do convite, aceitar ou recusar.
@Observable
@MainActor
public final class InvitationViewModel {
    public var token: String
    public private(set) var preview: InvitationPreview?
    public private(set) var isBusy = false
    public private(set) var message: UserMessage?
    public private(set) var didDecline = false

    @ObservationIgnored private let repository: CaregiverRepository
    @ObservationIgnored private let onAccepted: @MainActor (Baby) -> Void

    public init(token: String = "", repository: CaregiverRepository,
                onAccepted: @escaping @MainActor (Baby) -> Void) {
        self.token = token
        self.repository = repository
        self.onAccepted = onAccepted
    }

    public var trimmedToken: String { token.trimmingCharacters(in: .whitespacesAndNewlines) }
    public var canInspect: Bool { !trimmedToken.isEmpty && !isBusy }

    public func inspect() async {
        guard canInspect else { return }
        isBusy = true
        message = nil
        preview = nil
        defer { isBusy = false }
        do {
            preview = try await repository.inspectInvitation(token: trimmedToken)
        } catch let error as APIError where error.problemCode == ProblemCode.notFound {
            // 404 uniforme: inválido, expirado ou de outro e-mail.
            message = UserMessage("error.invitation_invalid")
        } catch {
            message = ErrorMapper.message(for: error)
        }
    }

    public func accept() async {
        guard preview != nil, !isBusy else { return }
        isBusy = true
        message = nil
        defer { isBusy = false }
        do {
            let baby = try await repository.acceptInvitation(token: trimmedToken)
            onAccepted(baby)
        } catch {
            message = ErrorMapper.message(for: error)
        }
    }

    public func decline() async {
        guard preview != nil, !isBusy else { return }
        isBusy = true
        message = nil
        defer { isBusy = false }
        do {
            try await repository.declineInvitation(token: trimmedToken)
            preview = nil
            didDecline = true
        } catch {
            message = ErrorMapper.message(for: error)
        }
    }
}
