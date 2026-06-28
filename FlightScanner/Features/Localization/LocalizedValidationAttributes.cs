using System.ComponentModel.DataAnnotations;

namespace FlightScanner.Features.Localization;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class LocalizedRequiredAttribute(string fieldKey) : RequiredAttribute
{
    public override string FormatErrorMessage(string name) =>
        string.Format(UiText.T("FieldRequired"), UiText.T(fieldKey));
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class LocalizedCompareAttribute(string otherProperty, string messageKey) : CompareAttribute(otherProperty)
{
    public override string FormatErrorMessage(string name) => UiText.T(messageKey);
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class LocalizedEmailAddressAttribute(string fieldKey) : ValidationAttribute
{
    private readonly EmailAddressAttribute inner = new();

    public override bool IsValid(object? value) => inner.IsValid(value);

    public override string FormatErrorMessage(string name) =>
        string.Format(UiText.T("EmailAddressInvalid"), UiText.T(fieldKey));
}
