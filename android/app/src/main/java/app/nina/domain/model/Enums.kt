package app.nina.domain.model

import kotlinx.serialization.Serializable

/** Papel do usuário em um bebê (`Role`). */
@Serializable(with = RoleSerializer::class)
enum class Role { OWNER, CAREGIVER, READ_ONLY, UNRECOGNIZED }
object RoleSerializer : TolerantEnumSerializer<Role>("Role", Role.entries, Role.UNRECOGNIZED)

/** Papéis aceitos em convite (`InvitableRole`). */
@Serializable(with = InvitableRoleSerializer::class)
enum class InvitableRole { CAREGIVER, READ_ONLY, UNRECOGNIZED }
object InvitableRoleSerializer :
    TolerantEnumSerializer<InvitableRole>("InvitableRole", InvitableRole.entries, InvitableRole.UNRECOGNIZED)

@Serializable(with = MembershipStatusSerializer::class)
enum class MembershipStatus { PENDING, ACTIVE, REVOKED, DECLINED, EXPIRED, UNRECOGNIZED }
object MembershipStatusSerializer :
    TolerantEnumSerializer<MembershipStatus>("MembershipStatus", MembershipStatus.entries, MembershipStatus.UNRECOGNIZED)

@Serializable(with = UserStatusSerializer::class)
enum class UserStatus { ACTIVE, PENDING_DELETION, UNRECOGNIZED }
object UserStatusSerializer : TolerantEnumSerializer<UserStatus>("UserStatus", UserStatus.entries, UserStatus.UNRECOGNIZED)

@Serializable(with = SexSerializer::class)
enum class Sex { FEMALE, MALE, OTHER, UNRECOGNIZED }
object SexSerializer : TolerantEnumSerializer<Sex>("Sex", Sex.entries, Sex.UNRECOGNIZED)

@Serializable(with = IdentityProviderSerializer::class)
enum class IdentityProvider { GOOGLE, APPLE, UNRECOGNIZED }
object IdentityProviderSerializer :
    TolerantEnumSerializer<IdentityProvider>("IdentityProvider", IdentityProvider.entries, IdentityProvider.UNRECOGNIZED)

@Serializable(with = PlatformSerializer::class)
enum class Platform { IOS, ANDROID, UNRECOGNIZED }
object PlatformSerializer : TolerantEnumSerializer<Platform>("Platform", Platform.entries, Platform.UNRECOGNIZED)

/** `PurposeKey`: finalidades de consentimento. Valores futuros são ignorados pelo cliente. */
@Serializable(with = ConsentPurposeSerializer::class)
enum class ConsentPurpose {
    TERMS_OF_USE, PRIVACY_POLICY, CHILD_DATA_GUARDIAN, ANALYTICS_PRODUCT, MARKETING_EMAIL, PUSH_NOTIFICATIONS, UNRECOGNIZED
}
object ConsentPurposeSerializer :
    TolerantEnumSerializer<ConsentPurpose>("PurposeKey", ConsentPurpose.entries, ConsentPurpose.UNRECOGNIZED)

@Serializable(with = ConsentStatusSerializer::class)
enum class ConsentStatus { GRANTED, REVOKED, SUPERSEDED, UNRECOGNIZED }
object ConsentStatusSerializer :
    TolerantEnumSerializer<ConsentStatus>("ConsentStatus", ConsentStatus.entries, ConsentStatus.UNRECOGNIZED)

@Serializable(with = ConsentSourceSerializer::class)
enum class ConsentSource { ONBOARDING, SETTINGS, PROMPT, UNRECOGNIZED }
object ConsentSourceSerializer :
    TolerantEnumSerializer<ConsentSource>("ConsentSource", ConsentSource.entries, ConsentSource.UNRECOGNIZED)

/** Enums extensíveis do contrato (`x-extensible-enum`), expostos em `/reference-data`. */
@Serializable(with = DiaperTypeSerializer::class)
enum class DiaperType { WET, DIRTY, MIXED, DRY, UNSPECIFIED, UNRECOGNIZED }
object DiaperTypeSerializer : TolerantEnumSerializer<DiaperType>("DiaperType", DiaperType.entries, DiaperType.UNRECOGNIZED)

@Serializable(with = FeedingTypeSerializer::class)
enum class FeedingType { BREASTFEEDING, BOTTLE, SOLID, OTHER, UNRECOGNIZED }
object FeedingTypeSerializer : TolerantEnumSerializer<FeedingType>("FeedingType", FeedingType.entries, FeedingType.UNRECOGNIZED)

@Serializable(with = MilkTypeSerializer::class)
enum class MilkType { BREAST_MILK, FORMULA, MIXED, OTHER, UNSPECIFIED, UNRECOGNIZED }
object MilkTypeSerializer : TolerantEnumSerializer<MilkType>("MilkType", MilkType.entries, MilkType.UNRECOGNIZED)

// ---- Tracking (AND-002) --------------------------------------------------------------------------------------------

@Serializable(with = BreastSideSerializer::class)
enum class BreastSide { LEFT, RIGHT, BOTH, UNRECOGNIZED }
object BreastSideSerializer : TolerantEnumSerializer<BreastSide>("BreastSide", BreastSide.entries, BreastSide.UNRECOGNIZED)

@Serializable(with = SleepTypeSerializer::class)
enum class SleepType { NAP, NIGHT, UNRECOGNIZED }
object SleepTypeSerializer : TolerantEnumSerializer<SleepType>("SleepType", SleepType.entries, SleepType.UNRECOGNIZED)

/** `source` de `SleepSession`: iniciada pelo timer ou registrada à mão (RF-009-A7). */
@Serializable(with = SleepSourceSerializer::class)
enum class SleepSource { TIMER, MANUAL, UNRECOGNIZED }
object SleepSourceSerializer : TolerantEnumSerializer<SleepSource>("SleepSource", SleepSource.entries, SleepSource.UNRECOGNIZED)

@Serializable(with = WakeSourceSerializer::class)
enum class WakeSource { MANUAL, INFERRED, IMPORT, UNRECOGNIZED }
object WakeSourceSerializer : TolerantEnumSerializer<WakeSource>("WakeSource", WakeSource.entries, WakeSource.UNRECOGNIZED)

/** `SyncEntityType` do contrato (entidades que o cliente escreve via `/sync/push`). */
@Serializable(with = SyncEntityTypeSerializer::class)
enum class SyncEntityType { SLEEP_SESSION, FEEDING_SESSION, PUMPING_SESSION, DIAPER_EVENT, WAKE_EVENT, UNRECOGNIZED }
object SyncEntityTypeSerializer :
    TolerantEnumSerializer<SyncEntityType>("SyncEntityType", SyncEntityType.entries, SyncEntityType.UNRECOGNIZED)

@Serializable(with = MutationOpSerializer::class)
enum class MutationOp { CREATE, UPDATE, DELETE, UNRECOGNIZED }
object MutationOpSerializer : TolerantEnumSerializer<MutationOp>("MutationOp", MutationOp.entries, MutationOp.UNRECOGNIZED)
