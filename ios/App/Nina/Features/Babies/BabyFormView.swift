import SwiftUI
import NinaCore

@MainActor
struct BabyFormScreen: View {
    @State private var viewModel: BabyFormViewModel

    init(container: AppContainer, mode: BabyFormViewModel.Mode, onSaved: @escaping @MainActor (Baby) -> Void) {
        _viewModel = State(initialValue: BabyFormViewModel(mode: mode, babies: container.babyRepository,
                                                           consents: container.consentRepository, onSaved: onSaved))
    }

    var body: some View {
        BabyFormView(viewModel: viewModel)
    }
}

/// Criação/edição de bebê (RF-004/005). Data prevista do parto é campo próprio e opcional; a idade exibida
/// vem de `age_calculation` da API, nunca de cálculo local.
struct BabyFormView: View {
    @Bindable var viewModel: BabyFormViewModel
    @FocusState private var nameFocused: Bool

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: NinaMetrics.space5) {
                if let banner = viewModel.banner { MessageBanner(message: banner) }
                if !viewModel.canEdit { MessageBanner(message: UserMessage("baby.owner_only"), style: .info) }

                NinaTextField(title: "baby.field.name", text: $viewModel.displayName,
                              error: viewModel.fieldErrors[.displayName], contentType: .nickname,
                              autocapitalization: .words, submitLabel: .done, onSubmit: { nameFocused = false })
                    .focused($nameFocused)

                VStack(alignment: .leading, spacing: NinaMetrics.space1) {
                    DatePicker("baby.field.birth_date", selection: $viewModel.birthDate, in: ...Date(),
                               displayedComponents: .date)
                        .environment(\.timeZone, viewModel.timeZone)
                        .frame(minHeight: NinaMetrics.minTouchTarget)
                    if let error = viewModel.fieldErrors[.birthDate] { FieldErrorLabel(message: error) }
                }

                VStack(alignment: .leading, spacing: NinaMetrics.space2) {
                    Toggle("baby.field.has_due_date", isOn: $viewModel.hasDueDate)
                        .frame(minHeight: NinaMetrics.minTouchTarget)
                    if viewModel.hasDueDate {
                        DatePicker("baby.field.due_date", selection: $viewModel.dueDate, displayedComponents: .date)
                            .environment(\.timeZone, viewModel.timeZone)
                            .frame(minHeight: NinaMetrics.minTouchTarget)
                    }
                    Text("baby.field.due_date_hint").font(.ninaCaption).foregroundStyle(Color(.textSecondary))
                        .fixedSize(horizontal: false, vertical: true)
                    if let error = viewModel.fieldErrors[.dueDate] { FieldErrorLabel(message: error) }
                }

                Picker("baby.field.sex", selection: $viewModel.sex) {
                    Text("baby.sex.none").tag(Sex?.none)
                    ForEach(Sex.allCases, id: \.self) { sex in
                        Text(sex.labelKey).tag(Sex?.some(sex))
                    }
                }
                .pickerStyle(.menu)
                .frame(minHeight: NinaMetrics.minTouchTarget)

                VStack(alignment: .leading, spacing: NinaMetrics.space1) {
                    Picker("baby.field.timezone", selection: $viewModel.timezoneIdentifier) {
                        ForEach(timezoneChoices, id: \.self) { Text($0).tag($0) }
                    }
                    .pickerStyle(.navigationLink)
                    .frame(minHeight: NinaMetrics.minTouchTarget)
                    Text("baby.field.timezone_hint").font(.ninaCaption).foregroundStyle(Color(.textSecondary))
                    if let error = viewModel.fieldErrors[.timezone] { FieldErrorLabel(message: error) }
                }

                if case .edit(let baby) = viewModel.mode, let summary = AgeSummary(baby: baby) {
                    VStack(alignment: .leading, spacing: NinaMetrics.space1) {
                        Text("baby.age.current").ninaHeading()
                        Text(AgeFormatting.describe(summary.chronological)).font(.ninaBody)
                        if let corrected = summary.corrected {
                            Text(String(format: NSLocalizedString("age.corrected", comment: ""), AgeFormatting.describe(corrected)))
                                .font(.ninaCallout).foregroundStyle(Color(.textSecondary))
                        }
                    }
                    .ninaCard()
                    .accessibilityElement(children: .combine)
                }

                if !viewModel.isEditing {
                    VStack(alignment: .leading, spacing: NinaMetrics.space2) {
                        NinaCheckbox(isOn: $viewModel.guardianDeclaration) { Text("baby.guardian.declaration") }
                        if let error = viewModel.fieldErrors[.guardian] { FieldErrorLabel(message: error) }
                    }
                }

                Button {
                    nameFocused = false
                    Task { await viewModel.save() }
                } label: {
                    Text("common.save")
                }
                .buttonStyle(.nina(.primary, isLoading: viewModel.isSaving))
                .disabled(viewModel.isSaving || !viewModel.canEdit)
            }
            .padding(.horizontal, NinaMetrics.gutter)
            .padding(.vertical, NinaMetrics.space4)
        }
        .scrollDismissesKeyboard(.interactively)
        .ninaScreenBackground()
    }

    private var timezoneChoices: [String] {
        var all = TimeZone.knownTimeZoneIdentifiers.sorted()
        if !all.contains(viewModel.timezoneIdentifier) { all.insert(viewModel.timezoneIdentifier, at: 0) }
        return all
    }
}

private extension Sex {
    var labelKey: LocalizedStringKey {
        switch self {
        case .female: return "baby.sex.female"
        case .male: return "baby.sex.male"
        case .other: return "baby.sex.other"
        case .unknown: return "baby.sex.other"
        }
    }
}
