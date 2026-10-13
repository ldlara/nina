import SwiftUI
import NinaCore

@MainActor
struct CaregiversScreen: View {
    @State private var viewModel: CaregiversViewModel

    init(container: AppContainer, baby: Baby, currentUserId: UUID, onLeftBaby: @escaping @MainActor () -> Void) {
        _viewModel = State(initialValue: CaregiversViewModel(baby: baby, currentUserId: currentUserId,
                                                            repository: container.caregiverRepository,
                                                            onLeftBaby: onLeftBaby))
    }

    var body: some View {
        CaregiversView(viewModel: viewModel)
    }
}

/// Lista de cuidadores e convites pendentes (ux-spec 4.7). Ações por linha ficam em menu e também como
/// ações personalizadas do VoiceOver (um foco por linha).
struct CaregiversView: View {
    @Bindable var viewModel: CaregiversViewModel
    @State private var showingInvite = false
    @State private var pendingRemoval: Membership?
    @State private var confirmingLeave = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: NinaMetrics.space5) {
                if let banner = viewModel.banner { MessageBanner(message: banner) }
                switch viewModel.state {
                case .loading where viewModel.memberships.isEmpty:
                    ProgressView().frame(maxWidth: .infinity).padding(.top, NinaMetrics.space8)
                case .failed(let message):
                    MessageBanner(message: message)
                    Button("common.retry") { Task { await viewModel.load() } }.buttonStyle(.nina(.secondary))
                default:
                    EmptyView()
                }

                section(title: "caregivers.active", members: viewModel.activeMembers)
                if !viewModel.pendingInvitations.isEmpty {
                    section(title: "caregivers.pending", members: viewModel.pendingInvitations)
                }

                if viewModel.canManage {
                    Button { showingInvite = true } label: { Label("caregivers.invite", systemImage: "person.badge.plus") }
                        .buttonStyle(.nina(.primary))
                } else if let mine = viewModel.memberships.first(where: viewModel.isCurrentUser), viewModel.canRemove(mine) {
                    Button("caregivers.leave") { confirmingLeave = true }
                        .buttonStyle(.nina(.destructive))
                        .confirmationDialog(Text("caregivers.leave.title"), isPresented: $confirmingLeave, titleVisibility: .visible) {
                            Button("caregivers.leave.confirm", role: .destructive) { Task { await viewModel.remove(mine) } }
                            Button("common.cancel", role: .cancel) {}
                        } message: {
                            Text("caregivers.leave.message")
                        }
                }
            }
            .padding(.horizontal, NinaMetrics.gutter)
            .padding(.vertical, NinaMetrics.space4)
        }
        .refreshable { await viewModel.load() }
        .ninaScreenBackground()
        .navigationTitle(Text("caregivers.title"))
        .task { await viewModel.load() }
        .sheet(isPresented: $showingInvite) { InviteSheet(viewModel: viewModel) }
        .confirmationDialog(Text("caregivers.remove.title"), isPresented: Binding(get: { pendingRemoval != nil },
                                                                                  set: { if !$0 { pendingRemoval = nil } }),
                            titleVisibility: .visible, presenting: pendingRemoval) { member in
            Button(member.status == .pending ? "caregivers.cancel_invite.confirm" : "caregivers.remove.confirm",
                   role: .destructive) { Task { await viewModel.remove(member) } }
            Button("common.cancel", role: .cancel) {}
        } message: { member in
            if member.status == .pending {
                Text("caregivers.cancel_invite.message")
            } else {
                Text("caregivers.remove.message \(displayName(member))")
            }
        }
    }

    @ViewBuilder
    private func section(title: LocalizedStringKey, members: [Membership]) -> some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space3) {
            Text(title).ninaHeading()
            if members.isEmpty {
                Text("caregivers.empty").font(.ninaCallout).foregroundStyle(Color(.textSecondary))
            }
            ForEach(members) { member in
                MemberRow(member: member, isMe: viewModel.isCurrentUser(member),
                          canResend: viewModel.canManage && member.status == .pending,
                          canRemove: viewModel.canRemove(member) && !(viewModel.isCurrentUser(member)),
                          canChangeRole: viewModel.canChangeRole(member),
                          onResend: { Task { await viewModel.resend(member) } },
                          onRemove: { pendingRemoval = member },
                          onChangeRole: { role in Task { await viewModel.changeRole(member, to: role) } })
            }
        }
    }

    private func displayName(_ member: Membership) -> String {
        member.user?.displayName ?? member.invitedEmail ?? ""
    }
}

private struct MemberRow: View {
    let member: Membership
    let isMe: Bool
    let canResend: Bool
    let canRemove: Bool
    let canChangeRole: Bool
    let onResend: () -> Void
    let onRemove: () -> Void
    let onChangeRole: (InvitableRole) -> Void

    private var name: String { member.user?.displayName ?? member.invitedEmail ?? "" }
    private var hasActions: Bool { canResend || canRemove || canChangeRole }

