import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { createField, type DesignerField } from './formSchemaModel';
import { LanguageProvider } from '../../../i18n/LanguageContext';
import { PropertiesPanel } from './PropertiesPanel';

function renderPanel(field: DesignerField | null, existingKeys: ReadonlySet<string> = new Set(), readOnly = false) {
  const onChange = vi.fn();
  const onDelete = vi.fn();
  const utils = render(
    <LanguageProvider initialLanguage="en-US">
      <PropertiesPanel field={field} existingKeys={existingKeys} readOnly={readOnly} onChange={onChange} onDelete={onDelete} />
    </LanguageProvider>,
  );
  return { ...utils, onChange, onDelete };
}

describe('PropertiesPanel', () => {
  it('shows an empty state when no field is selected', () => {
    renderPanel(null);
    expect(screen.getByText('Select a field to edit its properties')).toBeInTheDocument();
  });

  it('shows only type-appropriate properties for a Text field (MinLength/MaxLength, no Options)', () => {
    const field = createField('Text', new Set());
    renderPanel(field);

    expect(screen.getByText('Key')).toBeInTheDocument();
    expect(screen.getByText('Label')).toBeInTheDocument();
    expect(screen.getByText('Placeholder')).toBeInTheDocument();
    expect(screen.getByText('Min length')).toBeInTheDocument();
    expect(screen.getByText('Max length')).toBeInTheDocument();
    expect(screen.queryByText('Options')).not.toBeInTheDocument();
    expect(screen.queryByText('Min value')).not.toBeInTheDocument();
  });

  it('shows only type-appropriate properties for a Number field (MinValue/MaxValue, no length validation)', () => {
    const field = createField('Number', new Set());
    renderPanel(field);

    expect(screen.getByText('Min value')).toBeInTheDocument();
    expect(screen.getByText('Max value')).toBeInTheDocument();
    expect(screen.queryByText('Min length')).not.toBeInTheDocument();
    expect(screen.queryByText('Options')).not.toBeInTheDocument();
  });

  it('shows an Options editor for a Select field, seeded with one option', () => {
    const field = createField('Select', new Set());
    renderPanel(field);

    expect(screen.getByText('Options')).toBeInTheDocument();
    expect(screen.getByDisplayValue('Option 1')).toBeInTheDocument();
    expect(screen.getByDisplayValue('option1')).toBeInTheDocument();
  });

  it('adds an option to a Select field via Add Option', async () => {
    const user = userEvent.setup();
    const field = createField('Select', new Set());
    const { onChange } = renderPanel(field);

    await user.click(screen.getByRole('button', { name: /Add Option/i }));

    expect(onChange).toHaveBeenCalledWith(
      field.internalId,
      expect.objectContaining({ options: [{ value: 'option1', label: 'Option 1' }, { value: 'option2', label: 'Option 2' }] }),
    );
  });

  it('flags a duplicate option value', () => {
    const field: DesignerField = {
      ...createField('Select', new Set()),
      options: [{ value: 'a', label: 'A' }, { value: 'a', label: 'B' }],
    };
    renderPanel(field);
    expect(screen.getAllByText('Duplicate option value.')).toHaveLength(2);
  });

  it('flags an empty key', () => {
    const field: DesignerField = { ...createField('Text', new Set()), key: '' };
    renderPanel(field);
    expect(screen.getByText('Key is required.')).toBeInTheDocument();
  });

  it('flags an unsafe-identifier key format', () => {
    const field: DesignerField = { ...createField('Text', new Set()), key: '1-bad key' };
    renderPanel(field);
    expect(screen.getByText(/must start with a letter or underscore/)).toBeInTheDocument();
  });

  it('flags a duplicate key against existingKeys', () => {
    const field: DesignerField = { ...createField('Text', new Set()), key: 'itemName' };
    renderPanel(field, new Set(['itemName']));
    expect(screen.getByText('This key is already used by another field.')).toBeInTheDocument();
  });

  it('warns that renaming a key can break data semantics, when the key is otherwise valid', () => {
    const field = createField('Text', new Set());
    renderPanel(field);
    expect(screen.getByText(/Renaming this key changes which JSON property/)).toBeInTheDocument();
  });

  it('calls onChange when editing the Required checkbox', async () => {
    const user = userEvent.setup();
    const field = createField('Text', new Set());
    const { onChange } = renderPanel(field);

    await user.click(screen.getByRole('checkbox', { name: 'Required' }));
    expect(onChange).toHaveBeenCalledWith(field.internalId, { required: true });
  });

  it('renders an unsupported field type read-only with its raw JSON, no editable inputs', () => {
    const field: DesignerField = {
      internalId: 'x1',
      key: 'sig',
      type: 'Signature',
      label: 'Signature',
      description: null,
      required: false,
      defaultValue: null,
      placeholder: null,
      options: null,
      validation: null,
      readOnly: false,
      supported: false,
      raw: { key: 'sig', type: 'Signature', label: 'Signature' },
    };
    renderPanel(field);

    expect(screen.getByText(/isn't supported by the Form Designer yet/)).toBeInTheDocument();
    expect(screen.getByText(/"type": "Signature"/)).toBeInTheDocument();
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
  });

  it('disables all inputs and hides Delete/Add Option when readOnly', () => {
    const field = createField('Select', new Set());
    renderPanel(field, new Set(), true);

    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Add Option/i })).not.toBeInTheDocument();
    const keyInput = screen.getByDisplayValue(field.key);
    expect(keyInput).toBeDisabled();
  });
});
