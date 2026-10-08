import SwiftUI
import NinaCore

// MARK: - Linha de evento (timeline / listas)

struct EventRow: View {
    let event: TrackedEvent
    let fallbackTimeZone: TimeZone
    var currentUserId: UUID?

    private var showsAuthor: Bool {
        guard let author = event.lastModifiedBy, let currentUserId else { return false }
        return author.id != currentUserId && !author.displayName.isEmpty
    }

    var body: some View {
        HStack(alignment: .top, spacing: NinaMetrics.space3) {
            EventBadge(event: event)
            VStack(alignment: .leading, spacing: NinaMetrics.space1) {
                Text(EventFormatting.title(for: event))
                    .font(.ninaBodyStrong)
                    .foregroundStyle(Color(.textPrimary))
                Text(EventFormatting.subtitle(for: event, fallback: fallbackTimeZone))
                    .font(.ninaCallout)
                    .foregroundStyle(Color(.textSecondary))
                if let sync = EventFormatting.syncLabel(for: event) {
                    Label(sync, systemImage: event.syncStatus == .rejected ? "exclamationmark.triangle.fill" : "arrow.triangle.2.circlepath")
                        .font(.ninaCaption)
                        .foregroundStyle(Color(event.syncStatus == .rejected ? NinaColorToken.stateError : NinaColorToken.textSecondary))
                }
                if showsAuthor, let name = event.lastModifiedBy?.displayName {
                    Text(String(format: NSLocalizedString("event.edited_by %@", comment: ""), name))
                        .font(.ninaCaption)
                        .foregroundStyle(Color(.textSecondary))
                }
            }
            Spacer(minLength: 0)
            Image(systemName: "chevron.right")
                .font(.ninaCaption)
                .foregroundStyle(Color(.textSecondary))
                .accessibilityHidden(true)
        }
        .padding(.vertical, NinaMetrics.space2)
        .frame(minHeight: NinaMetrics.minTouchTarget, alignment: .top)
        .contentShape(Rectangle())
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text(EventFormatting.accessibilityLabel(for: event, fallback: fallbackTimeZone, showAuthor: showsAuthor)))
        .accessibilityHint(Text("a11y.event_row_hint"))
        .accessibilityAddTraits(.isButton)
    }
}

/// Ícone do tipo: forma + símbolo (nunca só cor).
struct EventBadge: View {
    let event: TrackedEvent
    var size: CGFloat = 40

    var body: some View {
        let color = Color(EventFormatting.token(for: event.kind))
        Image(systemName: EventFormatting.symbol(for: event))
            .font(.system(size: size * 0.45, weight: .semibold))
            .foregroundStyle(color)
            .frame(width: size, height: size)
            .background(Color(.bgSurfaceRaised), in: RoundedRectangle(cornerRadius: NinaMetrics.radiusSmall))
            .overlay(RoundedRectangle(cornerRadius: NinaMetrics.radiusSmall).stroke(color, lineWidth: 1.5))
            .accessibilityHidden(true)
    }
}

// MARK: - Indicador de sincronização (UX 4.13): discreto, nunca modal

struct SyncStatusBar: View {
    @Bindable var tracking: TrackingViewModel
    /// Mostra "Tudo sincronizado" (usado em Mais); na Home/Timeline o estado sincronizado fica oculto.
    var showWhenSynced = false

    var body: some View {
        let indicator = tracking.syncIndicator
        VStack(alignment: .leading, spacing: NinaMetrics.space2) {
            switch indicator.phase {
            case .synced:
                if showWhenSynced { line(symbol: "checkmark.icloud", key: "sync.all_synced", tint: .stateSuccess) }
            case .syncing:
                HStack(spacing: NinaMetrics.space2) {
                    ProgressView()
                    Text("sync.syncing").font(.ninaCallout).foregroundStyle(Color(.textSecondary))
                }
                .accessibilityElement(children: .combine)
            case .offline:
                banner(symbol: "icloud.slash", text: Text("sync.offline"), tint: .stateWarning)
                if indicator.pendingCount > 0 { pendingLine(indicator.pendingCount, retry: false) }
            case .pending:
                pendingLine(indicator.pendingCount, retry: true)
            case .error(let since):
                banner(symbol: "exclamationmark.icloud",
                       text: Text(String(format: NSLocalizedString("sync.error_since %@", comment: ""),
                                         EventFormatting.time(since, timeZone: tracking.babyTimeZone))),
                       tint: .stateWarning)
                retryButton("sync.retry_error")
            }
            if indicator.rejectedCount > 0 {
                banner(symbol: "exclamationmark.triangle.fill",
                       text: Text(LocalizedMessage.plural("sync.rejected_count", count: indicator.rejectedCount)),
                       tint: .stateError)
                Button("sync.discard_rejected") { Task { await tracking.discardRejected() } }
                    .buttonStyle(.nina(.secondary))
            }
        }
    }

