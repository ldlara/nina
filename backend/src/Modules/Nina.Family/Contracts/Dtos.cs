using System.Text.Json.Serialization;

namespace Nina.Family.Contracts;

// DTOs do contrato OpenAPI v1.0.1 (snake_case aplicado pela política JSON global). Campos nulos refletem "ausente" na entrada.

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BabyCreateRequest(
    Guid? Id,
    string? DisplayName,
    DateOnly? BirthDate,
    DateOnly? DueDate,
    string? Sex,
    string? Timezone);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvitationCreateRequest(string? Email, string? Role);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvitationTokenRequest(string? Token);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoleChangeRequest(string? Role);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OwnershipTransferRequest(Guid? NewOwnerMembershipId);

public sealed record AgeValueDto(int Days, int Weeks, int Months);

public sealed record AgeDto(DateOnly AsOf, AgeValueDto Chronological, AgeValueDto? Corrected, string Displayed);

public sealed record AgeCalculationDto(int ChronologicalDays, int? CorrectedDays, bool CorrectionApplied);

public sealed record BabyDto(
    Guid Id,
    string DisplayName,
    DateOnly BirthDate,
    DateOnly? DueDate,
    string? Sex,
    string Timezone,
    string? PhotoRef,
    string MyRole,
    AgeDto Age,
    AgeCalculationDto AgeCalculation,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record BabyList(IReadOnlyList<BabyDto> Items);

public sealed record UserRefDto(Guid Id, string DisplayName);

public sealed record MembershipDto(
    Guid Id,
    Guid BabyId,
    UserRefDto? User,
    string? InvitedEmail,
    string Role,
    string Status,
    DateTimeOffset InvitedAt,
    DateTimeOffset? InvitationExpiresAt,
    DateTimeOffset? AcceptedAt);

public sealed record MembershipList(IReadOnlyList<MembershipDto> Items);

public sealed record InvitationPreviewDto(
    string InviterDisplayName,
    string BabyLabel,
    string Role,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> VisibleData);
