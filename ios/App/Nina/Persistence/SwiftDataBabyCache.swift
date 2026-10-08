import Foundation
import SwiftData
import NinaCore

/// Linha do cache local: guarda o JSON do `Baby` (mesmo formato do contrato) em vez de espelhar cada campo.
/// Vantagem: um novo campo aditivo da API não exige migração de esquema no aparelho.
@Model
final class CachedBaby {
    @Attribute(.unique) var id: UUID
    var createdAt: Date
    var payload: Data

    init(id: UUID, createdAt: Date, payload: Data) {
        self.id = id
        self.createdAt = createdAt
        self.payload = payload
    }
}

/// Implementação SwiftData de `BabyCache`, isolada em um `@ModelActor` (contexto próprio, fora da main thread).
@ModelActor
actor SwiftDataBabyCache: BabyCache {
    func loadAll() async throws -> [Baby] {
        let rows = try modelContext.fetch(FetchDescriptor<CachedBaby>(sortBy: [SortDescriptor(\.createdAt)]))
        let decoder = NinaJSON.makeDecoder()
        // Linha ilegível (ex.: versão futura do app gravou algo novo) é ignorada, não derruba a lista.
        return rows.compactMap { try? decoder.decode(Baby.self, from: $0.payload) }
    }

    func replaceAll(_ babies: [Baby]) async throws {
        try modelContext.delete(model: CachedBaby.self)
        for baby in babies { try insert(baby) }
        try modelContext.save()
    }

    func upsert(_ baby: Baby) async throws {
        let babyId = baby.id
        let existing = try modelContext.fetch(FetchDescriptor<CachedBaby>(predicate: #Predicate { $0.id == babyId }))
        if let row = existing.first {
            row.payload = try NinaJSON.makeEncoder().encode(baby)
            row.createdAt = baby.createdAt
        } else {
            try insert(baby)
        }
        try modelContext.save()
    }

    func remove(id: Baby.ID) async throws {
        try modelContext.delete(model: CachedBaby.self, where: #Predicate { $0.id == id })
        try modelContext.save()
    }

    func clear() async throws {
        try modelContext.delete(model: CachedBaby.self)
        try modelContext.save()
    }

    private func insert(_ baby: Baby) throws {
        let data = try NinaJSON.makeEncoder().encode(baby)
        modelContext.insert(CachedBaby(id: baby.id, createdAt: baby.createdAt, payload: data))
    }
}

enum LocalStore {
    /// Container persistente; se o arquivo estiver corrompido, cai para memória (o servidor é a fonte da verdade).
    /// ATENÇÃO (IOS-002): o mesmo container guarda a fila de mutações offline. Cair para memória significa que
    /// registros ainda não enviados deixam de existir após o próximo fechamento do app. Ver README (risco conhecido).
    static func makeContainer(inMemory: Bool = false) -> ModelContainer {
        let schema = Schema([CachedBaby.self, CachedEvent.self, CachedMutation.self])
        let configuration = ModelConfiguration(schema: schema, isStoredInMemoryOnly: inMemory)
        if let container = try? ModelContainer(for: schema, configurations: [configuration]) {
            return container
        }
        let fallback = ModelConfiguration(schema: schema, isStoredInMemoryOnly: true)
        // swiftlint:disable:next force_try
        return try! ModelContainer(for: schema, configurations: [fallback])
    }
}
