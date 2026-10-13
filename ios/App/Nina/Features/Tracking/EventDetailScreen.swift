import SwiftUI
import NinaCore

/// Detalhe, edição e exclusão de um evento (UX 4.4). Excluir pede confirmação e oferece "Desfazer" por 8 s.
struct EventDetailScreen: View {
    let eventId: UUID
    @Bindable var tracking: TrackingViewModel
    let dismiss: () -> Void

    @State private var editing: TrackedEvent?
    @State private var addingWake: TrackedEvent?
    @State private var confirmingDelete = false
    @State private var wakes: [TrackedEvent] = []
    @State private var pendingWakeDelete: TrackedEvent?

    private var event: TrackedEvent? {
        tracking.dayEvents.first { $0.id == eventId } ?? tracking.recentEvents.first { $0.id == eventId }
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: NinaMetrics.space5) {
                if let banner = tracking.banner { MessageBanner(message: banner) }
                if let event {
                    header(event)
                    facts(event)
                    if event.kind == .sleep, event.endAt != nil { wakeSection(event) }
                    if tracking.canWrite { actions(event) } else {
                        MessageBanner(message: UserMessage("tracking.read_only"), style: .info)
                    }
                } else {
                    Text("detail.not_found").font(.ninaBody).foregroundStyle(Color(.textSecondary))
                }
            }
            .padding(NinaMetrics.gutter)
        }
        .ninaScreenBackground()
        .navigationTitle(Text("detail.title"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar { ToolbarItem(placement: .cancellationAction) { Button("common.close", action: dismiss) } }
        .task(id: event?.updatedAt) { await loadWakes() }
        .sheet(item: $editing) { event in
            NavigationStack {
                EventFormScreen(viewModel: EventFormViewModel(mode: .edit(event), tracking: tracking),
                                title: "form.edit.title", dismiss: { editing = nil })
            }
        }
        .sheet(item: $addingWake, onDismiss: { Task { await loadWakes() } }) { sleep in
            NavigationStack {
                EventFormScreen(viewModel: EventFormViewModel(mode: .create(.wake(sleepSessionId: sleep.id)), tracking: tracking),
                                title: "form.wake.title", dismiss: { addingWake = nil })
            }
        }
        .confirmationDialog(Text("detail.delete.title"), isPresented: $confirmingDelete, titleVisibility: .visible) {
            Button("detail.delete.confirm", role: .destructive) {
                if let event { Task { await tracking.delete(event); dismiss() } }
            }
            Button("common.cancel", role: .cancel) {}
        } message: {
            Text("detail.delete.message")
        }
        .confirmationDialog(Text("detail.wake.delete.title"), isPresented: Binding(get: { pendingWakeDelete != nil },
                                                                                  set: { if !$0 { pendingWakeDelete = nil } }),
                            titleVisibility: .visible) {
            Button("detail.delete.confirm", role: .destructive) {
                if let wake = pendingWakeDelete { Task { await tracking.delete(wake); await loadWakes() } }
                pendingWakeDelete = nil
            }
            Button("common.cancel", role: .cancel) { pendingWakeDelete = nil }
        }
    }

    private func loadWakes() async {
        guard let event, event.kind == .sleep else { wakes = []; return }
        wakes = await tracking.wakeEvents(for: event)
    }

    private func header(_ event: TrackedEvent) -> some View {
        HStack(spacing: NinaMetrics.space3) {
            EventBadge(event: event, size: 48)
            VStack(alignment: .leading, spacing: NinaMetrics.space1) {
                Text(EventFormatting.title(for: event)).ninaHeading()
                Text(EventFormatting.subtitle(for: event, fallback: tracking.babyTimeZone))
                    .font(.ninaCallout).foregroundStyle(Color(.textSecondary))
            }
        }
        .accessibilityElement(children: .combine)
    }

    @ViewBuilder
    private func facts(_ event: TrackedEvent) -> some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space3) {
            if let side = event.side { fact("detail.side", EventFormatting.name(side)) }
            if let volume = event.volumeMl { fact("detail.volume", EventFormatting.volume(volume)) }
            if event.feedingType == .bottle {
                fact("detail.milk_type", event.milkType.map { EventFormatting.name($0) } ?? NSLocalizedString("form.not_informed", comment: ""))
            }
            if let place = event.methodOrPlace { fact("detail.method_or_place", place) }
            if let notes = event.notes { fact("detail.notes", notes) }
            if let count = event.nightAwakenings, count > 0 {
                fact("detail.night_awakenings", String(count))
            }
            if let author = event.lastModifiedBy?.displayName, !author.isEmpty {
                fact("detail.last_modified", String(format: NSLocalizedString("detail.last_modified_value %@ %@", comment: ""),
                                                    author, EventFormatting.time(event.updatedAt, timeZone: tracking.babyTimeZone)))
            }
            if let sync = EventFormatting.syncLabel(for: event) {
                Label(sync, systemImage: event.syncStatus == .rejected ? "exclamationmark.triangle.fill" : "arrow.triangle.2.circlepath")
                    .font(.ninaCallout).foregroundStyle(Color(.textSecondary))
            }
        }
        .ninaCard()
    }

    private func fact(_ key: LocalizedStringKey, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space1) {
            Text(key).font(.ninaCaption).foregroundStyle(Color(.textSecondary))
            Text(value).font(.ninaBody).foregroundStyle(Color(.textPrimary)).fixedSize(horizontal: false, vertical: true)
        }
        .accessibilityElement(children: .combine)
    }

    private func wakeSection(_ sleep: TrackedEvent) -> some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space3) {
            Text("detail.wakes").ninaHeading()
            if wakes.isEmpty {
                Text("detail.wakes.empty").font(.ninaCallout).foregroundStyle(Color(.textSecondary))
            }
            ForEach(wakes) { wake in
                HStack {
                    Text(EventFormatting.subtitle(for: wake, fallback: tracking.babyTimeZone)).font(.ninaBody)
                    Spacer(minLength: 0)
                    if tracking.canWrite {
                        Button { pendingWakeDelete = wake } label: { Image(systemName: "trash") }
                            .frame(minWidth: NinaMetrics.minTouchTarget, minHeight: NinaMetrics.minTouchTarget)
                            .accessibilityLabel(Text("detail.wake.delete"))
                    }
                }
                .accessibilityElement(children: .contain)
            }
            if tracking.canWrite {
                Button { addingWake = sleep } label: { Label("detail.wake.add", systemImage: "plus") }
                    .buttonStyle(.nina(.secondary))
            }
        }
        .ninaCard()
    }

    private func actions(_ event: TrackedEvent) -> some View {
        VStack(spacing: NinaMetrics.space3) {
            Button { editing = event } label: { Label("detail.edit", systemImage: "pencil") }
                .buttonStyle(.nina(.primary))
            Button(role: .destructive) { confirmingDelete = true } label: { Label("detail.delete", systemImage: "trash") }
                .buttonStyle(.nina(.destructive))
        }
    }
}
