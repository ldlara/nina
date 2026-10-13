import SwiftUI
import NinaCore

@MainActor
struct RootView: View {
    let container: AppContainer
    @Bindable private var session: AppSessionViewModel
    @Binding var pendingInviteToken: String?

    init(container: AppContainer, pendingInviteToken: Binding<String?>) {
        self.container = container
        self.session = container.session
        self._pendingInviteToken = pendingInviteToken
    }

    var body: some View {
        Group {
            switch session.route {
            case .onboarding:
                OnboardingView(onStart: { session.finishOnboarding(hasAccount: false) },
                               onHaveAccount: { session.finishOnboarding(hasAccount: true) })
            case .auth(let mode):
                AuthScreen(container: container, initialMode: mode)
            case .verifyEmail(let pending):
                EmailVerificationScreen(container: container, pending: pending)
            case .main:
                MainView(container: container, pendingInviteToken: $pendingInviteToken)
            }
        }
        .ninaScreenBackground()
        .task { await session.start() }
    }
}
