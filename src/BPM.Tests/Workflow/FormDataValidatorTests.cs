using System.Text.Json;
using BPM.Domain.Forms;
using BPM.Workflow.Engine;
using Xunit;

namespace BPM.Tests.Workflow;

public class FormDataValidatorTests
{
    private static readonly FormSchema Schema = new(new[]
    {
        new FormFieldDefinition("itemName", FormFieldType.Text, "Item Name", Required: true, Validation: new FormFieldValidation(MaxLength: 50)),
        new FormFieldDefinition("quantity", FormFieldType.Number, "Quantity", Required: true, Validation: new FormFieldValidation(MinValue: 1, MaxValue: 1000)),
        new FormFieldDefinition("category", FormFieldType.Select, "Category", Options: new[] { new FormFieldOption("hw", "Hardware"), new FormFieldOption("sw", "Software") }),
        new FormFieldDefinition("approved", FormFieldType.Checkbox, "Approved", ReadOnly: true),
    });

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void Validate_AllFieldsValid_Passes()
    {
        var data = Parse("""{"itemName":"Server","quantity":2,"category":"hw"}""");

        var result = FormDataValidator.Validate(Schema, data, enforceRequired: true);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_MissingRequiredField_RejectedWhenEnforced()
    {
        var data = Parse("""{"quantity":2}""");

        var result = FormDataValidator.Validate(Schema, data, enforceRequired: true);

        Assert.Contains(result.Errors, e => e.Code == "FIELD_REQUIRED");
    }

    // Phase 5.4.2: requiredOverride, when supplied, fully replaces field.Required rather than
    // adding to it — this is how FormEngine feeds in the rule engine's *effective* required set.
    [Fact]
    public void Validate_RequiredOverride_AddsRequirednessBeyondStaticSchema()
    {
        var data = Parse("""{"itemName":"Server","quantity":2}""");

        var result = FormDataValidator.Validate(Schema, data, enforceRequired: true, requiredOverride: new HashSet<string> { "itemName", "quantity", "category" });

        Assert.Contains(result.Errors, e => e.Code == "FIELD_REQUIRED");
    }

    [Fact]
    public void Validate_RequiredOverride_CanRelaxAFieldThatIsNotInTheOverrideSet()
    {
        // itemName is statically Required in Schema, but an empty requiredOverride means "the rule
        // engine says nothing is conditionally required" — since the override *replaces* rather
        // than adds to field.Required, passing an override without itemName in it stops enforcing
        // it. FormEngine never actually does this (it always includes every statically-required
        // field's key), but the validator itself must not silently fall back to field.Required
        // once an override is supplied — that would make requiredOverride's contract ambiguous.
        var data = Parse("""{"quantity":2}""");

        var result = FormDataValidator.Validate(Schema, data, enforceRequired: true, requiredOverride: new HashSet<string> { "quantity" });

        Assert.DoesNotContain(result.Errors, e => e.Code == "FIELD_REQUIRED" && e.Message.Contains("itemName"));
    }

    [Fact]
    public void Validate_MissingRequiredField_AllowedWhenNotEnforced()
    {
        var data = Parse("""{"quantity":2}""");

        var result = FormDataValidator.Validate(Schema, data, enforceRequired: false);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_NumberBelowMinValue_Rejected()
    {
        var data = Parse("""{"itemName":"x","quantity":0}""");

        var result = FormDataValidator.Validate(Schema, data, enforceRequired: true);

        Assert.Contains(result.Errors, e => e.Code == "FIELD_OUT_OF_RANGE");
    }

    [Fact]
    public void Validate_NumberAboveMaxValue_Rejected()
    {
        var data = Parse("""{"itemName":"x","quantity":5000}""");

        var result = FormDataValidator.Validate(Schema, data, enforceRequired: true);

        Assert.Contains(result.Errors, e => e.Code == "FIELD_OUT_OF_RANGE");
    }

    [Fact]
    public void Validate_TextExceedingMaxLength_Rejected()
    {
        var data = Parse($$"""{"itemName":"{{new string('a', 51)}}","quantity":2}""");

        var result = FormDataValidator.Validate(Schema, data, enforceRequired: true);

        Assert.Contains(result.Errors, e => e.Code == "FIELD_OUT_OF_RANGE");
    }

    [Fact]
    public void Validate_WrongType_Rejected()
    {
        var data = Parse("""{"itemName":123,"quantity":2}""");

        var result = FormDataValidator.Validate(Schema, data, enforceRequired: true);

        Assert.Contains(result.Errors, e => e.Code == "FIELD_TYPE_MISMATCH");
    }

    [Fact]
    public void Validate_UnrecognizedSelectOption_Rejected()
    {
        var data = Parse("""{"itemName":"x","quantity":2,"category":"not-an-option"}""");

        var result = FormDataValidator.Validate(Schema, data, enforceRequired: true);

        Assert.Contains(result.Errors, e => e.Code == "INVALID_FIELD_OPTION");
    }

    [Fact]
    public void Validate_DataNotAnObject_Rejected()
    {
        var data = Parse("[1,2,3]");

        var result = FormDataValidator.Validate(Schema, data, enforceRequired: true);

        Assert.Contains(result.Errors, e => e.Code == "FORM_DATA_NOT_AN_OBJECT");
    }
}
