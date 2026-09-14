using BPM.Domain.Forms;
using BPM.Workflow.Validation;
using Xunit;

namespace BPM.Tests.Workflow;

public class FormSchemaValidatorTests
{
    private static FormSchema ValidSchema() => new(
        Fields: new[]
        {
            new FormFieldDefinition("itemName", FormFieldType.Text, "Item Name", Required: true),
            new FormFieldDefinition("quantity", FormFieldType.Number, "Quantity", Required: true, Validation: new FormFieldValidation(MinValue: 1, MaxValue: 1000)),
            new FormFieldDefinition("amount", FormFieldType.Currency, "Amount", Required: true, Validation: new FormFieldValidation(MinValue: 0)),
        });

    [Fact]
    public void Validate_ValidSchema_Passes()
    {
        var result = FormSchemaValidator.Validate(ValidSchema());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_EmptySchema_Rejected()
    {
        var result = FormSchemaValidator.Validate(new FormSchema(Array.Empty<FormFieldDefinition>()));

        Assert.Contains(result.Errors, e => e.Code == "EMPTY_SCHEMA");
    }

    [Fact]
    public void Validate_DuplicateFieldKeys_Rejected()
    {
        var schema = new FormSchema(new[]
        {
            new FormFieldDefinition("amount", FormFieldType.Number, "Amount 1"),
            new FormFieldDefinition("amount", FormFieldType.Text, "Amount 2"),
        });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "DUPLICATE_FIELD_KEY");
    }

    [Fact]
    public void Validate_UnsupportedFieldType_Rejected()
    {
        var schema = new FormSchema(new[]
        {
            new FormFieldDefinition("sig", FormFieldType.Signature, "Signature"),
        });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "UNSUPPORTED_FIELD_TYPE");
    }

    [Fact]
    public void Validate_InvalidFieldKey_Rejected()
    {
        var schema = new FormSchema(new[]
        {
            new FormFieldDefinition("Item Name", FormFieldType.Text, "Item Name"),
        });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "INVALID_FIELD_KEY");
    }

    [Fact]
    public void Validate_SelectFieldWithoutOptions_Rejected()
    {
        var schema = new FormSchema(new[]
        {
            new FormFieldDefinition("category", FormFieldType.Select, "Category"),
        });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "MISSING_FIELD_OPTIONS");
    }

    [Fact]
    public void Validate_SelectFieldWithDuplicateOptionValues_Rejected()
    {
        var schema = new FormSchema(new[]
        {
            new FormFieldDefinition("category", FormFieldType.Select, "Category", Options: new[]
            {
                new FormFieldOption("a", "A"),
                new FormFieldOption("a", "A duplicate"),
            }),
        });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "DUPLICATE_FIELD_OPTION");
    }

    [Fact]
    public void Validate_MaxLengthLessThanMinLength_Rejected()
    {
        var schema = new FormSchema(new[]
        {
            new FormFieldDefinition("notes", FormFieldType.Text, "Notes", Validation: new FormFieldValidation(MinLength: 10, MaxLength: 5)),
        });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "INVALID_VALIDATION_RULE");
    }

    [Fact]
    public void Validate_NumericValidationOnTextField_Rejected()
    {
        var schema = new FormSchema(new[]
        {
            new FormFieldDefinition("notes", FormFieldType.Text, "Notes", Validation: new FormFieldValidation(MinValue: 1)),
        });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "INVALID_VALIDATION_RULE");
    }

    [Fact]
    public void Validate_InvalidRegexPattern_Rejected()
    {
        var schema = new FormSchema(new[]
        {
            new FormFieldDefinition("code", FormFieldType.Text, "Code", Validation: new FormFieldValidation(Pattern: "[")),
        });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "INVALID_VALIDATION_RULE");
    }

    [Fact]
    public void Validate_VisibilityReferencingUnknownField_Rejected()
    {
        var schema = new FormSchema(new[]
        {
            new FormFieldDefinition("employmentType", FormFieldType.Text, "Employment Type"),
            new FormFieldDefinition("otherType", FormFieldType.Text, "Other Type", Visibility: new FormFieldVisibility(
                new FormVisibilityCondition("doesNotExist", FormVisibilityOperator.Equals, "Other"))),
        });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "INVALID_VISIBILITY_RULE");
    }

    [Fact]
    public void Validate_VisibilityDependingOnSelf_Rejected()
    {
        var schema = new FormSchema(new[]
        {
            new FormFieldDefinition("x", FormFieldType.Text, "X", Visibility: new FormFieldVisibility(
                new FormVisibilityCondition("x", FormVisibilityOperator.Equals, "y"))),
        });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "INVALID_VISIBILITY_RULE");
    }

    [Fact]
    public void Validate_ValidVisibilityRule_Passes()
    {
        var schema = new FormSchema(new[]
        {
            new FormFieldDefinition("employmentType", FormFieldType.Select, "Employment Type", Options: new[] { new FormFieldOption("Other", "Other") }),
            new FormFieldDefinition("otherType", FormFieldType.Text, "Other Type", Visibility: new FormFieldVisibility(
                new FormVisibilityCondition("employmentType", FormVisibilityOperator.Equals, "Other"))),
        });

        var result = FormSchemaValidator.Validate(schema);

        Assert.True(result.IsValid);
    }
}
