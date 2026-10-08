import AuthenticationServices
import CryptoKit
import Foundation
import Security
import UIKit
import NinaCore

/// Sign in with Apple atrás de `SocialSignInProvider`. O `nonce` bruto vai ao backend; o hash SHA-256 vai à
/// Apple, que o embute no `id_token` (ADR-0007: o servidor confere o hash).
struct AppleSignInProvider: SocialSignInProvider {
    let provider: IdentityProvider = .apple

    func authenticate() async throws -> SocialCredential {
        let coordinator = await MainActor.run { AppleSignInCoordinator() }
        return try await coordinator.run()
    }
}

@MainActor
private final class AppleSignInCoordinator: NSObject, ASAuthorizationControllerDelegate,
                                            ASAuthorizationControllerPresentationContextProviding {
    private var continuation: CheckedContinuation<SocialCredential, Error>?
    private var rawNonce = ""

    func run() async throws -> SocialCredential {
        rawNonce = Self.randomNonce()
        let request = ASAuthorizationAppleIDProvider().createRequest()
        request.requestedScopes = [.fullName, .email]
        request.nonce = Self.sha256Hex(rawNonce)

        return try await withCheckedThrowingContinuation { continuation in
            self.continuation = continuation
            let controller = ASAuthorizationController(authorizationRequests: [request])
            controller.delegate = self
            controller.presentationContextProvider = self
            controller.performRequests()
        }
    }

    nonisolated func presentationAnchor(for controller: ASAuthorizationController) -> ASPresentationAnchor {
        MainActor.assumeIsolated {
            let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            return scenes.flatMap(\.windows).first { $0.isKeyWindow } ?? ASPresentationAnchor()
        }
    }

    nonisolated func authorizationController(controller: ASAuthorizationController,
                                             didCompleteWithAuthorization authorization: ASAuthorization) {
        MainActor.assumeIsolated {
            guard let credential = authorization.credential as? ASAuthorizationAppleIDCredential,
                  let tokenData = credential.identityToken,
                  let token = String(data: tokenData, encoding: .utf8) else {
                finish(.failure(SocialSignInError.failed("missing_identity_token")))
                return
            }
            finish(.success(SocialCredential(idToken: token, nonce: rawNonce,
                                             givenName: credential.fullName?.givenName,
                                             familyName: credential.fullName?.familyName)))
        }
    }

    nonisolated func authorizationController(controller: ASAuthorizationController, didCompleteWithError error: Error) {
        MainActor.assumeIsolated {
            if let authError = error as? ASAuthorizationError, authError.code == .canceled {
                finish(.failure(SocialSignInError.cancelled))
            } else {
                finish(.failure(SocialSignInError.failed(String(describing: (error as NSError).code))))
            }
        }
    }

    private func finish(_ result: Result<SocialCredential, Error>) {
        continuation?.resume(with: result)
        continuation = nil
    }

    private static func randomNonce(length: Int = 32) -> String {
        var bytes = [UInt8](repeating: 0, count: length)
        let status = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        precondition(status == errSecSuccess, "Falha ao gerar nonce seguro")
        return bytes.map { String(format: "%02x", $0) }.joined()
    }

    private static func sha256Hex(_ input: String) -> String {
        SHA256.hash(data: Data(input.utf8)).map { String(format: "%02x", $0) }.joined()
    }
}
