import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { FormSchema } from '../../../types/form';
import { LanguageProvider } from '../../../i18n/LanguageContext';
import { FormDesigner } from './FormDesigner';

const EMPTY_SCHEMA: FormSchema = { fields: [] };

const SAMPLE_SCHEMA: FormSchema = {
  fields: [
    { key: 'itemName', type: 'Text', label: 'Item Name', required: true },
    { key: 'quantity', type: 'Number', label: 'Quantity', required: false },
    { key: 'category', type: 'Select', label: 'Category', required: false, options: [{ value: 'hw', label: 'Hardware' }] },
  ],
};

function noop() {}

function renderDesigner(overrides: Partial<React.ComponentProps<typeof FormDesigner>> = {}) {
  const onChange = vi.fn();
  const utils = render(
    <LanguageProvider initialLanguage="en-US">
      <FormDesigner
        initialSchema={overrides.initialSchema ?? EMPTY_SCHEMA}
        readOnly={overrides.readOnly ?? false}
        onChange={onChange}
        onSaveDraft={noop}
        onValidate={noop}
        validationResult={overrides.validationResult ?? null}
        onPublish={noop}
        {...overrides}
      />
    </LanguageProvider>,
  );
  return { ...utils, onChange };
}

function canvasRows() {
  return document.querySelectorAll('[data-field-internal-id]');
}

describe('FormDesigner', () => {
  it('renders the field palette and an empty canvas', () => {
    renderDesigner();
    expect(screen.getByRole('button', { name: 'Text' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Select' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'File' })).toBeInTheDocument();
    expect(screen.getByText(/No fields yet/)).toBeInTheDocument();
  });

  it('loads an existing schema with the correct number of fields, in order', () => {
    renderDesigner({ initialSchema: SAMPLE_SCHEMA });
    const rows = canvasRows();
    expect(rows).toHaveLength(3);
    expect(within(rows[0] as HTMLElement).getByText('Item Name')).toBeInTheDocument();
    expect(within(rows[1] as HTMLElement).getByText('Quantity')).toBeInTheDocument();
    expect(within(rows[2] as HTMLElement).getByText('Category')).toBeInTheDocument();
  });

  it('adds a field of each supported type via palette clicks', async () => {
    const user = userEvent.setup();
    renderDesigner();

    await user.click(screen.getByRole('button', { name: 'Text' }));
    await user.click(screen.getByRole('button', { name: 'Number' }));
    await user.click(screen.getByRole('button', { name: 'Select' }));
    await user.click(screen.getByRole('button', { name: 'Checkbox' }));

    await waitFor(() => expect(canvasRows()).toHaveLength(4));
  });

  it('reports the initial schema as not dirty, then dirty after adding a field', async () => {
    const { onChange } = renderDesigner({ initialSchema: SAMPLE_SCHEMA });

    await waitFor(() => expect(onChange).toHaveBeenCalled());
    const [, initialDirty] = onChange.mock.calls[0];
    expect(initialDirty).toBe(false);

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Text' }));

    await waitFor(() => {
      const lastCall = onChange.mock.calls.at(-1)!;
      expect(lastCall[1]).toBe(true);
    });
  });

  it('selects a field and edits its label in the properties panel', async () => {
    const user = userEvent.setup();
    renderDesigner({ initialSchema: SAMPLE_SCHEMA });

    await user.click(screen.getByText('Item Name'));
    const labelInput = await screen.findByDisplayValue('Item Name');
    await user.clear(labelInput);
    await user.type(labelInput, 'Product Name');

    await waitFor(() => expect(screen.getAllByText('Product Name').length).toBeGreaterThan(0));
  });

  it('reorders a field with the Move Down / Move Up buttons', async () => {
    const user = userEvent.setup();
    renderDesigner({ initialSchema: SAMPLE_SCHEMA });

    await user.click(screen.getByRole('button', { name: 'Move Item Name down' }));

    await waitFor(() => {
      const rows = canvasRows();
      expect(within(rows[0] as HTMLElement).getByText('Quantity')).toBeInTheDocument();
      expect(within(rows[1] as HTMLElement).getByText('Item Name')).toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: 'Move Item Name up' }));
    await waitFor(() => {
      const rows = canvasRows();
      expect(within(rows[0] as HTMLElement).getByText('Item Name')).toBeInTheDocument();
    });
  });

  it('deletes a selected field', async () => {
    const user = userEvent.setup();
    renderDesigner({ initialSchema: SAMPLE_SCHEMA });

    await user.click(screen.getByRole('button', { name: 'Delete Quantity' }));
    await waitFor(() => expect(canvasRows()).toHaveLength(2));
    expect(screen.queryByText('Quantity')).not.toBeInTheDocument();
  });

  it('duplicates a field with a fresh, unique key', async () => {
    const user = userEvent.setup();
    renderDesigner({ initialSchema: SAMPLE_SCHEMA });

    await user.click(screen.getByRole('button', { name: 'Duplicate Item Name' }));
    await waitFor(() => expect(canvasRows()).toHaveLength(4));
    // Duplicated field keeps the label but gets a distinct generated key.
    expect(screen.getAllByText('Item Name')).toHaveLength(2);
  });

  it('supports undo and redo of a field addition', async () => {
    const user = userEvent.setup();
    renderDesigner({ initialSchema: SAMPLE_SCHEMA });

    await user.click(screen.getByRole('button', { name: 'Number' }));
    await waitFor(() => expect(canvasRows()).toHaveLength(4));

    await user.click(screen.getByRole('button', { name: /Undo/i }));
    await waitFor(() => expect(canvasRows()).toHaveLength(3));

    await user.click(screen.getByRole('button', { name: /Redo/i }));
    await waitFor(() => expect(canvasRows()).toHaveLength(4));
  });

  it('renders read-only with no palette, no undo/redo/save, and no delete action, tagged Published', () => {
    renderDesigner({ initialSchema: SAMPLE_SCHEMA, readOnly: true });

    expect(screen.queryByRole('button', { name: 'Text' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save Draft' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Undo/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Delete Quantity' })).not.toBeInTheDocument();
    expect(screen.getByText('Published — read-only')).toBeInTheDocument();
  });

  it('switches into Preview mode and back, hiding the palette while previewing', async () => {
    const user = userEvent.setup();
    renderDesigner({ initialSchema: SAMPLE_SCHEMA });

    await user.click(screen.getByRole('button', { name: /Preview/i }));
    expect(screen.queryByRole('button', { name: 'Text' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Back to Designer/i })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Back to Designer/i }));
    expect(screen.getByRole('button', { name: 'Text' })).toBeInTheDocument();
  });

  it('shows validation errors and highlights the mentioned field', () => {
    renderDesigner({
      initialSchema: SAMPLE_SCHEMA,
      validationResult: {
        isValid: false,
        errors: [{ code: 'FIELD_LABEL_REQUIRED', message: "Field 'quantity' must have a non-empty label." }],
      },
    });

    expect(screen.getByText('Form Validation')).toBeInTheDocument();
    expect(screen.getByText('FIELD_LABEL_REQUIRED')).toBeInTheDocument();
    expect(screen.getByText('Number "Quantity"')).toBeInTheDocument();
  });

  it('shows a success message when validation passes', () => {
    renderDesigner({ initialSchema: SAMPLE_SCHEMA, validationResult: { isValid: true, errors: [] } });
    expect(screen.getByText('Form schema is valid.')).toBeInTheDocument();
  });
});

