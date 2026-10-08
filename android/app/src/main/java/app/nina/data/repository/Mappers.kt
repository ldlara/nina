package app.nina.data.repository

import app.nina.data.local.BabyEntity
import app.nina.data.local.MembershipEntity
import app.nina.data.remote.dto.BabyDto
import app.nina.data.remote.dto.ConsentAcceptanceDto
import app.nina.data.remote.dto.InvitationPreviewDto
import app.nina.data.remote.dto.LegalDocumentDto
import app.nina.data.remote.dto.MembershipDto
import app.nina.data.remote.dto.ReferenceDataDto
import app.nina.data.remote.dto.UserDto
import app.nina.domain.model.AgeCalculation
import app.nina.domain.model.Baby
import app.nina.domain.model.ConsentAcceptance
import app.nina.domain.model.InvitableRole
import app.nina.domain.model.InvitationPreview
import app.nina.domain.model.LegalDocument
import app.nina.domain.model.Membership
import app.nina.domain.model.MembershipStatus
import app.nina.domain.model.ReferenceData
import app.nina.domain.model.Role
import app.nina.domain.model.Sex
import app.nina.domain.model.User
import java.time.Instant
import java.time.LocalDate
import java.time.OffsetDateTime

/** Aceita RFC 3339 com `Z` ou offset. */
internal fun parseInstant(value: String): Instant = OffsetDateTime.parse(value).toInstant()

internal fun parseInstantOrNull(value: String?): Instant? = value?.let { runCatching { parseInstant(it) }.getOrNull() }

internal fun UserDto.toDomain() = User(id, email, emailVerified, displayName, locale, timezone, status, hasPassword)

internal fun ConsentAcceptance.toDto() = ConsentAcceptanceDto(purpose, documentVersion)

internal fun LegalDocumentDto.toDomain() = LegalDocument(purposeKey, version, url, required)

internal fun BabyDto.toDomain(): Baby = Baby(
    id = id,
    displayName = displayName,
    birthDate = LocalDate.parse(birthDate),
    dueDate = dueDate?.let(LocalDate::parse),
    sex = sex,
    timezone = timezone,
    myRole = myRole,
    version = version,
    ageCalculation = ageCalculation?.let { AgeCalculation(it.chronologicalDays, it.correctedDays, it.correctionApplied) },
    ageAsOf = age?.asOf?.let { runCatching { LocalDate.parse(it) }.getOrNull() },
    createdAt = parseInstant(createdAt),
    updatedAt = parseInstant(updatedAt),
)

internal fun Baby.toEntity() = BabyEntity(
    id = id,
    displayName = displayName,
    birthDate = birthDate.toString(),
    dueDate = dueDate?.toString(),
    sex = sex?.name,
    timezone = timezone,
    myRole = myRole.name,
    version = version,
    chronologicalDays = ageCalculation?.chronologicalDays,
    correctedDays = ageCalculation?.correctedDays,
    correctionApplied = ageCalculation?.correctionApplied,
    ageAsOf = ageAsOf?.toString(),
    createdAt = createdAt.toString(),
    updatedAt = updatedAt.toString(),
)

internal fun BabyEntity.toDomain() = Baby(
    id = id,
    displayName = displayName,
    birthDate = LocalDate.parse(birthDate),
    dueDate = dueDate?.let(LocalDate::parse),
    sex = sex?.let { s -> Sex.entries.firstOrNull { it.name == s } ?: Sex.UNRECOGNIZED },
    timezone = timezone,
    myRole = Role.entries.firstOrNull { it.name == myRole } ?: Role.UNRECOGNIZED,
    version = version,
    ageCalculation = chronologicalDays?.let { AgeCalculation(it, correctedDays, correctionApplied ?: (correctedDays != null)) },
    ageAsOf = ageAsOf?.let(LocalDate::parse),
    createdAt = Instant.parse(createdAt),
    updatedAt = Instant.parse(updatedAt),
)

internal fun MembershipDto.toDomain() = Membership(
    id = id,
    babyId = babyId,
    userId = user?.id,
    userDisplayName = user?.displayName,
    invitedEmail = invitedEmail,
    role = role,
    status = status,
    invitedAt = parseInstant(invitedAt),
    invitationExpiresAt = parseInstantOrNull(invitationExpiresAt),
    acceptedAt = parseInstantOrNull(acceptedAt),
)

internal fun Membership.toEntity() = MembershipEntity(
    id = id,
    babyId = babyId,
    userId = userId,
    userDisplayName = userDisplayName,
    invitedEmail = invitedEmail,
    role = role.name,
    status = status.name,
    invitedAt = invitedAt.toString(),
    invitationExpiresAt = invitationExpiresAt?.toString(),
    acceptedAt = acceptedAt?.toString(),
)

internal fun MembershipEntity.toDomain() = Membership(
    id = id,
    babyId = babyId,
    userId = userId,
    userDisplayName = userDisplayName,
    invitedEmail = invitedEmail,
    role = Role.entries.firstOrNull { it.name == role } ?: Role.UNRECOGNIZED,
    status = MembershipStatus.entries.firstOrNull { it.name == status } ?: MembershipStatus.UNRECOGNIZED,
    invitedAt = Instant.parse(invitedAt),
    invitationExpiresAt = invitationExpiresAt?.let(Instant::parse),
    acceptedAt = acceptedAt?.let(Instant::parse),
)

internal fun InvitationPreviewDto.toDomain() =
    InvitationPreview(inviterDisplayName, babyLabel, role, parseInstant(expiresAt))

internal fun ReferenceDataDto.toDomain() = ReferenceData(
    version = version,
    correctedAgeMaxMonths = policies.correctedAgeMaxMonths,
    invitationTtlDays = limits.invitationTtlDays,
    diaperTypes = diaperTypes,
    feedingTypes = feedingTypes,
    milkTypes = milkTypes,
)

internal fun InvitableRole.requireSendable(): InvitableRole {
    require(this != InvitableRole.UNRECOGNIZED) { "Papel inválido" }
    return this
}
