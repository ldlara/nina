import SwiftUI
import NinaCore

@MainActor
struct MoreView: View {
    let container: AppContainer
    @Bindable var babies: BabiesViewModel
    @Binding var pendingInviteToken: String?
    @AppStorage(ThemePreference.storageKey) private var themeRaw = ThemePreference.system.rawValue
    @State private var showingInviteEntry = false
    @State private var confirmingLogout = false

    var body: some View {
        NavigationStack {
            List {
                if let baby = babies.selectedBaby, let userId = container.session.currentUser?.id {
                    Section {
                        NavigationLink {
                            CaregiversScreen(container: container, baby: baby, currentUserId: userId,
                                             onLeftBaby: { babies.didLeave(babyId: baby.id) })
                        } label: {
                            Label("more.caregivers", systemImage: "person.2")
                        }
                    }
                }

                Section {
                    Button { showingInviteEntry = true } label: { Label("more.accept_invite", systemImage: "envelope.open") }
                }

                Section("more.appearance") {
                    Picker("more.theme", selection: $themeRaw) {
                        ForEach(ThemePreference.allCases) { Text($0.titleKey).tag($0.rawValue) }
                    }
                }

                Section("more.account") {
                    if let user = container.session.currentUser {
                        VStack(alignment: .leading, spacing: NinaMetrics.space1) {
                            Text(user.displayName ?? user.email).font(.ninaBodyStrong)
                            if user.displayName != nil { Text(user.email).font(.ninaCallout).foregroundStyle(Color(.textSecondary)) }
                        }
                        .accessibilityElement(children: .combine)
                    }
                    Button("more.logout", role: .destructive) { confirmingLogout = true }
                }
            }
            .scrollContentBackground(.hidden)
            .ninaScreenBackground()
            .navigationTitle(Text("tab.more"))
            .sheet(isPresented: $showingInviteEntry) { InvitationAcceptScreen(container: container, token: "") }
            .confirmationDialog(Text("more.logout.title"), isPresented: $confirmingLogout, titleVisibility: .visible) {
                Button("more.logout", role: .destructive) { Task { await container.session.logout() } }
                Button("common.cancel", role: .cancel) {}
            } message: {
                Text("more.logout.message")
            }
        }
    }
}
