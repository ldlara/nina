import SwiftUI
import NinaCore

struct MainView: View {
    let container: AppContainer
    @Bindable private var babies: BabiesViewModel
    @Binding var pendingInviteToken: String?

    init(container: AppContainer, pendingInviteToken: Binding<String?>) {
        self.container = container
        self.babies = container.babies
        self._pendingInviteToken = pendingInviteToken
    }

    var body: some View {
        content
            .task { await babies.load() }
            .sheet(isPresented: Binding(get: { pendingInviteToken != nil },
                                        set: { if !$0 { pendingInviteToken = nil } })) {
                InvitationAcceptScreen(container: container, token: pendingInviteToken ?? "")
            }
    }

    @ViewBuilder private var content: some View {
        if babies.needsFirstBaby {
            NavigationStack {
                BabyFormScreen(container: container, mode: .create, onSaved: { babies.didSave($0) })
                    .navigationTitle(Text("baby.create.title"))
                    .navigationBarTitleDisplayMode(.inline)
            }
        } else if babies.babies.isEmpty {
            LoadingOrErrorView(state: babies.state, retry: { Task { await babies.load() } })
        } else {
            TabView {
                HomeView(container: container, babies: babies)
                    .tabItem { Label("tab.home", systemImage: "moon.stars") }
                MoreView(container: container, babies: babies, pendingInviteToken: $pendingInviteToken)
                    .tabItem { Label("tab.more", systemImage: "ellipsis.circle") }
            }
        }
    }
}

private struct LoadingOrErrorView: View {
    let state: LoadState
    let retry: () -> Void

    var body: some View {
        VStack(spacing: NinaMetrics.space4) {
            if case .failed(let message) = state {
                MessageBanner(message: message)
                Button("common.retry", action: retry).buttonStyle(.nina(.primary))
            } else {
                ProgressView()
                Text("common.loading").font(.ninaCallout).foregroundStyle(Color(.textSecondary))
            }
        }
        .padding(NinaMetrics.gutter)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .accessibilityElement(children: .combine)
    }
}
