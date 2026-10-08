import SwiftUI
import NinaCore

/// Folhas (sheets) do tracking. Um único `sheet(item:)` por tela evita folhas empilhadas.
enum TrackingSheet: Identifiable, Hashable {
    case quickActions
    case sleepTimer
    case retroStart
    case retroStop
    case sleepStopped(UUID)
    case detail(UUID)
    case form(QuickAction)

    var id: String {
        switch self {
        case .quickActions: return "quick"
        case .sleepTimer: return "timer"
        case .retroStart: return "retro-start"
        case .retroStop: return "retro-stop"
        case .sleepStopped(let id): return "stopped-" + id.uuidString
        case .detail(let id): return "detail-" + id.uuidString
        case .form(let action): return "form-" + action.rawValue
        }
    }
}

// MARK: - Cartão "Agora"

struct NowCard: View {
    @Bindable var tracking: TrackingViewModel
    let babyName: String
    let onOpenTimer: () -> Void

    var body: some View {
        // Dormindo: atualiza a cada segundo. Acordado: a cada 30 s basta (minutos).
        TimelineView(.periodic(from: Date(), by: tracking.openSleep == nil ? 30 : 1)) { context in
            content(at: context.date)
        }
    }

    @ViewBuilder
    private func content(at date: Date) -> some View {
        if let open = tracking.openSleep {
            let elapsed = max(0, date.timeIntervalSince(open.startAt))
            Button(action: onOpenTimer) {
                VStack(alignment: .leading, spacing: NinaMetrics.space2) {
                    Text("now.title").font(.ninaCaption).foregroundStyle(Color(.textSecondary))
                    Text("now.sleeping").ninaHeading()
                    Text(EventFormatting.clock(elapsed)).font(.ninaDisplay).foregroundStyle(Color(.textPrimary))
                    Text(String(format: NSLocalizedString("now.since_type %@ %@", comment: ""),
                                EventFormatting.time(open.startAt, timeZone: open.timeZone ?? tracking.babyTimeZone),
                                EventFormatting.name(open.sleepType ?? .nap)))
                        .font(.ninaCallout).foregroundStyle(Color(.textSecondary))
                    if let starter = open.createdBy?.displayName, !starter.isEmpty {
                        Text(String(format: NSLocalizedString("now.started_by %@", comment: ""), starter))
                            .font(.ninaCallout).foregroundStyle(Color(.textSecondary))
                    }
                }
                .ninaCard()
            }
            .buttonStyle(.plain)
            .accessibilityElement(children: .ignore)
            // Falado em minutos: o cronômetro por segundo não é lido (evita anunciar a cada tick).
            .accessibilityLabel(Text(String(format: NSLocalizedString("a11y.now_sleeping %@", comment: ""),
                                            EventFormatting.spokenDuration(elapsed))))
            .accessibilityHint(Text("a11y.now_open_timer_hint"))
            .accessibilityAddTraits([.isButton, .updatesFrequently])
        } else {
            VStack(alignment: .leading, spacing: NinaMetrics.space2) {
                Text("now.title").font(.ninaCaption).foregroundStyle(Color(.textSecondary))
                if let since = tracking.awakeSince {
                    Text(String(format: NSLocalizedString("now.awake_for %@", comment: ""),
                                EventFormatting.duration(date.timeIntervalSince(since))))
                        .ninaHeading()
                    if let last = tracking.lastClosedSleep {
                        Text(String(format: NSLocalizedString("now.last_sleep %@", comment: ""),
                                    EventFormatting.timeRange(start: last.startAt, end: last.endAt,
                                                              timeZone: last.timeZone ?? tracking.babyTimeZone)))
                            .font(.ninaCallout).foregroundStyle(Color(.textSecondary))
                    }
                } else if tracking.hasNoRecords {
                    Text("now.empty.title").ninaHeading()
                    Text(String(format: NSLocalizedString("now.empty.detail %@", comment: ""), babyName))
                        .font(.ninaBody).foregroundStyle(Color(.textSecondary))
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    Text("now.awake").ninaHeading()
                }
            }
            .ninaCard()
            .accessibilityElement(children: .combine)
        }
    }
}

// MARK: - Cartão "Hoje"

struct TodayCard: View {
    @Bindable var tracking: TrackingViewModel
    let onRecord: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space3) {
            Text("today.title").ninaHeading()
            let summary = tracking.todaySummary
            if summary.isEmpty {
                Text("today.empty").font(.ninaBody).foregroundStyle(Color(.textSecondary))
                    .fixedSize(horizontal: false, vertical: true)
                if tracking.canWrite {
                    Button("today.empty.action", action: onRecord).buttonStyle(.nina(.secondary))
                }
            } else {
                DaySummaryLine(summary: summary)
                TimelineView(.periodic(from: Date(), by: 30)) { context in
                    VStack(alignment: .leading, spacing: NinaMetrics.space1) {
                        if let feeding = tracking.lastFeeding {
                            Text(String(format: NSLocalizedString("today.last_feeding %@", comment: ""),
                                        EventFormatting.duration(context.date.timeIntervalSince(feeding.startAt))))
                        }
                        if let diaper = tracking.lastDiaper {
                            Text(String(format: NSLocalizedString("today.last_diaper %@", comment: ""),
                                        EventFormatting.duration(context.date.timeIntervalSince(diaper.startAt))))
                        }
                    }
                    .font(.ninaCallout)
                    .foregroundStyle(Color(.textSecondary))
                }
            }
        }
        .ninaCard()
    }
}