    var body: some View {
        HStack(alignment: .center, spacing: NinaMetrics.space3) {
            InitialAvatar(name: name, size: 44)
            VStack(alignment: .leading, spacing: NinaMetrics.space1) {
                Text(isMe ? "\(name) (\(NSLocalizedString("caregivers.you", comment: "")))" : name)
                    .font(.ninaBodyStrong).foregroundStyle(Color(.textPrimary))
                    .fixedSize(horizontal: false, vertical: true)
                Text(member.role.labelKey).font(.ninaCallout).foregroundStyle(Color(.textSecondary))
                Label {
                    statusText
                } icon: {
                    Image(systemName: member.status == .pending ? "clock" : "checkmark.circle")
                }
                .font(.ninaCaption).foregroundStyle(Color(.textSecondary))
            }
            Spacer(minLength: 0)
            if hasActions {
                Menu {
                    if canResend { Button("caregivers.resend", action: onResend) }
                    if canChangeRole {
                        ForEach(InvitableRole.allCases, id: \.self) { role in
                            Button(role.labelKey) { onChangeRole(role) }
                        }
                    }
                    if canRemove {
                        Button(member.status == .pending ? "caregivers.cancel_invite" : "caregivers.remove",
                               role: .destructive, action: onRemove)
                    }
                } label: {
                    Image(systemName: "ellipsis.circle")
                        .font(.title2)
                        .frame(minWidth: NinaMetrics.minTouchTarget, minHeight: NinaMetrics.minTouchTarget)
                }
                .accessibilityHidden(true)
            }
        }
        .ninaCard()
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(accessibilityDescription))
        .accessibilityActions {
            if canResend { Button("caregivers.resend", action: onResend) }
            if canChangeRole {
                ForEach(InvitableRole.allCases, id: \.self) { role in
                    Button(role.labelKey) { onChangeRole(role) }
                }
            }
            if canRemove {
                Button(member.status == .pending ? "caregivers.cancel_invite" : "caregivers.remove", action: onRemove)
            }
        }
    }

    @ViewBuilder private var statusText: some View {
        if member.status == .pending, let expires = member.invitationExpiresAt {
            Text("caregivers.status.pending_until \(expires.formatted(date: .abbreviated, time: .omitted))")
        } else {
            Text(member.status.labelKey)
        }
    }

    private var accessibilityDescription: String {
        let role = NSLocalizedString(member.role.labelKeyString, comment: "")
        let status = NSLocalizedString(member.status.labelKeyString, comment: "")
        return "\(name), \(role), \(status)"
    }
}

struct InviteSheet: View {
    @Bindable var viewModel: CaregiversViewModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: NinaMetrics.space5) {
                    if let banner = viewModel.banner { MessageBanner(message: banner) }
                    NinaTextField(title: "caregivers.invite.email", text: $viewModel.inviteEmail,
                                  error: viewModel.inviteFieldError, contentType: .emailAddress, keyboard: .emailAddress,
                                  submitLabel: .done)

                    Text("caregivers.invite.role").ninaHeading()
                    ForEach(InvitableRole.allCases, id: \.self) { role in
                        RoleChoice(role: role, isSelected: viewModel.inviteRole == role) { viewModel.inviteRole = role }
                    }
                    Text("caregivers.invite.expiry_hint").font(.ninaCaption).foregroundStyle(Color(.textSecondary))

                    Button {
                        Task { if await viewModel.invite() { dismiss() } }
                    } label: {
                        Text("caregivers.invite.send")
                    }
                    .buttonStyle(.nina(.primary, isLoading: viewModel.isBusy))
                    .disabled(viewModel.isBusy)
                }
                .padding(NinaMetrics.gutter)
            }
            .navigationTitle(Text("caregivers.invite"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button("common.cancel") { dismiss() } } }
            .ninaScreenBackground()
        }
    }
}

private struct RoleChoice: View {
    let role: InvitableRole
    let isSelected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(alignment: .top, spacing: NinaMetrics.space3) {
                Image(systemName: isSelected ? "largecircle.fill.circle" : "circle")
                    .font(.title3).foregroundStyle(Color(.accentPrimary)).accessibilityHidden(true)
                VStack(alignment: .leading, spacing: NinaMetrics.space1) {
                    Text(role.labelKey).font(.ninaBodyStrong).foregroundStyle(Color(.textPrimary))
                    Text(role.descriptionKey).font(.ninaCallout).foregroundStyle(Color(.textSecondary))
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 0)
            }
            .frame(minHeight: NinaMetrics.minTouchTarget, alignment: .top)
            .ninaCard()
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(isSelected ? [.isSelected] : [])
    }
}

// MARK: - Rótulos localizados

extension Role {
    var labelKeyString: String {
        switch self {
        case .owner: return "role.owner"
        case .caregiver: return "role.caregiver"
        case .readOnly: return "role.read_only"
        case .unknown: return "role.unknown"
        }
    }
    var labelKey: LocalizedStringKey { LocalizedStringKey(labelKeyString) }
}

extension InvitableRole {
    var labelKey: LocalizedStringKey {
        switch self {
        case .caregiver: return "role.caregiver"
        case .readOnly: return "role.read_only"
        case .unknown: return "role.unknown"
        }
    }
    var descriptionKey: LocalizedStringKey {
        switch self {
        case .caregiver: return "role.caregiver.description"
        case .readOnly: return "role.read_only.description"
        case .unknown: return "role.unknown"
        }
    }
}

extension MembershipStatus {
    var labelKeyString: String {
        switch self {
        case .active: return "membership.status.active"
        case .pending: return "membership.status.pending"
        case .revoked: return "membership.status.revoked"
        case .declined: return "membership.status.declined"
        case .expired: return "membership.status.expired"
        case .unknown: return "membership.status.unknown"
        }
    }
    var labelKey: LocalizedStringKey { LocalizedStringKey(labelKeyString) }
}
