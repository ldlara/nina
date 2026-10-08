import SwiftUI
import NinaCore

/// Conteúdo de cada folha do tracking. `dismiss` fecha a folha inteira.
struct TrackingSheetContent: View {
    let sheet: TrackingSheet
    let container: AppContainer
    @Bindable var tracking: TrackingViewModel
    let dismiss: () -> Void

    var body: some View {
        switch sheet {
        case .quickActions:
            QuickActionSheet(container: container, tracking: tracking, dismiss: dismiss)
        case .sleepTimer:
            NavigationStack { SleepTimerScreen(tracking: tracking, dismiss: dismiss) }
        case .retroStart:
            RetroTimeSheet(title: "sleep.retro_start.title", tracking: tracking, dismiss: dismiss) { date in
                await tracking.startSleep(startAt: date) != nil
            }
        case .retroStop:
            RetroTimeSheet(title: "sleep.retro_stop.title", tracking: tracking, dismiss: dismiss) { date in
                await tracking.stopSleep(endAt: date) != nil
            }
        case .sleepStopped(let id):
            NavigationStack { SleepStoppedSheet(eventId: id, tracking: tracking, dismiss: dismiss) }
        case .detail(let id):
            NavigationStack { EventDetailScreen(eventId: id, tracking: tracking, dismiss: dismiss) }
        case .form(let action):
            NavigationStack {
                QuickActionDestination(action: action, tracking: tracking, dismiss: dismiss)
            }
        }
    }
}

// MARK: - Ação rápida (UX 5.1): 5 botões grandes, ordem por uso recente

struct QuickActionSheet: View {
    let container: AppContainer
    @Bindable var tracking: TrackingViewModel
    let dismiss: () -> Void

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: NinaMetrics.space4) {
                    if let banner = tracking.banner { MessageBanner(message: banner) }
                    ForEach(tracking.quickActionOrder, id: \.self) { action in
                        if action == .diaper {
                            diaperTile
                        } else {
                            NavigationLink(value: action) { tile(action) }
                                .buttonStyle(.plain)
                        }
                    }
                }
                .padding(NinaMetrics.gutter)
            }
            .ninaScreenBackground()
            .navigationTitle(Text("quick.title"))
            .navigationBarTitleDisplayMode(.inline)
            .navigationDestination(for: QuickAction.self) { action in
                QuickActionDestination(action: action, tracking: tracking, dismiss: dismiss)
            }
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("common.close", action: dismiss) }
            }
        }
    }

    private func tile(_ action: QuickAction) -> some View {
        HStack(spacing: NinaMetrics.space3) {
            Image(systemName: QuickActionStyle.symbol(action))
                .font(.title2)
                .foregroundStyle(Color(QuickActionStyle.token(action)))
                .frame(width: 40)
                .accessibilityHidden(true)
            Text(QuickActionStyle.title(action)).font(.ninaBodyStrong).foregroundStyle(Color(.textPrimary))
            Spacer(minLength: 0)
            Image(systemName: "chevron.right").foregroundStyle(Color(.textSecondary)).accessibilityHidden(true)
        }
        .frame(minHeight: NinaMetrics.primaryTouchTarget)
        .ninaCard()
        .accessibilityElement(children: .combine)
    }

    /// Fralda em 2 toques: abrir a folha + tipo (UX 2.1). Os chips salvam direto, com "agora".
    private var diaperTile: some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space3) {
            HStack(spacing: NinaMetrics.space3) {
                Image(systemName: QuickActionStyle.symbol(.diaper))
                    .font(.title2)
                    .foregroundStyle(Color(QuickActionStyle.token(.diaper)))
                    .frame(width: 40)
                    .accessibilityHidden(true)
                Text(QuickActionStyle.title(.diaper)).ninaHeading()
            }
            AdaptiveStack {
                diaperButton(.wet)
                diaperButton(.dirty)
                diaperButton(.mixed)
            }
            NavigationLink(value: QuickAction.diaper) {
                Text("quick.diaper.more").font(.ninaCallout)
            }
            .frame(minHeight: NinaMetrics.minTouchTarget, alignment: .leading)
        }
        .ninaCard()
    }

    private func diaperButton(_ type: DiaperType) -> some View {
        Button(EventFormatting.name(type)) {
            Task {
                if await tracking.logDiaper(type) != nil { dismiss() }
            }
        }
        .buttonStyle(.nina(.secondary))
    }
}

enum QuickActionStyle {
    static func title(_ action: QuickAction) -> LocalizedStringKey {
        switch action {
        case .sleep: return "quick.sleep"
        case .breastfeeding: return "quick.breastfeeding"
        case .bottle: return "quick.bottle"
        case .diaper: return "quick.diaper"
        case .pumping: return "quick.pumping"
        }
    }

