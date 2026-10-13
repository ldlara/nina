package app.nina.data.local

import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey

/**
 * Cache local do bebê. Persistem-se apenas `birth_date` e `due_date`; as colunas `chronological_days`/`corrected_days`
 * guardam o **último cálculo recebido do servidor** (para exibir offline com a data de referência `age_as_of`) e não
 * são fonte de verdade: são sobrescritas a cada atualização (ADR-0009).
 */
@Entity(tableName = "baby")
data class BabyEntity(
    @PrimaryKey val id: String,
    val displayName: String,
    val birthDate: String,
    val dueDate: String?,
    val sex: String?,
    val timezone: String,
    val myRole: String,
    val version: Int,
    val chronologicalDays: Int?,
    val correctedDays: Int?,
    val correctionApplied: Boolean?,
    val ageAsOf: String?,
    val createdAt: String,
    val updatedAt: String,
)

@Entity(tableName = "membership", indices = [Index("babyId")])
data class MembershipEntity(
    @PrimaryKey val id: String,
    val babyId: String,
    val userId: String?,
    val userDisplayName: String?,
    val invitedEmail: String?,
    val role: String,
    val status: String,
    val invitedAt: String,
    val invitationExpiresAt: String?,
    val acceptedAt: String?,
)
