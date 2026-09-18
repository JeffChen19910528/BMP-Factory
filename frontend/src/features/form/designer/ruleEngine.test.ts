import { describe, expect, it } from 'vitest';
import type { FormFieldCondition, FormRule, FormSchema } from '../../../types/form';
import { evaluateForm, parseFormula, referencedFields, ruleReferencesField } from './ruleEngine';

function eq(field: string, value: string): FormFieldCondition {
  return { kind: 'field', field, operator: 'Equals', value };
}

describe('parseFormula', () => {
  it('parses a simple multiplication and reports referenced fields', () => {
    const node = parseFormula('quantity * unitPrice');
    expect(node).not.toBeNull();
    expect(referencedFields(node!).sort()).toEqual(['quantity', 'unitPrice']);
  });

  it('rejects unsupported syntax', () => {
    expect(parseFormula('quantity * ')).toBeNull();
    expect(parseFormula('quantity ^ 2')).toBeNull();
    expect(parseFormula('eval(quantity)')).toBeNull();
  });

  it('handles parentheses', () => {
    const node = parseFormula('(subtotal + tax) * 1');
    expect(node).not.toBeNull();
  });
});

describe('evaluateForm — conditional visibility/enabled/required', () => {
  const schema: FormSchema = {
    fields: [
      { key: 'travelType', type: 'Select', label: 'Travel Type', options: [{ value: 'BUSINESS', label: 'Business' }, { value: 'PERSONAL', label: 'Personal' }] },
      { key: 'destination', type: 'Text', label: 'Destination' },
    ],
    rules: [{ id: 'r1', target: 'destination', type: 'Visibility', condition: eq('travelType', 'BUSINESS') }],
  };

  it('shows destination when travelType is BUSINESS', () => {
    const state = evaluateForm(schema, { travelType: 'BUSINESS' });
    expect(state.fields.destination.visible).toBe(true);
  });

  it('hides destination when travelType is PERSONAL', () => {
    const state = evaluateForm(schema, { travelType: 'PERSONAL' });
    expect(state.fields.destination.visible).toBe(false);
  });

  it('a field with no rules defaults to visible/enabled/not-required', () => {
    const state = evaluateForm(schema, {});
    expect(state.fields.travelType).toEqual({ visible: true, enabled: true, required: false });
  });
});

describe('evaluateForm — enabled and conditional required', () => {
  const schema: FormSchema = {
    fields: [
      { key: 'status', type: 'Select', label: 'Status', options: [{ value: 'APPROVED', label: 'Approved' }, { value: 'PENDING', label: 'Pending' }] },
      { key: 'approvalComment', type: 'Text', label: 'Comment' },
    ],
    rules: [{ id: 'r1', target: 'approvalComment', type: 'Enabled', condition: eq('status', 'APPROVED') }],
  };

  it('enables approvalComment only when status is APPROVED', () => {
    expect(evaluateForm(schema, { status: 'APPROVED' }).fields.approvalComment.enabled).toBe(true);
    expect(evaluateForm(schema, { status: 'PENDING' }).fields.approvalComment.enabled).toBe(false);
  });

  it('conditional required is additive to a statically required field', () => {
    const requiredSchema: FormSchema = {
      fields: [{ key: 'name', type: 'Text', label: 'Name', required: true }],
      rules: [],
    };
    expect(evaluateForm(requiredSchema, {}).fields.name.required).toBe(true);
  });
});

