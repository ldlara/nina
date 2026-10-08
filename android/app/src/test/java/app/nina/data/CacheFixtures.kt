package app.nina.data.repository

import app.nina.data.local.BabyEntity

/** Entidade de cache de exemplo para testes (mesmo pacote para acessar mappers internos). */
internal fun cachedBabyEntity(id: String = "b-1", role: String = "OWNER") = BabyEntity(
    id = id, displayName = "Nina", birthDate = "2026-01-10", dueDate = "2026-01-24", sex = null,
    timezone = "America/Sao_Paulo", myRole = role, version = 3, chronologicalDays = 271, correctedDays = 257,
    correctionApplied = true, ageAsOf = "2026-10-08", createdAt = "2026-10-08T17:00:00Z", updatedAt = "2026-10-08T17:10:00Z",
)
