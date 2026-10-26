import SwiftUI
import NinaCore

/// Mamada no peito (UX 4.3): escolhe o lado (inicia o timer), "Trocar de lado" em 1 toque, "Terminar" salva.
/// Alternativa sem timer: "Registrar manualmente".
struct BreastfeedingTimerScreen: View {
    @Bindable var tracking: TrackingViewModel
    let dismiss: () -> Void
    @State private var timer: BreastfeedingTimerViewModel
    @State private var confirmingDiscard = false
    @State private var manual = false

    init(tracking: TrackingViewModel, dismiss: @escaping () -> Void) {
        self.tracking = tracking
        self.dismiss = dismiss
        // O timer guarda estado em UserDefaults; recriar a tela não perde a mamada em andamento.
        _timer = State(initialValue: BreastfeedingTimerViewModel(tracking: tracking, store: UserDefaultsFeedingTimerStore()))
    }

    var body: some View {
        ScrollView {
            VStack(spacing: NinaMetrics.space5) {
                if let banner = timer.banner ?? tracking.banner { MessageBanner(message: banner) }
                if timer.timer == nil {
                    sidePicker
                } else {
                    running
                }
            }
            .padding(NinaMetrics.gutter)
        }
        .ninaScreenBackground()
        .navigationTitle(Text("form.breastfeeding.title"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar { ToolbarItem(placement: .cancellationAction) { Button("common.close", action: dismiss) } }
        .navigationDestination(isPresented: $manual) {
            EventFormScreen(viewModel: EventFormViewModel(mode: .create(.breastfeeding), tracking: tracking),
                            title: "form.breastfeeding.title", dismiss: dismiss)
        }
        .confirmationDialog(Text("breast.discard.title"), isPresented: $confirmingDiscard, titleVisibility: .visible) {
            Button("breast.discard.confirm", role: .destructive) { timer.discard() }
            Button("common.cancel", role: .cancel) {}
        } message: {
            Text("breast.discard.message")
        }
    }

    // MARK: Escolha do lado (toque 3 do fluxo: ação -> Peito -> lado)

    private var sidePicker: some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space4) {
            Text("breast.pick_side").ninaHeading()
            if let last = tracking.lastUsed.breastSide {
                Text(String(format: NSLocalizedString("breast.last_side %@", comment: ""), EventFormatting.name(last)))
                    .font(.ninaCallout).foregroundStyle(Color(.textSecondary))
            }
            AdaptiveStack {
                sideButton(.left)
                sideButton(.right)
            }
            Button("breast.manual") { manual = true }
                .buttonStyle(.nina(.text))
                .frame(maxWidth: .infinity)
        }
    }

    private func sideButton(_ side: BreastSide) -> some View {
        Button(EventFormatting.name(side)) { timer.start(side: side) }
            .buttonStyle(.nina(.primary))
            .disabled(!tracking.canWrite)
    }

    // MARK: Em andamento

    private var running: some View {
        TimelineView(.periodic(from: Date(), by: 1)) { context in
            VStack(spacing: NinaMetrics.space5) {
                VStack(spacing: NinaMetrics.space2) {
                    Text(EventFormatting.clock(timer.elapsed(at: context.date)))
                        .font(.ninaDisplay).foregroundStyle(Color(.textPrimary))
                    if let side = timer.currentSide {
                        Text(String(format: NSLocalizedString("breast.current_side %@ %@", comment: ""),
                                    EventFormatting.name(side),
                                    EventFormatting.clock(timer.elapsedCurrentSide(at: context.date))))
                            .font(.ninaBody).foregroundStyle(Color(.textSecondary))
                    }
                }
                .accessibilityElement(children: .ignore)
                .accessibilityLabel(Text(String(format: NSLocalizedString("a11y.breast_running %@", comment: ""),
                                                EventFormatting.spokenDuration(timer.elapsed(at: context.date)))))
                .accessibilityAddTraits(.updatesFrequently)

                if timer.isRunning {
                    Button { timer.switchSide() } label: { Label("breast.switch_side", systemImage: "arrow.left.arrow.right") }
                        .buttonStyle(.nina(.secondary))
                }
                Button { Task { if await timer.finish() { dismiss() } } } label: { Text("breast.finish") }
                    .buttonStyle(.nina(.primary, isLoading: timer.isFinishing))
                Button("breast.discard.action", role: .destructive) { confirmingDiscard = true }
                    .buttonStyle(.nina(.text))
            }
        }
    }
}