    static func symbol(_ action: QuickAction) -> String {
        switch action {
        case .sleep: return "moon.zzz.fill"
        case .breastfeeding: return "heart.fill"
        case .bottle: return "drop.fill"
        case .diaper: return "diamond.fill"
        case .pumping: return "arrow.triangle.2.circlepath"
        }
    }

    static func token(_ action: QuickAction) -> NinaColorToken {
        switch action {
        case .sleep: return .eventSleep
        case .breastfeeding, .bottle: return .eventFeeding
        case .diaper: return .eventDiaper
        case .pumping: return .eventPumping
        }
    }
}

/// Destino de cada ação: formulário ou timer de mamada.
struct QuickActionDestination: View {
    let action: QuickAction
    @Bindable var tracking: TrackingViewModel
    let dismiss: () -> Void

    var body: some View {
        switch action {
        case .breastfeeding:
            BreastfeedingTimerScreen(tracking: tracking, dismiss: dismiss)
        case .sleep:
            EventFormScreen(viewModel: EventFormViewModel(mode: .create(.sleep), tracking: tracking),
                            title: "form.sleep.title", dismiss: dismiss)
        case .bottle:
            EventFormScreen(viewModel: EventFormViewModel(mode: .create(.bottle), tracking: tracking),
                            title: "form.bottle.title", dismiss: dismiss)
        case .diaper:
            EventFormScreen(viewModel: EventFormViewModel(mode: .create(.diaper), tracking: tracking),
                            title: "form.diaper.title", dismiss: dismiss)
        case .pumping:
            EventFormScreen(viewModel: EventFormViewModel(mode: .create(.pumping), tracking: tracking),
                            title: "form.pumping.title", dismiss: dismiss)
        }
    }
}

// MARK: - Timer de sono (UX 5.2)

struct SleepTimerScreen: View {
    @Bindable var tracking: TrackingViewModel
    let dismiss: () -> Void
    @State private var confirmingCancel = false

    var body: some View {
        ScrollView {
            VStack(spacing: NinaMetrics.space5) {
                if let banner = tracking.banner { MessageBanner(message: banner) }
                if let open = tracking.openSleep {
                    timerBlock(open)
                    retroBlock
                    typeBlock(open)
                } else {
                    Text("sleep.timer.not_running").font(.ninaBody).foregroundStyle(Color(.textSecondary))
                }
            }
            .padding(NinaMetrics.gutter)
        }
        .ninaScreenBackground()
        .navigationTitle(Text("sleep.timer.title"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar { ToolbarItem(placement: .cancellationAction) { Button("common.close", action: dismiss) } }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            if tracking.openSleep != nil, tracking.canWrite {
                VStack(spacing: NinaMetrics.space2) {
                    Button { Task { if await tracking.stopSleep() != nil { dismiss() } } } label: {
                        Label("sleep.stop", systemImage: "sun.max.fill")
                    }
                    .buttonStyle(.nina(.primary))
                    Button("sleep.cancel.action", role: .destructive) { confirmingCancel = true }
                        .buttonStyle(.nina(.text))
                }
                .padding(.horizontal, NinaMetrics.gutter)
                .padding(.vertical, NinaMetrics.space3)
                .background(Color(.bgApp).opacity(0.97))
            }
        }
        .confirmationDialog(Text("sleep.cancel.title"), isPresented: $confirmingCancel, titleVisibility: .visible) {
            Button("sleep.cancel.confirm", role: .destructive) {
                Task { await tracking.cancelOpenSleep(); dismiss() }
            }
            Button("common.cancel", role: .cancel) {}
        } message: {
            Text("sleep.cancel.message")
        }
    }

    private func timerBlock(_ open: TrackedEvent) -> some View {
        TimelineView(.periodic(from: Date(), by: 1)) { context in
            let elapsed = max(0, context.date.timeIntervalSince(open.startAt))
            VStack(spacing: NinaMetrics.space2) {
                Text("now.sleeping").ninaHeading()
                Text(EventFormatting.clock(elapsed)).font(.ninaDisplay).foregroundStyle(Color(.textPrimary))
                Text(String(format: NSLocalizedString("now.since_type %@ %@", comment: ""),
                            EventFormatting.time(open.startAt, timeZone: open.timeZone ?? tracking.babyTimeZone),
                            EventFormatting.name(open.sleepType ?? .nap)))
                    .font(.ninaCallout).foregroundStyle(Color(.textSecondary))
            }
            .frame(maxWidth: .infinity)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(Text(String(format: NSLocalizedString("a11y.now_sleeping %@", comment: ""),
                                            EventFormatting.spokenDuration(elapsed))))
            .accessibilityAddTraits(.updatesFrequently)
        }
    }

    /// "Foi antes?" corrige o início: -5, -10, -15, -30 min.
    private var retroBlock: some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space2) {
            Text("sleep.start_correction").font(.ninaCallout).foregroundStyle(Color(.textSecondary))
            AdaptiveStack {
                ForEach(RetroactiveShortcut.minutes, id: \.self) { minutes in
                    Button(String(format: NSLocalizedString("sleep.minus_minutes %lld", comment: ""), minutes)) {
                        Task { await tracking.moveOpenSleepStart(earlierByMinutes: minutes) }
                    }
                    .buttonStyle(.nina(.secondary))
                }
            }
        }
    }

    private func typeBlock(_ open: TrackedEvent) -> some View {
        Picker("sleep.type", selection: Binding(get: { open.sleepType ?? .nap },
                                                set: { type in Task { await tracking.setOpenSleepType(type) } })) {
            Text(EventFormatting.name(.nap)).tag(SleepType.nap)
            Text(EventFormatting.name(.night)).tag(SleepType.night)
        }
        .pickerStyle(.segmented)
        .frame(minHeight: NinaMetrics.minTouchTarget)
    }
}

