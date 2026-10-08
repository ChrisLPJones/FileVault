namespace Backend.Models
{
    // Request bodies and responses for two-factor authentication. Fields are validated before use.

    // POST /user/login/2fa: the challenge from POST /user/login plus either a code or a recovery code
    public record TwoFactorLoginRequest(string? ChallengeToken, string? Code, string? RecoveryCode);

    public record TwoFactorSetupRequest(string? Password);

    public record TwoFactorCodeRequest(string? Code);

    // Turning 2FA off needs the password and either a code or a recovery code
    public record TwoFactorDisableRequest(string? Password, string? Code, string? RecoveryCode);

    public record TwoFactorChallengeResponse(bool TwoFactorRequired, string ChallengeToken);

    public record TwoFactorStatusResponse(bool Enabled, int RecoveryCodesLeft);

    public record TwoFactorSetupResponse(string Secret, string OtpAuthUri);

    public record RecoveryCodesResponse(List<string> RecoveryCodes);
}