    private func pendingLine(_ count: Int, retry: Bool) -> some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space2) {
            Label(LocalizedMessage.plural("sync.pending_count", count: count), systemImage: "arrow.triangle.2.circlepath")
                .font(.ninaCallout)
                .foregroundStyle(Color(.textSecondary))
            if retry { retryButton("sync.retry_now") }
        }
        .accessibilityElement(children: .contain)
    }

    private func retryButton(_ key: LocalizedStringKey) -> some View {
        Button(key) { Task { await tracking.retryNow() } }
            .buttonStyle(.nina(.secondary))
    }

    private func line(symbol: String, key: LocalizedStringKey, tint: NinaColorToken) -> some View {
        Label(key, systemImage: symbol)
            .font(.ninaCallout)
            .foregroundStyle(Color(tint))
    }

    /// Faixa fina com ícone + texto (cor nunca é o único sinal).
    private func banner(symbol: String, text: Text, tint: NinaColorToken) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: NinaMetrics.space3) {
            Image(systemName: symbol).foregroundStyle(Color(tint)).accessibilityHidden(true)
            text.font(.ninaCallout).foregroundStyle(Color(.textPrimary)).fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .padding(NinaMetrics.space3)
        .background(Color(.bgSurfaceRaised), in: RoundedRectangle(cornerRadius: NinaMetrics.radiusSmall))
        .overlay(RoundedRectangle(cornerRadius: NinaMetrics.radiusSmall).stroke(Color(tint), lineWidth: 1))
        .accessibilityElement(children: .combine)
    }
}

// MARK: - Desfazer (8 s)

struct UndoToast: View {
    let undo: UndoAction
    let onUndo: () -> Void
    let onDismiss: () -> Void

    var body: some View {
        HStack(spacing: NinaMetrics.space3) {
            Text(LocalizedMessage.text(for: undo.message))
                .font(.ninaCallout)
                .foregroundStyle(Color(.textPrimary))
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
            Button("common.undo", action: onUndo)
                .buttonStyle(.nina(.text))
        }
        .padding(NinaMetrics.space3)
        .background(Color(.bgSurfaceRaised), in: RoundedRectangle(cornerRadius: NinaMetrics.radiusMedium))
        .overlay(RoundedRectangle(cornerRadius: NinaMetrics.radiusMedium).stroke(Color(.borderSubtle), lineWidth: 1))
        .padding(.horizontal, NinaMetrics.gutter)
        .accessibilityElement(children: .contain)
        .accessibilityAddTraits(.updatesFrequently)
        // Some sozinho depois da janela de desfazer; o `id` troca a cada nova ação e reinicia a contagem.
        .task(id: undo.expiresAt) {
            let remaining = max(0, undo.expiresAt.timeIntervalSinceNow)
            try? await Task.sleep(nanoseconds: UInt64(remaining * 1_000_000_000))
            if !Task.isCancelled { onDismiss() }
        }
    }
}

// MARK: - Cabeçalho de resumo do dia

struct DaySummaryLine: View {
    let summary: DaySummary

    var body: some View {
        let sleep = EventFormatting.duration(summary.sleepSeconds)
        VStack(alignment: .leading, spacing: NinaMetrics.space1) {
            Text(String(format: NSLocalizedString("summary.sleep %@ %lld", comment: ""), sleep, summary.napCount))
            Text(String(format: NSLocalizedString("summary.feedings_diapers %lld %lld", comment: ""),
                        summary.feedingCount, summary.diaperCount))
            if summary.pumpingCount > 0 {
                Text(String(format: NSLocalizedString("summary.pumpings %lld", comment: ""), summary.pumpingCount))
            }
        }
        .font(.ninaBody)
        .foregroundStyle(Color(.textPrimary))
        .accessibilityElement(children: .combine)
    }
}
