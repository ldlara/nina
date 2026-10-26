import SwiftUI
import NinaCore

/// Linha do tempo por dia (UX 4.4): navegação de dia, chips de filtro, totais compactos e estados
/// vazio / erro / offline / sincronizando. Lê só do banco local (sem spinner de tela cheia, RNF-005).
struct TimelineScreen: View {
    let container: AppContainer
    @Bindable var tracking: TrackingViewModel
    @State private var sheet: TrackingSheet?

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: NinaMetrics.space4) {
                    SyncStatusBar(tracking: tracking)
                    if let banner = tracking.banner { MessageBanner(message: banner) }
                    if case .failed(let message) = tracking.state {
                        MessageBanner(message: message)
                        Button("common.retry") { Task { await tracking.reload() } }.buttonStyle(.nina(.secondary))
                    }
                    dayHeader
                    filterChips
                    daySummary
                    list
                }
                .padding(.horizontal, NinaMetrics.gutter)
                .padding(.vertical, NinaMetrics.space4)
            }
            .refreshable { await tracking.reload() }
            .ninaScreenBackground()
            .navigationTitle(Text("tab.timeline"))
            .toolbar {
                if tracking.canWrite {
                    ToolbarItem(placement: .primaryAction) {
                        Button { sheet = .quickActions } label: { Label("home.record_other", systemImage: "plus") }
                    }
                }
            }
            .overlay(alignment: .bottom) {
                if let undo = tracking.undo {
                    UndoToast(undo: undo, onUndo: { Task { await tracking.performUndo() } },
                              onDismiss: { tracking.dismissUndo() })
                        .padding(.bottom, NinaMetrics.space4)
                }
            }
            .sheet(item: $sheet) { sheet in
                TrackingSheetContent(sheet: sheet, container: container, tracking: tracking, dismiss: { self.sheet = nil })
            }
            .task { await tracking.reload() }
        }
    }

    // MARK: Dia

    private var dayHeader: some View {
        HStack(spacing: NinaMetrics.space2) {
            Button { Task { await tracking.shiftSelectedDay(by: -1) } } label: { Image(systemName: "chevron.left") }
                .frame(minWidth: NinaMetrics.minTouchTarget, minHeight: NinaMetrics.minTouchTarget)
                .accessibilityLabel(Text("timeline.previous_day"))
            Spacer(minLength: 0)
            Text(EventFormatting.dayTitle(tracking.selectedDay, today: tracking.today, timeZone: tracking.babyTimeZone))
                .ninaHeading()
                .multilineTextAlignment(.center)
            Spacer(minLength: 0)
            Button { Task { await tracking.shiftSelectedDay(by: 1) } } label: { Image(systemName: "chevron.right") }
                .frame(minWidth: NinaMetrics.minTouchTarget, minHeight: NinaMetrics.minTouchTarget)
                .disabled(tracking.isSelectedDayToday)
                .accessibilityLabel(Text("timeline.next_day"))
        }
    }

    private var filterChips: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: NinaMetrics.space2) {
                ForEach(TimelineFilter.allCases, id: \.self) { filter in
                    Button { tracking.filter = filter } label: {
                        Text(Self.filterTitle(filter))
                            .font(.ninaCallout.weight(tracking.filter == filter ? .semibold : .regular))
                            .padding(.horizontal, NinaMetrics.space4)
                            .frame(minHeight: NinaMetrics.minTouchTarget)
                            .foregroundStyle(Color(tracking.filter == filter ? NinaColorToken.textOnAccent : NinaColorToken.textPrimary))
                            .background(Color(tracking.filter == filter ? NinaColorToken.accentPrimary : NinaColorToken.bgSurface),
                                        in: Capsule())
                            .overlay(Capsule().stroke(Color(.accentPrimary), lineWidth: 1.5))
                    }
                    .buttonStyle(.plain)
                    .accessibilityAddTraits(tracking.filter == filter ? .isSelected : [])
                }
            }
        }
    }

    static func filterTitle(_ filter: TimelineFilter) -> LocalizedStringKey {
        switch filter {
        case .all: return "timeline.filter.all"
        case .sleep: return "timeline.filter.sleep"
        case .food: return "timeline.filter.food"
        case .diaper: return "timeline.filter.diaper"
        case .pumping: return "timeline.filter.pumping"
        }
    }

    @ViewBuilder private var daySummary: some View {
        let totals = tracking.selectedDaySummary
        if !totals.isEmpty {
            DaySummaryLine(summary: totals).ninaCard()
        }
    }

    // MARK: Lista

    @ViewBuilder private var list: some View {
        let events = tracking.visibleDayEvents
        if events.isEmpty {
            VStack(spacing: NinaMetrics.space3) {
                Image(systemName: "moon.stars").font(.largeTitle).foregroundStyle(Color(.textSecondary)).accessibilityHidden(true)
                Text(LocalizedStringKey(tracking.filter == .all ? "timeline.empty" : "timeline.empty_filtered"))
                    .font(.ninaBody).foregroundStyle(Color(.textSecondary)).multilineTextAlignment(.center)
                if tracking.canWrite && tracking.filter == .all {
                    Button("timeline.add") { sheet = .quickActions }.buttonStyle(.nina(.secondary))
                }
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, NinaMetrics.space8)
        } else {
            LazyVStack(alignment: .leading, spacing: 0) {
                ForEach(events) { event in
                    Button { sheet = .detail(event.id) } label: {
                        EventRow(event: event, fallbackTimeZone: tracking.babyTimeZone,
                                 currentUserId: container.session.currentUser?.id)
                    }
                    .buttonStyle(.plain)
                    Divider().overlay(Color(.borderSubtle))
                }
            }
        }
    }
}
