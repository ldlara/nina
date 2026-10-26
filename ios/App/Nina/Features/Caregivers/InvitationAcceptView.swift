import SwiftUI
import NinaCore

@MainActor
struct InvitationAcceptScreen: View {
    let container: AppContainer
    @State private var viewModel: InvitationViewModel
    @Environment(\.dismiss) private var dismiss

    init(container: AppContainer, token: String) {
        self.container = container
        let babies = container.babies
        _viewModel = State(initialValue: InvitationViewModel(token: token, repository: container.caregiverRepository,
                                                            onAccepted: { babies.didSave($0) }))
    }

    var body: some View {
        NavigationStack {
            InvitationAcceptView(viewModel: viewModel, autoInspect: !viewModel.trimmedToken.isEmpty, onDone: { dismiss() })
                .navigationTitle(Text("invitation.title"))
                .navigationBarTitleDisplayMode(.inline)
                .toolbar { ToolbarItem(placement: .cancellationAction) { Button("common.close") { dismiss() } } }
                .ninaScreenBackground()
        }
    }
}

/// Fluxo do convidado (ux-spec 4.7): quem convidou, papel e o que fica visível; Aceitar ou Recusar.
/// O nome completo do bebê não aparece antes do aceite (só o rótulo reduzido que a API devolve).
struct InvitationAcceptView: View {
    @Bindable var viewModel: InvitationViewModel
    let autoInspect: Bool
    let onDone: () -> Void

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: NinaMetrics.space5) {
                if let message = viewModel.message { MessageBanner(message: message) }

                if viewModel.didDecline {
                    MessageBanner(message: UserMessage("invitation.declined"), style: .info)
                    Button("common.close", action: onDone).buttonStyle(.nina(.secondary))
                } else if let preview = viewModel.preview {
                    previewCard(preview)
                    Button {
                        Task {
                            await viewModel.accept()
                            if viewModel.message == nil { onDone() }
                        }
                    } label: {
                        Text("invitation.accept")
                    }
                    .buttonStyle(.nina(.primary, isLoading: viewModel.isBusy))
                    .disabled(viewModel.isBusy)
                    Button("invitation.decline") { Task { await viewModel.decline() } }
                        .buttonStyle(.nina(.secondary))
                        .disabled(viewModel.isBusy)
                } else {
                    Text("invitation.enter_hint").font(.ninaBody).foregroundStyle(Color(.textSecondary))
                    NinaTextField(title: "invitation.field.token", text: $viewModel.token, submitLabel: .go,
                                  onSubmit: { Task { await viewModel.inspect() } })
                    Button("invitation.inspect") { Task { await viewModel.inspect() } }
                        .buttonStyle(.nina(.primary, isLoading: viewModel.isBusy))
                        .disabled(!viewModel.canInspect)
                }
            }
            .padding(NinaMetrics.gutter)
        }
        .task { if autoInspect { await viewModel.inspect() } }
    }

    private func previewCard(_ preview: InvitationPreview) -> some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space3) {
            Text("invitation.preview \(preview.inviterDisplayName) \(preview.babyLabel)")
                .font(.ninaTitle2).foregroundStyle(Color(.textPrimary))
                .accessibilityAddTraits(.isHeader)
            Text("invitation.role \(NSLocalizedString(preview.role.previewRoleKey, comment: ""))")
                .font(.ninaBody).foregroundStyle(Color(.textPrimary))
            Text(preview.role.descriptionKey).font(.ninaCallout).foregroundStyle(Color(.textSecondary))
            Text("invitation.expires \(preview.expiresAt.formatted(date: .abbreviated, time: .omitted))")
                .font(.ninaCaption).foregroundStyle(Color(.textSecondary))
            Text("invitation.visible_hint").font(.ninaCallout).foregroundStyle(Color(.textSecondary))
        }
        .ninaCard()
        .accessibilityElement(children: .combine)
    }
}

private extension InvitableRole {
    var previewRoleKey: String {
        switch self {
        case .caregiver: return "role.caregiver"
        case .readOnly: return "role.read_only"
        case .unknown: return "role.unknown"
        }
    }
}
