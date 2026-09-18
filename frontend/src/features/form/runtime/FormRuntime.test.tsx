import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import type { FormSchema } from '../../../types/form';
import { LanguageProvider } from '../../../i18n/LanguageContext';
import { FormRuntime } from './FormRuntime';

vi.mock('../../../services/userService', () => ({
  listUsers: vi.fn().mockResolvedValue([{ id: 'user-1', username: 'alice', displayName: 'Alice Smith', email: 'alice@bpm.local', departmentId: null, isActive: true }]),
}));
vi.mock('../../../services/departmentService', () => ({
  listDepartments: vi.fn().mockResolvedValue([{ id: 'dept-1', name: 'Finance', organizationId: 'org-1', parentId: null, managerUserId: null }]),
}));
vi.mock('./hooks', () => ({
  useAttachments: () => ({ data: [] }),
  useUploadAttachment: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useDeleteAttachment: () => ({ mutate: vi.fn() }),
}));

function noop() {}

function renderRuntime(overrides: Partial<React.ComponentProps<typeof FormRuntime>> = {}) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onChange = vi.fn();
  const utils = render(
    <LanguageProvider initialLanguage="en-US">
      <QueryClientProvider client={queryClient}>
        <FormRuntime
          schema={overrides.schema ?? { fields: [] }}
          formInstanceId="instance-1"
          initialData={overrides.initialData ?? {}}
          readOnly={overrides.readOnly ?? false}
          onChange={onChange}
          onSaveDraft={noop}
          onSubmit={noop}
          {...overrides}
        />
      </QueryClientProvider>
    </LanguageProvider>,
  );
  return { ...utils, onChange };
}

const EXPENSE_SCHEMA: FormSchema = {
  fields: [
    { key: 'expenseType', type: 'Select', label: 'Expense Type', required: true, options: [{ value: 'OTHER', label: 'Other' }, { value: 'TRAVEL', label: 'Travel' }] },
    { key: 'description', type: 'Text', label: 'Description' },
    { key: 'amount', type: 'Number', label: 'Amount', required: true },
  ],
  rules: [
    { id: 'r1', target: 'description', type: 'Visibility', condition: { kind: 'field', field: 'expenseType', operator: 'Equals', value: 'OTHER' } },
    { id: 'r2', target: 'description', type: 'Required', condition: { kind: 'field', field: 'expenseType', operator: 'Equals', value: 'OTHER' } },
  ],
};

describe('FormRuntime — rendering', () => {
  it('renders text/number/select field types from the schema', () => {
    renderRuntime({ schema: EXPENSE_SCHEMA });
    expect(screen.getByText('Expense Type')).toBeInTheDocument();
    expect(screen.getByText('Amount')).toBeInTheDocument();
  });

  it('renders a checkbox field', () => {
    renderRuntime({ schema: { fields: [{ key: 'agree', type: 'Checkbox', label: 'I agree' }] } });
    expect(screen.getByRole('checkbox')).toBeInTheDocument();
  });

  it('renders a textarea field', () => {
    renderRuntime({ schema: { fields: [{ key: 'notes', type: 'Textarea', label: 'Notes' }] } });
    expect(document.querySelector('textarea')).toBeInTheDocument();
  });

  it('renders a currency field', () => {
    renderRuntime({ schema: { fields: [{ key: 'total', type: 'Currency', label: 'Total' }] } });
    expect(screen.getByText('Total')).toBeInTheDocument();
  });

  it('renders a date field', () => {
    renderRuntime({ schema: { fields: [{ key: 'when', type: 'Date', label: 'When' }] } });
    expect(screen.getByText('When')).toBeInTheDocument();
  });

  it('renders a radio field', () => {
    renderRuntime({ schema: { fields: [{ key: 'choice', type: 'Radio', label: 'Choice', options: [{ value: 'a', label: 'A' }] }] } });
    expect(screen.getByRole('radio')).toBeInTheDocument();
  });

  it('renders a user field with real user options', async () => {
    const user = userEvent.setup();
    renderRuntime({ schema: { fields: [{ key: 'owner', type: 'User', label: 'Owner' }] } });

    await user.click(screen.getByRole('combobox'));
    expect(await screen.findByText('Alice Smith')).toBeInTheDocument();
  });

  it('renders a department field with real department options', async () => {
    const user = userEvent.setup();
    renderRuntime({ schema: { fields: [{ key: 'dept', type: 'Department', label: 'Department' }] } });

    await user.click(screen.getByRole('combobox'));
    expect(await screen.findByText('Finance')).toBeInTheDocument();
  });

  it('renders a file field with an upload button', () => {
    renderRuntime({ schema: { fields: [{ key: 'attachment', type: 'File', label: 'Attachment' }] } });
    expect(screen.getByRole('button', { name: /Choose File/i })).toBeInTheDocument();
  });
});