describe('evaluateForm — compound AND/OR/NOT', () => {
  it('AND requires every child condition to be true', () => {
    const schema: FormSchema = {
      fields: [
        { key: 'amount', type: 'Number', label: 'Amount' },
        { key: 'expenseType', type: 'Select', label: 'Expense Type', options: [{ value: 'PURCHASE', label: 'Purchase' }] },
        { key: 'approverNote', type: 'Text', label: 'Note' },
      ],
      rules: [
        {
          id: 'r1',
          target: 'approverNote',
          type: 'Required',
          condition: {
            kind: 'compound',
            operator: 'And',
            conditions: [
              { kind: 'field', field: 'amount', operator: 'GreaterThan', value: '100000' },
              eq('expenseType', 'PURCHASE'),
            ],
          },
        },
      ],
    };

    expect(evaluateForm(schema, { amount: 200000, expenseType: 'PURCHASE' }).fields.approverNote.required).toBe(true);
    expect(evaluateForm(schema, { amount: 50, expenseType: 'PURCHASE' }).fields.approverNote.required).toBe(false);
  });

  it('NOT inverts its single child condition', () => {
    const schema: FormSchema = {
      fields: [
        { key: 'expenseType', type: 'Select', label: 'Expense Type', options: [{ value: 'TRAVEL', label: 'Travel' }] },
        { key: 'description', type: 'Text', label: 'Description' },
      ],
      rules: [
        {
          id: 'r1',
          target: 'description',
          type: 'Visibility',
          condition: { kind: 'compound', operator: 'Not', conditions: [eq('expenseType', 'TRAVEL')] },
        },
      ],
    };

    expect(evaluateForm(schema, { expenseType: 'TRAVEL' }).fields.description.visible).toBe(false);
    expect(evaluateForm(schema, { expenseType: 'OTHER' }).fields.description.visible).toBe(true);
  });
});

describe('evaluateForm — calculated fields', () => {
  it('computes and overwrites a spoofed client value', () => {
    const schema: FormSchema = {
      fields: [
        { key: 'quantity', type: 'Number', label: 'Quantity' },
        { key: 'unitPrice', type: 'Number', label: 'Unit Price' },
        { key: 'total', type: 'Number', label: 'Total' },
      ],
      rules: [{ id: 'r1', target: 'total', type: 'Calculated', formula: 'quantity * unitPrice' }],
    };

    const state = evaluateForm(schema, { quantity: 3, unitPrice: 100, total: 999999 });
    expect(state.values.total).toBe(300);
  });

  it('reports a division-by-zero calculation error instead of throwing', () => {
    const schema: FormSchema = {
      fields: [
        { key: 'amount', type: 'Number', label: 'Amount' },
        { key: 'divisor', type: 'Number', label: 'Divisor' },
        { key: 'result', type: 'Number', label: 'Result' },
      ],
      rules: [{ id: 'r1', target: 'result', type: 'Calculated', formula: 'amount / divisor' }],
    };

    const state = evaluateForm(schema, { amount: 10, divisor: 0 });
    expect(state.calculationErrors.result).toBe('DIVISION_BY_ZERO');
  });
});

describe('evaluateForm — unknown rule types are ignored, not evaluated', () => {
  it('a rule with an unrecognized type has no effect', () => {
    const schema: FormSchema = {
      fields: [{ key: 'a', type: 'Text', label: 'A' }],
      rules: [{ id: 'future1', target: 'a', type: 'Unknown', someNewShape: true } as FormRule],
    };

    const state = evaluateForm(schema, {});
    expect(state.fields.a).toEqual({ visible: true, enabled: true, required: false });
  });
});

describe('ruleReferencesField', () => {
  it('detects a field referenced in a leaf condition', () => {
    const rule: FormRule = { id: 'r1', target: 'b', type: 'Visibility', condition: eq('a', 'x') };
    expect(ruleReferencesField(rule, 'a')).toBe(true);
    expect(ruleReferencesField(rule, 'c')).toBe(false);
  });

  it('detects a field referenced in a formula', () => {
    const rule: FormRule = { id: 'r1', target: 'total', type: 'Calculated', formula: 'quantity * unitPrice' };
    expect(ruleReferencesField(rule, 'unitPrice')).toBe(true);
    expect(ruleReferencesField(rule, 'other')).toBe(false);
  });
});
