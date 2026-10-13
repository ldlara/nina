import SwiftUI
import NinaCore

@MainActor
struct HomeView: View {
    let container: AppContainer
    @Bindable var babies: BabiesViewModel
    @Bindable var tracking: TrackingViewModel
    @State private var editingBaby: Baby?
    @State private var addingBaby = false
    @State private var sheet: TrackingSheet?
    @State private var confirmingCancelSleep = false

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: NinaMetrics.space5) {
                    SyncStatusBar(tracking: tracking)
                    if babies.isShowingCachedData { MessageBanner(message: UserMessage("home.offline_cache"), style: .warning) }
                    if case .failed(let message) = babies.state { MessageBanner(message: message) }
                    if let banner = tracking.banner { MessageBanner(message: banner) }

                    if babies.babies.count > 1 { babySelector }

                    if let baby = babies.selectedBaby {
                        BabyCard(baby: baby, onEdit: baby.myRole.isOwner ? { editingBaby = baby } : nil)
                    }
                    if tracking.shouldAskIfStillSleeping { stillSleepingPrompt }
                    NowCard(tracking: tracking, babyName: babies.selectedBaby?.displayName ?? "", onOpenTimer: { sheet = .sleepTimer })
                    TodayCard(tracking: tracking, onRecord: { sheet = .quickActions })
                    if !tracking.canWrite {
                        MessageBanner(message: UserMessage("tracking.read_only"), style: .info)
                    }
                    if case .failed(let message) = tracking.state { MessageBanner(message: message) }
                }
                .padding(.horizontal, NinaMetrics.gutter)
                .padding(.vertical, NinaMetrics.space4)
            }
            .refreshable { await babies.load(); await tracking.reload() }
            .ninaScreenBackground()
            .navigationTitle(Text("tab.home"))
            .toolbar {
                ToolbarItem(placement: .primaryAction) {
                    Button { addingBaby = true } label: { Label("baby.add", systemImage: "plus") }
                }
            }
            .safeAreaInset(edge: .bottom, spacing: 0) {
                // O aviso de "Desfazer" fica logo acima dos botões, qualquer que seja o tamanho do texto.
                VStack(spacing: NinaMetrics.space2) {
                    undoOverlay
                    actionBar
                }
            }
            .sheet(item: $sheet) { sheet in
                TrackingSheetContent(sheet: sheet, container: container, tracking: tracking, dismiss: { self.sheet = nil })
            }
            .sheet(item: $editingBaby) { baby in
                NavigationStack {
                    BabyFormScreen(container: container, mode: .edit(baby), onSaved: { saved in
                        babies.didSave(saved)
                        editingBaby = nil
                    })
                    .navigationTitle(Text("baby.edit.title"))
                    .navigationBarTitleDisplayMode(.inline)
                    .toolbar { ToolbarItem(placement: .cancellationAction) { Button("common.cancel") { editingBaby = nil } } }
                }
            }
            .sheet(isPresented: $addingBaby) {
                NavigationStack {
                    BabyFormScreen(container: container, mode: .create, onSaved: { saved in
                        babies.didSave(saved)
                        addingBaby = false
                    })
                    .navigationTitle(Text("baby.create.title"))
                    .navigationBarTitleDisplayMode(.inline)
                    .toolbar { ToolbarItem(placement: .cancellationAction) { Button("common.cancel") { addingBaby = false } } }
                }
            }
            .confirmationDialog(Text("sleep.cancel.title"), isPresented: $confirmingCancelSleep, titleVisibility: .visible) {
                Button("sleep.cancel.confirm", role: .destructive) { Task { await tracking.cancelOpenSleep() } }
                Button("common.cancel", role: .cancel) {}
            } message: {
                Text("sleep.cancel.message")
            }
        }
    }

    // MARK: Ações (zona do polegar)

    @ViewBuilder private var actionBar: some View {
        if tracking.canWrite {
            VStack(spacing: NinaMetrics.space2) {
                if tracking.openSleep == nil {
                    Button { Task { await tracking.startSleep() } } label: {
                        Label("sleep.start", systemImage: "moon.zzz.fill")
                    }
                    .buttonStyle(.nina(.primary))
                    .accessibilityHint(Text("a11y.sleep_start_hint"))
                    HStack(spacing: NinaMetrics.space3) {
                        wentBeforeMenu(title: "sleep.went_before", apply: { minutes in
                            Task { await tracking.startSleep(minutesAgo: minutes) }
                        }, custom: { sheet = .retroStart })
                        Button { sheet = .quickActions } label: { Label("home.record_other", systemImage: "plus") }
                            .buttonStyle(.nina(.secondary))
                    }
                } else {
                    Button { Task { await stopSleep() } } label: {
                        Label("sleep.stop", systemImage: "sun.max.fill")
                    }
                    .buttonStyle(.nina(.primary))
                    .accessibilityHint(Text("a11y.sleep_stop_hint"))
                    HStack(spacing: NinaMetrics.space3) {
                        wentBeforeMenu(title: "sleep.went_before_end", apply: { minutes in
                            Task { await stopSleep(minutesAgo: minutes) }
                        }, custom: { sheet = .retroStop })
                        Button { sheet = .quickActions } label: { Label("home.record_other", systemImage: "plus") }
                            .buttonStyle(.nina(.secondary))
                    }
                    Button("sleep.cancel.action", role: .destructive) { confirmingCancelSleep = true }
                        .buttonStyle(.nina(.text))
                }
            }
            .padding(.horizontal, NinaMetrics.gutter)
            .padding(.vertical, NinaMetrics.space3)
            .background(Color(.bgApp).opacity(0.97))
        }
    }

    private func stopSleep(minutesAgo: Int = 0, endAt: Date? = nil) async {
        if let stopped = await tracking.stopSleep(minutesAgo: minutesAgo, endAt: endAt) {
            sheet = .sleepStopped(stopped.id)
        }
    }

    private func wentBeforeMenu(title: LocalizedStringKey, apply: @escaping (Int) -> Void,
                                custom: @escaping () -> Void) -> some View {
        Menu {
            ForEach(RetroactiveShortcut.minutes, id: \.self) { minutes in
                Button(String(format: NSLocalizedString("sleep.minutes_ago %lld", comment: ""), minutes)) { apply(minutes) }
            }
            Button("sleep.pick_time", action: custom)
        } label: {
            Label(title, systemImage: "clock.arrow.circlepath")
                .font(.ninaBodyStrong)
                .frame(maxWidth: .infinity, minHeight: NinaMetrics.minTouchTarget)
                .overlay(RoundedRectangle(cornerRadius: NinaMetrics.radiusMedium)
                    .stroke(Color(.accentPrimary), lineWidth: 1.5))
        }
    }

    @ViewBuilder private var undoOverlay: some View {
        if let undo = tracking.undo {
            UndoToast(undo: undo, onUndo: { Task { await tracking.performUndo() } },
                      onDismiss: { tracking.dismissUndo() })
                .transition(.opacity)
        }
    }

    private var stillSleepingPrompt: some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space3) {
            Text("sleep.still_sleeping.title").ninaHeading()
            AdaptiveStack {
                Button("sleep.still_sleeping.yes") { tracking.dismissLongSleepPrompt() }
                    .buttonStyle(.nina(.secondary))
                Button("sleep.stop") { Task { await stopSleep() } }
                    .buttonStyle(.nina(.primary))
            }
        }
        .ninaCard()
    }

    private var babySelector: some View {
        Picker("home.baby_selector", selection: Binding(get: { babies.selectedBaby?.id ?? UUID() },
                                                         set: { babies.selectedBabyId = $0 })) {
            ForEach(babies.babies) { Text($0.displayName).tag($0.id) }
        }
        .pickerStyle(.menu)
        .frame(minHeight: NinaMetrics.minTouchTarget)
    }
}

