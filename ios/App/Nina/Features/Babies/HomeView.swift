import SwiftUI
import NinaCore

struct HomeView: View {
    let container: AppContainer
    @Bindable var babies: BabiesViewModel
    @State private var editingBaby: Baby?
    @State private var addingBaby = false

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: NinaMetrics.space5) {
                    if babies.isShowingCachedData { MessageBanner(message: UserMessage("home.offline_cache"), style: .warning) }
                    if case .failed(let message) = babies.state { MessageBanner(message: message) }

                    if babies.babies.count > 1 { babySelector }

                    if let baby = babies.selectedBaby {
                        BabyCard(baby: baby, onEdit: baby.myRole.isOwner ? { editingBaby = baby } : nil)
                        placeholderCard
                    }
                }
                .padding(.horizontal, NinaMetrics.gutter)
                .padding(.vertical, NinaMetrics.space4)
            }
            .refreshable { await babies.load() }
            .ninaScreenBackground()
            .navigationTitle(Text("tab.home"))
            .toolbar {
                ToolbarItem(placement: .primaryAction) {
                    Button { addingBaby = true } label: { Label("baby.add", systemImage: "plus") }
                }
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
        }
    }

    private var babySelector: some View {
        Picker("home.baby_selector", selection: Binding(get: { babies.selectedBaby?.id ?? UUID() },
                                                         set: { babies.selectedBabyId = $0 })) {
            ForEach(babies.babies) { Text($0.displayName).tag($0.id) }
        }
        .pickerStyle(.menu)
        .frame(minHeight: NinaMetrics.minTouchTarget)
    }

    private var placeholderCard: some View {
        VStack(alignment: .leading, spacing: NinaMetrics.space2) {
            Text("home.coming_soon.title").ninaHeading()
            Text("home.coming_soon.detail").font(.ninaBody).foregroundStyle(Color(.textSecondary))
                .fixedSize(horizontal: false, vertical: true)
        }
        .ninaCard()
        .accessibilityElement(children: .combine)
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
