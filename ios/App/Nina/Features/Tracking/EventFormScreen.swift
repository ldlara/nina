import SwiftUI
import NinaCore

/// Formulário único para criar/editar sono manual, mamada (peito), mamadeira, bomba, fralda e despertar.
/// Campos mudam conforme o tipo: `milk_type` só em mamadeira, lado só em peito/bomba, fim obrigatório em mamada.
struct EventFormScreen: View {
    @State var viewModel: EventFormViewModel
    let title: LocalizedStringKey
    let dismiss: () -> Void
    @Environment(\.dynamicTypeSize) private var typeSize

    init(viewModel: EventFormViewModel, title: LocalizedStringKey, dismiss: @escaping () -> Void) {
        _viewModel = State(initialValue: viewModel)
        self.title = title
        self.dismiss = dismiss
    }

    var body: some View {
        @Bindable var vm = viewModel
        Form {
            if let banner = vm.banner { MessageBanner(message: banner).listRowBackground(Color.clear) }

            if vm.showsSleepType {
                Section {
                    Picker("sleep.type", selection: $vm.sleepType) {
                        Text(EventFormatting.name(.nap)).tag(SleepType.nap)
                        Text(EventFormatting.name(.night)).tag(SleepType.night)
                    }
                    .pickerStyle(.segmented)
                    .frame(minHeight: NinaMetrics.minTouchTarget)
                }
            }

            if vm.showsDiaperType {
                Section {
                    Picker("form.diaper.type", selection: $vm.diaperType) {
                        ForEach(DiaperType.allCases, id: \.self) { Text(EventFormatting.name($0)).tag($0) }
                    }
                    .pickerStyle(.inline)
                    .labelsHidden()
                } header: { Text("form.diaper.type") }
            }

            if vm.showsSide {
                Section {
                    Picker("form.side", selection: $vm.side) {
                        if !vm.sideIsRequired { Text("form.not_informed").tag(BreastSide?.none) }
                        ForEach(BreastSide.allCases, id: \.self) { side in
                            Text(EventFormatting.name(side)).tag(Optional(side))
                        }
                    }
                    .pickerStyle(.segmented)
                    .frame(minHeight: NinaMetrics.minTouchTarget)
                    fieldError("side", vm)
                } header: { Text("form.side") }
            }

            Section {
                DatePicker(LocalizedStringKey(vm.showsDuration || vm.kind == .sleep ? "form.start" : "form.time"),
                           selection: Binding(get: { vm.startAt }, set: { vm.moveStart(to: $0) }),
                           in: ...vm.maxSelectableTime, displayedComponents: [.date, .hourAndMinute])
                    .frame(minHeight: NinaMetrics.minTouchTarget)
                fieldError("startAt", vm)

                if vm.showsEnd && vm.showsDuration && vm.kind != .sleep {
                    Stepper(value: Binding(get: { vm.durationMinutes }, set: { vm.durationMinutes = $0 }), in: 1...600) {
                        Text(String(format: NSLocalizedString("form.duration_minutes %lld", comment: ""), vm.durationMinutes))
                    }
                    .frame(minHeight: NinaMetrics.minTouchTarget)
                    .accessibilityLabel(Text("form.duration"))
                    .accessibilityValue(Text(EventFormatting.spokenDuration(TimeInterval(vm.durationMinutes * 60))))
                    fieldError("endAt", vm)
                } else if vm.showsEnd {
                    if vm.endIsOptional {
                        Toggle(LocalizedStringKey(vm.kind == .sleep ? "form.has_end_sleep" : "form.has_end"), isOn: $vm.hasEnd)
                            .frame(minHeight: NinaMetrics.minTouchTarget)
                    }
                    if vm.hasEnd || !vm.endIsOptional {
                        DatePicker("form.end", selection: $vm.endAt, in: vm.endRange,
                                   displayedComponents: [.date, .hourAndMinute])
                            .frame(minHeight: NinaMetrics.minTouchTarget)
                    }
                    fieldError("endAt", vm)
                }
            }
            .environment(\.timeZone, vm.timeZone)

            if vm.showsVolume {
                Section {
                    if vm.volumeIsOptional {
                        Toggle("form.volume.include", isOn: $vm.includesVolume)
                            .frame(minHeight: NinaMetrics.minTouchTarget)
                    }
                    if vm.includesVolume || !vm.volumeIsOptional {
                        Stepper(value: $vm.volumeMl, in: 1...EventValidator.maxVolumeMl, step: EventFormViewModel.volumeStepMl) {
                            Text(EventFormatting.volume(vm.volumeMl)).font(.ninaBodyStrong)
                        }
                        .frame(minHeight: NinaMetrics.minTouchTarget)
                        .accessibilityLabel(Text("form.volume"))
                        .accessibilityValue(Text(EventFormatting.volume(vm.volumeMl)))
                    }
                    fieldError("volumeMl", vm)
                } header: { Text("form.volume") }
            }

            if vm.showsMilkType {
                Section {
                    Picker("form.milk_type", selection: $vm.milkType) {
                        Text("form.not_informed").tag(MilkType?.none)
                        ForEach(MilkType.allCases, id: \.self) { type in
                            Text(EventFormatting.name(type)).tag(Optional(type))
                        }
                    }
                    .frame(minHeight: NinaMetrics.minTouchTarget)
                }
            }

            if vm.showsMethodOrPlace || vm.showsNotes {
                Section {
                    if vm.showsMethodOrPlace {
                        TextField("form.method_or_place", text: $vm.methodOrPlace)
                            .frame(minHeight: NinaMetrics.minTouchTarget)
                        fieldError("methodOrPlace", vm)
                    }
                    if vm.showsNotes {
                        TextField("form.notes", text: $vm.notes, axis: .vertical)
                            .lineLimit(1...4)
                            .frame(minHeight: NinaMetrics.minTouchTarget)
                        fieldError("notes", vm)
                    }
                } header: { Text("form.details") }
            }

            if !vm.canSave && !vm.isSaving {
                Section { MessageBanner(message: UserMessage("tracking.read_only"), style: .info).listRowBackground(Color.clear) }
            }
        }
        .scrollContentBackground(.hidden)
        .ninaScreenBackground()
        .navigationTitle(Text(title))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .cancellationAction) { Button("common.cancel", action: dismiss) }
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            Button { Task { if await vm.save() { dismiss() } } } label: {
                Text("common.save")
            }
            .buttonStyle(.nina(.primary, isLoading: vm.isSaving))
            .disabled(!vm.canSave)
            .padding(.horizontal, NinaMetrics.gutter)
            .padding(.vertical, NinaMetrics.space3)
            .background(Color(.bgApp).opacity(0.97))
        }
    }

    @ViewBuilder
    private func fieldError(_ field: String, _ vm: EventFormViewModel) -> some View {
        if let message = vm.fieldErrors[field] { FieldErrorLabel(message: message) }
    }
}