/// "Soneca registrada ✓": ajuste do fim (-5, -10, ...) e detalhes. O registro já foi salvo ao tocar "Acordou".
struct SleepStoppedSheet: View {
    let eventId: UUID
    @Bindable var tracking: TrackingViewModel
    let dismiss: () -> Void
    @State private var editing: TrackedEvent?

    private var event: TrackedEvent? { tracking.recentEvents.first { $0.id == eventId } }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: NinaMetrics.space5) {
                if let banner = tracking.banner { MessageBanner(message: banner) }
                if let event {
                    Label("sleep.saved", systemImage: "checkmark.circle.fill")
                        .font(.ninaTitle2).foregroundStyle(Color(.stateSuccess))
                    Text(EventFormatting.subtitle(for: event, fallback: tracking.babyTimeZone))
                        .font(.ninaBody).foregroundStyle(Color(.textPrimary))
                    if tracking.canWrite {
                        Text("sleep.end_correction").font(.ninaCallout).foregroundStyle(Color(.textSecondary))
                        AdaptiveStack {
                            ForEach(RetroactiveShortcut.minutes, id: \.self) { minutes in
                                Button(String(format: NSLocalizedString("sleep.minus_minutes %lld", comment: ""), minutes)) {
                                    guard let end = event.endAt else { return }
                                    var changes = EventChanges()
                                    changes.endAt = .set(end.addingTimeInterval(-Double(minutes) * 60))
                                    Task { await tracking.edit(event, changes: changes) }
                                }
                                .buttonStyle(.nina(.secondary))
                            }
                        }
                        Button("sleep.details") { editing = event }.buttonStyle(.nina(.secondary))
                    }
                } else {
                    Text("sleep.saved").font(.ninaBody)
                }
                Button("common.done", action: dismiss).buttonStyle(.nina(.primary))
            }
            .padding(NinaMetrics.gutter)
        }
        .ninaScreenBackground()
        .navigationTitle(Text("sleep.timer.title"))
        .navigationBarTitleDisplayMode(.inline)
        .sheet(item: $editing) { event in
            NavigationStack {
                EventFormScreen(viewModel: EventFormViewModel(mode: .edit(event), tracking: tracking),
                                title: "form.edit.title", dismiss: { editing = nil })
            }
        }
    }
}

// MARK: - "Foi antes…" com hora exata

struct RetroTimeSheet: View {
    let title: LocalizedStringKey
    @Bindable var tracking: TrackingViewModel
    let dismiss: () -> Void
    let confirm: (Date) async -> Bool

    @State private var date: Date

    init(title: LocalizedStringKey, tracking: TrackingViewModel, dismiss: @escaping () -> Void,
         confirm: @escaping (Date) async -> Bool) {
        self.title = title
        self.tracking = tracking
        self.dismiss = dismiss
        self.confirm = confirm
        _date = State(initialValue: tracking.currentTime.addingTimeInterval(-15 * 60))
    }

    var body: some View {
        NavigationStack {
            Form {
                if let banner = tracking.banner { MessageBanner(message: banner).listRowBackground(Color.clear) }
                DatePicker("form.time", selection: $date, in: ...tracking.currentTime,
                           displayedComponents: [.date, .hourAndMinute])
                    .environment(\.timeZone, tracking.babyTimeZone)
                    .frame(minHeight: NinaMetrics.minTouchTarget)
            }
            .navigationTitle(Text(title))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("common.cancel", action: dismiss) }
                ToolbarItem(placement: .confirmationAction) {
                    Button("common.save") { Task { if await confirm(date) { dismiss() } } }
                }
            }
        }
    }
}