struct BabyCard: View {
    let baby: Baby
    let onEdit: (() -> Void)?

    var body: some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space3) {
            AdaptiveStack {
                InitialAvatar(name: baby.displayName, size: 48)
                VStack(alignment: .leading, spacing: NinaMetrics.space1) {
                    Text(baby.displayName).font(.ninaTitle1).foregroundStyle(Color(.textPrimary))
                        .accessibilityAddTraits(.isHeader)
                    if let summary = AgeSummary(baby: baby) {
                        Text(AgeFormatting.describe(summary.chronological))
                            .font(.ninaBody).foregroundStyle(Color(.textPrimary))
                        if let corrected = summary.corrected {
                            // Idade corrigida é informação secundária e vem pronta da API (age_calculation).
                            Text(String(format: NSLocalizedString("age.corrected", comment: ""), AgeFormatting.describe(corrected)))
                                .font(.ninaCallout).foregroundStyle(Color(.textSecondary))
                        }
                    }
                }
            }
            .accessibilityElement(children: .combine)

            if let onEdit {
                Button(action: onEdit) { Label("baby.edit.action", systemImage: "pencil") }
                    .buttonStyle(.nina(.secondary))
            }
        }
        .ninaCard()
    }
}

enum AgeFormatting {
    /// "8 meses (38 semanas)", "5 semanas" ou "3 dias", com plural por idioma (stringsdict).
    static func describe(_ parts: AgeSummary.Parts) -> String {
        if parts.months >= 1 {
            let months = LocalizedMessage.plural("age.months", count: parts.months)
            let weeks = LocalizedMessage.plural("age.weeks", count: parts.weeks)
            return String(format: NSLocalizedString("age.months_weeks", comment: ""), months, weeks)
        }
        if parts.weeks >= 1 { return LocalizedMessage.plural("age.weeks", count: parts.weeks) }
        return LocalizedMessage.plural("age.days", count: parts.days)
    }
}
