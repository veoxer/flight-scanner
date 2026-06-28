using Microsoft.AspNetCore.Identity;

namespace FlightScanner.Features.Localization;

public sealed class LocalizedIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError PasswordTooShort(int length) =>
        Error(nameof(PasswordTooShort), string.Format(UiText.T("PasswordTooShort"), length));

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) =>
        Error(nameof(PasswordRequiresUniqueChars), string.Format(UiText.T("PasswordRequiresUniqueChars"), uniqueChars));

    public override IdentityError PasswordRequiresNonAlphanumeric() =>
        Error(nameof(PasswordRequiresNonAlphanumeric), UiText.T("PasswordRequiresNonAlphanumeric"));

    public override IdentityError PasswordRequiresDigit() =>
        Error(nameof(PasswordRequiresDigit), UiText.T("PasswordRequiresDigit"));

    public override IdentityError PasswordRequiresLower() =>
        Error(nameof(PasswordRequiresLower), UiText.T("PasswordRequiresLower"));

    public override IdentityError PasswordRequiresUpper() =>
        Error(nameof(PasswordRequiresUpper), UiText.T("PasswordRequiresUpper"));

    public override IdentityError DuplicateEmail(string? email) =>
        Error(nameof(DuplicateEmail), string.Format(UiText.T("DuplicateEmail"), email));

    public override IdentityError DuplicateUserName(string userName) =>
        Error(nameof(DuplicateUserName), string.Format(UiText.T("DuplicateUserName"), userName));

    public override IdentityError InvalidEmail(string? email) =>
        Error(nameof(InvalidEmail), string.Format(UiText.T("InvalidEmail"), email));

    private static IdentityError Error(string code, string description) => new()
    {
        Code = code,
        Description = description
    };
}
