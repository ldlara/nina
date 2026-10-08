import Foundation
import Observation

@Observable
@MainActor
public final class BabyFormViewModel {
    public enum Mode: Equatable, Sendable {
        case create
        case edit(Baby)
    }

    public enum Field: Hashable, Sendable { case displayName, birthDate, dueDate, timezone, guardian }

    public var displayName: String
    public var birthDate: Date
    public var hasDueDate: Bool
    public var dueDate: Date
    /// `nil` = "Prefiro não informar".
    public var sex: Sex?
    public var timezoneIdentifier: String
    public var guardianDeclaration = false

    public private(set) var isSaving = false
    public private(set) var banner: UserMessage?
    public private(set) var fieldErrors: [Field: UserMessage] = [:]

    public let mode: Mode

    @ObservationIgnored private let babies: BabyRepository
    @ObservationIgnored private let consents: ConsentRepository
    @ObservationIgnored private let now: @Sendable () -> Date
    @ObservationIgnored private let onSaved: @MainActor (Baby) -> Void
    @ObservationIgnored private let newBabyId = UUID()
    @ObservationIgnored private let idempotencyKey = UUID()

    public init(mode: Mode, babies: BabyRepository, consents: ConsentRepository,
                deviceTimeZone: TimeZone = .current, now: @escaping @Sendable () -> Date = { Date() },
                onSaved: @escaping @MainActor (Baby) -> Void) {
        self.mode = mode
        self.babies = babies
        self.consents = consents
        self.now = now
        self.onSaved = onSaved
        switch mode {
        case .create:
            displayName = ""
            birthDate = now()
            hasDueDate = false
            dueDate = now()
            sex = nil
            timezoneIdentifier = deviceTimeZone.identifier
        case .edit(let baby):
            let zone = TimeZone(identifier: baby.timezone) ?? deviceTimeZone
            displayName = baby.displayName
            birthDate = baby.birthDate.date(in: zone)
            hasDueDate = baby.dueDate != nil
            dueDate = (baby.dueDate ?? baby.birthDate).date(in: zone)
            sex = baby.sex
            timezoneIdentifier = baby.timezone
        }
    }

    // MARK: - Derivados

    public var isEditing: Bool {
        if case .edit = mode { return true }
        return false
    }

    /// Só o Owner edita o perfil do bebê (ADR-0009, decisão 6). Criação é sempre permitida.
    public var canEdit: Bool {
        switch mode {
        case .create: return true
        case .edit(let baby): return baby.myRole.isOwner
        }
    }

    public var timeZone: TimeZone { TimeZone(identifier: timezoneIdentifier) ?? .current }

    /// Data de hoje no fuso do bebê, para impedir nascimento no futuro (RF-004-A2).
    public var today: CivilDate { CivilDate(date: now(), timeZone: timeZone) }

    public var selectedBirthCivilDate: CivilDate { CivilDate(date: birthDate, timeZone: timeZone) }
    public var selectedDueCivilDate: CivilDate? { hasDueDate ? CivilDate(date: dueDate, timeZone: timeZone) : nil }

    // MARK: - Validação

    @discardableResult
    public func validate() -> Bool {
        var errors: [Field: UserMessage] = [:]
        let name = displayName.trimmingCharacters(in: .whitespacesAndNewlines)
        if name.isEmpty { errors[.displayName] = UserMessage("validation.required") }
        if name.count > 60 { errors[.displayName] = UserMessage("validation.name_too_long", arguments: ["60"]) }
        if TimeZone(identifier: timezoneIdentifier) == nil { errors[.timezone] = UserMessage("validation.timezone_invalid") }
        if selectedBirthCivilDate > today { errors[.birthDate] = UserMessage("validation.birth_in_future") }
        if !isEditing && !guardianDeclaration { errors[.guardian] = UserMessage("validation.guardian_required") }
        fieldErrors = errors
        return errors.isEmpty
    }

    // MARK: - Salvar

    public func save() async {
        banner = nil
        guard canEdit else {
            banner = UserMessage("error.forbidden_role")
            return
        }
        guard validate() else { return }
        isSaving = true
        defer { isSaving = false }
        do {
            let saved: Baby
            switch mode {
            case .create: saved = try await create()
            case .edit(let original):
                guard let updated = try await update(original) else {
                    onSaved(original)
                    return
                }
                saved = updated
            }
            onSaved(saved)
        } catch {
            banner = ErrorMapper.message(for: error)
            let serverFields = ErrorMapper.fieldMessages(for: error)
            if let message = serverFields["display_name"] { fieldErrors[.displayName] = message }
            if let message = serverFields["birth_date"] { fieldErrors[.birthDate] = message }
            if let message = serverFields["due_date"] { fieldErrors[.dueDate] = message }
        }
    }

    private func create() async throws -> Baby {
        try await recordGuardianConsent()
        let body = BabyCreate(id: newBabyId,
                              displayName: displayName.trimmingCharacters(in: .whitespacesAndNewlines),
                              birthDate: selectedBirthCivilDate, dueDate: selectedDueCivilDate, sex: sex,
                              timezone: timezoneIdentifier)
        return try await babies.createBaby(body, idempotencyKey: idempotencyKey)
    }

    /// O servidor exige a declaração de responsável legal já consentida antes de criar o bebê (403 CONSENT_REQUIRED).
    private func recordGuardianConsent() async throws {
        let documents = try await consents.legalDocuments()
        guard let document = documents.first(where: { $0.purposeKey == .childDataGuardian })
                ?? documents.first(where: { $0.purposeKey == .privacyPolicy }) else {
            throw APIError.problem(Problem(code: "LEGAL_DOCUMENT_UNAVAILABLE"), httpStatus: 0)
        }
        try await consents.record(ConsentInput(purposeKey: .childDataGuardian, documentVersion: document.version,
                                               status: .granted, source: .onboarding))
    }

    /// Retorna `nil` quando nada mudou (sem chamada de rede).
    private func update(_ original: Baby) async throws -> Baby? {
        let patch = makePatch(against: original)
        if patch.isEmpty { return nil }
        return try await babies.updateBaby(original, patch: patch)
    }

    func makePatch(against original: Baby) -> BabyUpdate {
        var patch = BabyUpdate()
        let name = displayName.trimmingCharacters(in: .whitespacesAndNewlines)
        if name != original.displayName { patch.displayName = name }
        let birth = selectedBirthCivilDate
        if birth != original.birthDate { patch.birthDate = birth }
        switch (selectedDueCivilDate, original.dueDate) {
        case (nil, .some): patch.dueDate = .clear
        case (.some(let new), let old) where new != old: patch.dueDate = .set(new)
        default: break
        }
        switch (sex, original.sex) {
        case (nil, .some): patch.sex = .clear
        case (.some(let new), let old) where new != old: patch.sex = .set(new)
        default: break
        }
        if timezoneIdentifier != original.timezone { patch.timezone = timezoneIdentifier }
        return patch
    }
}
