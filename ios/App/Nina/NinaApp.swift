import SwiftUI
import NinaCore

@main
struct NinaApp: App {
    @State private var container: AppContainer
    @State private var pendingInviteToken: String?
    @AppStorage(ThemePreference.storageKey) private var themeRaw = ThemePreference.system.rawValue

    init() {
        _container = State(initialValue: AppContainer())
    }

    private var theme: ThemePreference { ThemePreference(rawValue: themeRaw) ?? .system }

    var body: some Scene {
        WindowGroup {
            RootView(container: container, pendingInviteToken: $pendingInviteToken)
                .tint(Color(.accentPrimary))
                .preferredColorScheme(theme.colorScheme)
                .onOpenURL { url in
                    // nina://invite?token=... (o token só é lido; nunca é registrado em log).
                    if let token = InvitationLink.token(from: url) { pendingInviteToken = token }
                }
        }
    }
}
