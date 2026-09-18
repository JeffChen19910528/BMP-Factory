import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { FormRule } from '../../../types/form';
import { createField } from './formSchemaModel';
import { LanguageProvider } from '../../../i18n/LanguageContext';
import { RuleBuilder } from './RuleBuilder';

async function selectOption(user: ReturnType<typeof userEvent.setup>, combobox: HTMLElement, optionName: string | RegExp) {
  await user.click(combobox);
  const option = await screen.findByRole('option', { name: optionName });
  fireEvent.click(option);
}

const travelType = { ...createField('Select', new Set()), key: 'travelType', label: 'Travel Type', options: [{ value: 'BUSINESS', label: 'Business' }, { value: 'PERSONAL', label: 'Personal' }] };
const destination = { ...createField('Text', new Set()), key: 'destination', label: 'Destination' };

function renderBuilder(rules: FormRule[] = [], readOnly = false) {
  const onChange = vi.fn();
  const utils = render(
    <LanguageProvider initialLanguage="en-US">
      <RuleBuilder field={destination} allFields={[travelType, destination]} rules={rules} readOnly={readOnly} onChange={onChange} />
    </LanguageProvider>,
  );
  return { ...utils, onChange };
}

describe('RuleBuilder', () => {
  it('shows "no rules yet" when the field has none', () => {
    renderBuilder([]);
    expect(screen.getByText('No rules for this field yet.')).toBeInTheDocument();
  });

  it('adds a new Visibility rule targeting the selected field', async () => {
    const user = userEvent.setup();
    const { onChange } = renderBuilder([]);

    await user.click(screen.getByRole('button', { name: /Add Rule/i }));

    expect(onChange).toHaveBeenCalledWith([expect.objectContaining({ target: 'destination', type: 'Visibility' })]);
  });

  it('only shows rules that target the currently selected field', () => {
    const rules: FormRule[] = [
      { id: 'r1', target: 'destination', type: 'Visibility', condition: { kind: 'field', field: 'travelType', operator: 'Equals', value: 'BUSINESS' } },
      { id: 'r2', target: 'travelType', type: 'Required', condition: { kind: 'field', field: 'destination', operator: 'IsNotEmpty' } },
    ];
    renderBuilder(rules);

    expect(screen.queryByText('No rules for this field yet.')).not.toBeInTheDocument();
    // Only one rule row (r1) should render — r2 targets a different field.
    expect(screen.getAllByRole('button', { name: /Delete rule/i })).toHaveLength(1);
  });

  it('edits the condition field, operator, and value', async () => {
    const user = userEvent.setup();
    const rules: FormRule[] = [{ id: 'r1', target: 'destination', type: 'Visibility', condition: { kind: 'field', field: '', operator: 'Equals', value: '' } }];
    const { onChange } = renderBuilder(rules);

    const fieldSelect = screen.getAllByRole('combobox')[1];
    await selectOption(user, fieldSelect, 'Travel Type');

    expect(onChange).toHaveBeenCalledWith([
      expect.objectContaining({ condition: expect.objectContaining({ field: 'travelType' }) }),
    ]);
  });

  it('shows Select-typed options as the value picker when the source field is Select', async () => {
    const user = userEvent.setup();
    const rules: FormRule[] = [{ id: 'r1', target: 'destination', type: 'Visibility', condition: { kind: 'field', field: 'travelType', operator: 'Equals', value: '' } }];
    renderBuilder(rules);

    const comboboxes = screen.getAllByRole('combobox');
    await user.click(comboboxes[comboboxes.length - 1]);

    expect(await screen.findByRole('option', { name: 'Business' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Personal' })).toBeInTheDocument();
  });

  it('restricts operators to ones valid for the source field type', async () => {
    const user = userEvent.setup();
    const rules: FormRule[] = [{ id: 'r1', target: 'destination', type: 'Visibility', condition: { kind: 'field', field: 'travelType', operator: 'Equals', value: '' } }];
    renderBuilder(rules);

    const comboboxes = screen.getAllByRole('combobox');
    // travelType is a Select field — GreaterThan should not be offered.
    await user.click(comboboxes[2]);
    expect(screen.queryByRole('option', { name: 'greater than' })).not.toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'equals' })).toBeInTheDocument();
  });

  it('switches a rule to Calculated and shows the formula editor', async () => {
    const user = userEvent.setup();
    const rules: FormRule[] = [{ id: 'r1', target: 'destination', type: 'Visibility', condition: { kind: 'field', field: 'travelType', operator: 'Equals', value: 'BUSINESS' } }];
    const { onChange } = renderBuilder(rules);

    await selectOption(user, screen.getAllByRole('combobox')[0], 'Calculated');

    expect(onChange).toHaveBeenCalledWith([expect.objectContaining({ type: 'Calculated', formula: '' })]);
  });

  it('flags an invalid formula already present in the rule', () => {
    const rules: FormRule[] = [{ id: 'r1', target: 'destination', type: 'Calculated', formula: 'quantity * ' }];
    renderBuilder(rules);

    expect(screen.getByText(/Invalid formula/i)).toBeInTheDocument();
  });

  it('does not flag a valid formula', () => {
    const rules: FormRule[] = [{ id: 'r1', target: 'destination', type: 'Calculated', formula: 'quantity * unitPrice' }];
    renderBuilder(rules);

    expect(screen.queryByText(/Invalid formula/i)).not.toBeInTheDocument();
  });

  it('typing into the formula input reports the new value via onChange', async () => {
    const user = userEvent.setup();
    const rules: FormRule[] = [{ id: 'r1', target: 'destination', type: 'Calculated', formula: '' }];
    const { onChange } = renderBuilder(rules);

    const input = screen.getByPlaceholderText('e.g. quantity * unitPrice');
    await user.type(input, 'x');

    expect(onChange).toHaveBeenCalledWith([expect.objectContaining({ formula: 'x' })]);
  });

  it('deletes a rule', async () => {
    const user = userEvent.setup();
    const rules: FormRule[] = [{ id: 'r1', target: 'destination', type: 'Visibility', condition: { kind: 'field', field: 'travelType', operator: 'Equals', value: 'BUSINESS' } }];
    const { onChange } = renderBuilder(rules);

    await user.click(screen.getByRole('button', { name: 'Delete rule r1' }));
    expect(onChange).toHaveBeenCalledWith([]);
  });

  it('groups a leaf condition into an AND compound and can add/remove conditions', async () => {
    const user = userEvent.setup();
    const rules: FormRule[] = [{ id: 'r1', target: 'destination', type: 'Visibility', condition: { kind: 'field', field: 'travelType', operator: 'Equals', value: 'BUSINESS' } }];
    const { onChange, rerender } = renderBuilder(rules);

    await user.click(screen.getByRole('button', { name: /Group with AND\/OR/i }));
    expect(onChange).toHaveBeenCalledWith([
      expect.objectContaining({ condition: expect.objectContaining({ kind: 'compound', operator: 'And' }) }),
    ]);

    const grouped = onChange.mock.calls[0][0] as FormRule[];
    rerender(
      <LanguageProvider initialLanguage="en-US">
        <RuleBuilder field={destination} allFields={[travelType, destination]} rules={grouped} readOnly={false} onChange={onChange} />
      </LanguageProvider>,
    );

    await user.click(screen.getByRole('button', { name: /Add Condition/i }));
    const afterAdd = onChange.mock.calls.at(-1)![0] as FormRule[];
    const compoundCondition = afterAdd[0].condition as { conditions: unknown[] };
    expect(compoundCondition.conditions).toHaveLength(3);
  });

  it('shows an unsupported rule type read-only, never editable', () => {
    const rules: FormRule[] = [{ id: 'future1', target: 'destination', type: 'Unknown', someNewShape: true } as FormRule];
    renderBuilder(rules);

    expect(screen.getByText(/Unsupported rule type/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Delete rule future1/i })).not.toBeInTheDocument();
  });

  it('disables Add Rule and hides delete/inputs when readOnly', () => {
    const rules: FormRule[] = [{ id: 'r1', target: 'destination', type: 'Visibility', condition: { kind: 'field', field: 'travelType', operator: 'Equals', value: 'BUSINESS' } }];
    renderBuilder(rules, true);

    expect(screen.queryByRole('button', { name: /Add Rule/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Delete rule r1' })).not.toBeInTheDocument();
  });
});
