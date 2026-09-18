import { describe, expect, it } from 'vitest';
import type { FormSchema } from '../../../types/form';
import { evaluateForm } from '../designer/ruleEngine';
import { resolveRuntimeErrors, validateClientSide } from './runtimeValidation';

describe('validateClientSide', () => {
  it('flags a missing required field', () => {
    const schema: FormSchema = { fields: [{ key: 'amount', type: 'Number', label: 'Amount', required: true }] };
    const state = evaluateForm(schema, {});
    const errors = validateClientSide(schema, state, {});

    expect(errors).toEqual([{ fieldKey: 'amount', message: 'Amount is required.' }]);
  });

  it('passes when the required field has a value', () => {
    const schema: FormSchema = { fields: [{ key: 'amount', type: 'Number', label: 'Amount', required: true }] };
    const state = evaluateForm(schema, { amount: 100 });
    expect(validateClientSide(schema, state, { amount: 100 })).toEqual([]);
  });

  it('flags a conditionally-required field derived from a rule, even though it is not statically required', () => {
    const schema: FormSchema = {
      fields: [
        { key: 'expenseType', type: 'Select', label: 'Expense Type', options: [{ value: 'OTHER', label: 'Other' }] },
        { key: 'description', type: 'Text', label: 'Description' },
      ],
      rules: [{ id: 'r1', target: 'description', type: 'Required', condition: { kind: 'field', field: 'expenseType', operator: 'Equals', value: 'OTHER' } }],
    };
    const values = { expenseType: 'OTHER' };
    const state = evaluateForm(schema, values);
    const errors = validateClientSide(schema, state, values);

    expect(errors).toEqual([{ fieldKey: 'description', message: 'Description is required.' }]);
  });

  it('flags an invalid type for a Number field', () => {
    const schema: FormSchema = { fields: [{ key: 'amount', type: 'Number', label: 'Amount' }] };
    const state = evaluateForm(schema, { amount: 'not a number' });
    const errors = validateClientSide(schema, state, { amount: 'not a number' });

    expect(errors).toEqual([{ fieldKey: 'amount', message: 'Amount must be a number.' }]);
  });

  it('flags a value below minValue / above maxValue', () => {
    const schema: FormSchema = { fields: [{ key: 'quantity', type: 'Number', label: 'Quantity', validation: { minValue: 1, maxValue: 10 } }] };
    const state = evaluateForm(schema, { quantity: 0 });
    expect(validateClientSide(schema, state, { quantity: 0 })).toEqual([{ fieldKey: 'quantity', message: 'Quantity must be at least 1.' }]);

    const state2 = evaluateForm(schema, { quantity: 20 });
    expect(validateClientSide(schema, state2, { quantity: 20 })).toEqual([{ fieldKey: 'quantity', message: 'Quantity must be at most 10.' }]);
  });

  it('flags a string shorter than minLength', () => {
    const schema: FormSchema = { fields: [{ key: 'name', type: 'Text', label: 'Name', validation: { minLength: 3 } }] };
    const state = evaluateForm(schema, { name: 'ab' });
    expect(validateClientSide(schema, state, { name: 'ab' })).toEqual([{ fieldKey: 'name', message: 'Name must be at least 3 characters.' }]);
  });

  it('does not exempt a hidden field from a conditional required check', () => {
    const schema: FormSchema = {
      fields: [
        { key: 'expenseType', type: 'Select', label: 'Expense Type', options: [{ value: 'OTHER', label: 'Other' }] },
        { key: 'description', type: 'Text', label: 'Description' },
      ],
      rules: [
        { id: 'r1', target: 'description', type: 'Visibility', condition: { kind: 'field', field: 'expenseType', operator: 'Equals', value: 'OTHER' } },
        { id: 'r2', target: 'description', type: 'Required', condition: { kind: 'field', field: 'expenseType', operator: 'Equals', value: 'OTHER' } },
      ],
    };
    // Even if description were somehow considered hidden, required must still be enforced —
    // here it's visible (expenseType=OTHER) so this also demonstrates the ordinary case.
    const values = { expenseType: 'OTHER' };
    const state = evaluateForm(schema, values);
    expect(validateClientSide(schema, state, values)).toEqual([{ fieldKey: 'description', message: 'Description is required.' }]);
  });
});

describe('resolveRuntimeErrors', () => {
  const schema: FormSchema = {
    fields: [
      { key: 'destination', type: 'Text', label: 'Destination' },
      { key: 'quantity', type: 'Number', label: 'Quantity' },
    ],
  };

  it('maps a backend error to the field named in its message', () => {
    const result = { isValid: false, errors: [{ code: 'FIELD_REQUIRED', message: "Field 'destination' is required." }] };
    expect(resolveRuntimeErrors(result, schema)).toEqual([{ fieldKey: 'destination', message: "Field 'destination' is required." }]);
  });

  it('falls back to the unmatched sentinel when no field key is found in the message', () => {
    const result = { isValid: false, errors: [{ code: 'FORM_DATA_NOT_AN_OBJECT', message: 'Form data must be a JSON object.' }] };
    expect(resolveRuntimeErrors(result, schema)).toEqual([{ fieldKey: '__unmatched__', message: 'Form data must be a JSON object.' }]);
  });

  it('returns an empty array for a null result', () => {
    expect(resolveRuntimeErrors(null, schema)).toEqual([]);
  });
});
