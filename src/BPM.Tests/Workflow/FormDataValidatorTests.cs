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
