namespace Nina.Identity.Contracts;

// DTOs do contrato OpenAPI v1.0.0 (snake_case aplicado pela política JSON global). Campos nulos refletem "ausente" na entrada.

public sealed record ConsentAcceptance(string? PurposeKey, string? DocumentVersion);

public sealed record DeviceInfo(Guid? DeviceId, string? Platform, string? DeviceLabel, string? AppVersion, string? OsVersion);

public sealed record RegisterRequest(
    string? Email,
    string? Password,
    string? DisplayName,
    string? Locale,
    string? Timezone,
    List<ConsentAcceptance>? Consents);

public sealed record EmailVerifyRequest(string? Email, string? Code, DeviceInfo? Device);

public sealed record LoginRequest(string? Email, string? Password, DeviceInfo? Device);

public sealed record SocialLoginRequest(
    string? IdToken,
    string? Nonce,
    DeviceInfo? Device,
    string? GivenName,
    string? FamilyName,
    string? Locale,
    string? Timezone,
    List<ConsentAcceptance>? Consents);

public sealed record RefreshRequest(string? RefreshToken, Guid? DeviceId);

public sealed record ForgotPasswordRequest(string? Email);

public sealed record ResetPasswordRequest(string? Token, string? NewPassword);

public sealed record ReauthRequest(string? Password, string? Provider, string? IdToken, string? Nonce);

public sealed record ChangePasswordRequest(string? NewPassword);

public sealed record LinkIdentityRequest(string? Provider, string? IdToken, string? Nonce);

public sealed record UserUpdateRequest(string? DisplayName, string? Locale, string? Timezone);

public sealed record PushTokenRequest(
    string? Platform,
    string? Token,
    string? Environment,
    string? AppVersion,
    string? Locale,
    bool? OsNotificationsAuthorized);

public sealed record ConsentInputRequest(
    string? PurposeKey,
    string? DocumentVersion,
    string? Status,
    string? Source,
    Guid? BabyId,
    string? Locale);

// ---- respostas

public sealed record VerificationPending(string Status, int ResendAfterSeconds);

public sealed record UserIdentityDto(string Provider, DateTimeOffset LinkedAt);

public sealed record UserDto(
    Guid Id,
    string Email,
    bool EmailVerified,
    string? DisplayName,
    string Locale,
    string? Timezone,
    string Status,
    bool HasPassword,
    List<UserIdentityDto> Identities,
    DateTimeOffset CreatedAt);

public sealed record ConsentAcceptanceDto(string PurposeKey, string DocumentVersion);

public sealed record TokenResponse(
    string TokenType,
    string AccessToken,
    int ExpiresIn,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt,
    Guid SessionId,
    UserDto User,
    List<ConsentAcceptanceDto> PendingConsents);

public sealed record ReauthResponse(string ReauthToken, int ExpiresIn);

public sealed record SessionDto(
    Guid Id,
    Guid DeviceId,
    string? DeviceLabel,
    string Platform,
    string? AppVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    string? ApproxLocation,
    bool IsCurrent);

public sealed record SessionList(List<SessionDto> Items);

public sealed record RevokedCountDto(int RevokedCount);

public sealed record PushTokenDto(Guid DeviceId, string Platform, DateTimeOffset RegisteredAt, DateTimeOffset UpdatedAt);

public sealed record ConsentRecordDto(
    Guid Id,
    string PurposeKey,
    string DocumentVersion,
    string Status,
    DateTimeOffset GrantedAt,
    DateTimeOffset? RevokedAt,
    Guid? BabyId,
    string Source);

public sealed record LegalDocumentDto(
    string PurposeKey,
    string Version,
    string Url,
    string ContentHash,
    DateTimeOffset EffectiveAt,
    bool Required);

public sealed record LegalDocumentList(List<LegalDocumentDto> Items);

public sealed record ConsentsResponse(
    List<ConsentRecordDto> Current,
    List<LegalDocumentDto> PendingRequired,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    List<ConsentRecordDto>? History);