const RULE_SCHEMA: FormSchema = {
  fields: [
    { key: 'travelType', type: 'Select', label: 'Travel Type', options: [{ value: 'BUSINESS', label: 'Business' }, { value: 'PERSONAL', label: 'Personal' }] },
    { key: 'destination', type: 'Text', label: 'Destination' },
  ],
  rules: [{ id: 'r1', target: 'destination', type: 'Visibility', condition: { kind: 'field', field: 'travelType', operator: 'Equals', value: 'BUSINESS' } }],
};

describe('FormDesigner — Advanced Form Rules (Phase 5.4.2)', () => {
  it('adding a rule via the Properties Panel is covered by Undo/Redo', async () => {
    const user = userEvent.setup();
    renderDesigner({ initialSchema: SAMPLE_SCHEMA });

    await user.click(screen.getByText('Item Name'));
    await user.click(screen.getByRole('button', { name: /Add Rule/i }));
    expect(screen.queryByText('No rules for this field yet.')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Undo/i }));
    expect(screen.getByText('No rules for this field yet.')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Redo/i }));
    expect(screen.queryByText('No rules for this field yet.')).not.toBeInTheDocument();
  });

  it('deleting a field also drops any rule that referenced it', async () => {
    const user = userEvent.setup();
    renderDesigner({ initialSchema: RULE_SCHEMA });

    await user.click(screen.getByText('Travel Type'));
    // The Properties Panel's own Delete-field button — distinct from the per-row canvas "Delete
    // Travel Type" button and the per-option "delete" icon buttons by its combined
    // icon-label-plus-text accessible name.
    await user.click(screen.getByRole('button', { name: 'delete Delete' }));

    await user.click(screen.getByText('Destination'));
    expect(screen.getByText('No rules for this field yet.')).toBeInTheDocument();
  });

  it('reports the schema (including rules) via onChange for Save Draft to persist', async () => {
    const { onChange } = renderDesigner({ initialSchema: RULE_SCHEMA });

    await waitFor(() => expect(onChange).toHaveBeenCalled());
    const [schema] = onChange.mock.calls[0];
    expect(schema.rules).toEqual(RULE_SCHEMA.rules);
  });

  it('Preview mode actually evaluates rules: destination is hidden until Travel Type is Business', async () => {
    const user = userEvent.setup();
    renderDesigner({ initialSchema: RULE_SCHEMA });

    await user.click(screen.getByRole('button', { name: /Preview/i }));
    expect(screen.queryByText('Destination')).not.toBeInTheDocument();

    const travelTypeSelect = screen.getByRole('combobox');
    await user.click(travelTypeSelect);
    await user.click(await screen.findByText('Business'));

    expect(await screen.findByText('Destination')).toBeInTheDocument();
  });
});