describe('FormRuntime — rules', () => {
  it('hides a conditionally-visible field until its condition matches', async () => {
    const user = userEvent.setup();
    renderRuntime({ schema: EXPENSE_SCHEMA });

    expect(screen.queryByText('Description')).not.toBeInTheDocument();

    await user.click(screen.getAllByRole('combobox')[0]);
    await user.click(await screen.findByText('Other'));

    expect(await screen.findByText('Description')).toBeInTheDocument();
  });

  it('marks a conditionally-required field with an asterisk once visible', async () => {
    const user = userEvent.setup();
    renderRuntime({ schema: EXPENSE_SCHEMA });

    await user.click(screen.getAllByRole('combobox')[0]);
    await user.click(await screen.findByText('Other'));

    await screen.findByText('Description');
    expect(screen.getAllByText('*').length).toBeGreaterThan(0);
  });

  it('shows a calculated field as disabled and reflects the computed value', () => {
    const schema: FormSchema = {
      fields: [
        { key: 'quantity', type: 'Number', label: 'Quantity' },
        { key: 'unitPrice', type: 'Number', label: 'Unit Price' },
        { key: 'total', type: 'Number', label: 'Total' },
      ],
      rules: [{ id: 'r1', target: 'total', type: 'Calculated', formula: 'quantity * unitPrice' }],
    };
    renderRuntime({ schema, initialData: { quantity: 3, unitPrice: 100 } });

    const spinButtons = screen.getAllByRole('spinbutton');
    const totalInput = spinButtons[spinButtons.length - 1];
    expect(totalInput).toBeDisabled();
    expect(totalInput).toHaveValue('300');
  });

  it('renders a value the backend already applied as a default at instance creation (FormEngine.BuildInitialDataJson) — the Runtime never recomputes defaults itself', () => {
    const schema: FormSchema = { fields: [{ key: 'country', type: 'Text', label: 'Country', defaultValue: 'Taiwan' }] };
    // A freshly created FormInstance's GET .../data already reflects the applied default — this
    // is what initialData looks like in that real scenario, not {}.
    renderRuntime({ schema, initialData: { country: 'Taiwan' } });

    expect(screen.getByDisplayValue('Taiwan')).toBeInTheDocument();
  });
});

describe('FormRuntime — validation', () => {
  it('shows a client-side required error next to the field on Submit, without calling onSubmit', async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn();
    renderRuntime({ schema: EXPENSE_SCHEMA, onSubmit });

    await user.click(screen.getByRole('button', { name: 'Submit' }));

    expect(await screen.findByText(/Expense Type is required/i)).toBeInTheDocument();
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('calls onSubmit once client validation passes', async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn();
    renderRuntime({
      schema: { fields: [{ key: 'amount', type: 'Number', label: 'Amount', required: true }] },
      initialData: { amount: 100 },
      onSubmit,
    });

    await user.click(screen.getByRole('button', { name: 'Submit' }));
    expect(onSubmit).toHaveBeenCalled();
  });

  it('maps a backend validation error to the corresponding field', () => {
    renderRuntime({
      schema: { fields: [{ key: 'destination', type: 'Text', label: 'Destination' }] },
      backendResult: { isValid: false, errors: [{ code: 'FIELD_REQUIRED', message: "Field 'destination' is required." }] },
    });

    expect(screen.getByText(/Field 'destination' is required\./)).toBeInTheDocument();
  });

  it('shows unmatched backend errors in a generic banner rather than silently dropping them', () => {
    renderRuntime({
      schema: { fields: [{ key: 'destination', type: 'Text', label: 'Destination' }] },
      backendResult: { isValid: false, errors: [{ code: 'FORM_DATA_NOT_AN_OBJECT', message: 'Form data must be a JSON object.' }] },
    });

    expect(screen.getByText('Submission failed')).toBeInTheDocument();
    expect(screen.getByText('Form data must be a JSON object.')).toBeInTheDocument();
  });
});

describe('FormRuntime — lifecycle / UX', () => {
  it('reports dirty=false for the initial load, then dirty=true after an edit', async () => {
    const user = userEvent.setup();
    const { onChange } = renderRuntime({ schema: { fields: [{ key: 'name', type: 'Text', label: 'Name' }] }, initialData: { name: 'John' } });

    await waitFor(() => expect(onChange).toHaveBeenCalled());
    expect(onChange.mock.calls[0][1]).toBe(false);

    await user.type(screen.getByRole('textbox'), '!');
    await waitFor(() => expect(onChange.mock.calls.at(-1)![1]).toBe(true));
  });

  it('preserves previously saved data when resuming a draft (does not reset to defaults)', () => {
    const schema: FormSchema = { fields: [{ key: 'quantity', type: 'Number', label: 'Quantity', defaultValue: '1' }] };
    renderRuntime({ schema, initialData: { quantity: 5 } });

    expect(screen.getByDisplayValue('5')).toBeInTheDocument();
    expect(screen.queryByDisplayValue('1')).not.toBeInTheDocument();
  });

  it('renders fully read-only with no Save Draft / Submit buttons when readOnly', () => {
    renderRuntime({ schema: { fields: [{ key: 'name', type: 'Text', label: 'Name' }] }, readOnly: true });

    expect(screen.queryByRole('button', { name: 'Save Draft' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Submit' })).not.toBeInTheDocument();
    expect(screen.getByText('Submitted — read-only')).toBeInTheDocument();
  });

  it('disables every field when read-only', () => {
    renderRuntime({ schema: { fields: [{ key: 'name', type: 'Text', label: 'Name' }] }, readOnly: true, initialData: { name: 'John' } });
    expect(screen.getByRole('textbox')).toBeDisabled();
  });

  it('calls onSaveDraft when Save Draft is clicked', async () => {
    const user = userEvent.setup();
    const onSaveDraft = vi.fn();
    renderRuntime({ schema: { fields: [{ key: 'name', type: 'Text', label: 'Name' }] }, onSaveDraft });

    await user.click(screen.getByRole('button', { name: 'Save Draft' }));
    expect(onSaveDraft).toHaveBeenCalled();
  });
});
