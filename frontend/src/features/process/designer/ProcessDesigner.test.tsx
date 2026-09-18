import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import type { WorkflowDefinition } from '../../../types/process';
import { LanguageProvider } from '../../../i18n/LanguageContext';
import { ProcessDesigner } from './ProcessDesigner';

// PropertiesPanel's assignment/form pickers (AssignmentValueInput, the Form select) call real
// TanStack Query hooks against these services — mocked here with small fixtures so selecting a
// node in a test never makes a real network call.
vi.mock('../../../services/roleService', () => ({
  listRoles: vi.fn().mockResolvedValue([{ id: 'role-1', name: 'Manager' }]),
}));
vi.mock('../../../services/departmentService', () => ({
  listDepartments: vi.fn().mockResolvedValue([{ id: 'dept-1', name: 'Finance', organizationId: 'org-1', parentId: null, managerUserId: null }]),
}));
vi.mock('../../../services/userService', () => ({
  listUsers: vi.fn().mockResolvedValue([{ id: 'user-1', username: 'alice', displayName: 'Alice', email: 'alice@bpm.local', departmentId: null, isActive: true }]),
}));
vi.mock('../../../services/formDefinitionService', () => ({
  listFormDefinitions: vi
    .fn()
    .mockResolvedValue([{ id: 'form-1', key: 'purchase-request', name: 'Purchase Request', description: null, category: null, status: 'Published', currentVersionId: 'v-1' }]),
}));

const EMPTY_DEFINITION: WorkflowDefinition = { nodes: [], transitions: [] };

const SEQUENTIAL_DEFINITION: WorkflowDefinition = {
  nodes: [
    { id: 'start', type: 'Start', name: 'Start' },
    { id: 'approval', type: 'ApprovalTask', name: 'Manager Approval', approval: { policy: 'AnyOne', assignments: [{ type: 'Role', value: 'Manager' }] } },
    { id: 'end', type: 'End', name: 'End' },
  ],
  transitions: [
    { id: 't1', source: 'start', target: 'approval' },
    { id: 't2', source: 'approval', target: 'end' },
  ],
};

function noop() {}

function renderDesigner(overrides: Partial<React.ComponentProps<typeof ProcessDesigner>> = {}) {
  const onChange = vi.fn();
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const utils = render(
    <LanguageProvider initialLanguage="en-US">
      <QueryClientProvider client={queryClient}>
        <ProcessDesigner
          initialDefinition={overrides.initialDefinition ?? EMPTY_DEFINITION}
          readOnly={overrides.readOnly ?? false}
          onChange={onChange}
          onSaveDraft={noop}
          onValidate={noop}
          validationResult={overrides.validationResult ?? null}
          onPublish={noop}
          {...overrides}
        />
      </QueryClientProvider>
    </LanguageProvider>,
  );
  return { ...utils, onChange };
}

describe('ProcessDesigner', () => {
  it('renders the palette and an empty canvas', () => {
    renderDesigner();
    expect(screen.getByRole('button', { name: 'Start' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'User Task' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Approval Task' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'End' })).toBeInTheDocument();
  });

  it('loads an existing definition with the correct number of nodes', async () => {
    // Edge rendering in React Flow depends on measured node dimensions (ResizeObserver), which
    // jsdom can't provide — .react-flow__edge count isn't reliably assertable here. graphModel's
    // own tests (deserializeWorkflowDefinition) already verify the transitions are read correctly
    // into the data model; this test only checks the node side of that same load.
    renderDesigner({ initialDefinition: SEQUENTIAL_DEFINITION });
    await waitFor(() => expect(document.querySelectorAll('.react-flow__node')).toHaveLength(3));
    expect(screen.getByText('Manager Approval')).toBeInTheDocument();
  });

  it('adds Start, UserTask, ApprovalTask and End nodes via palette clicks', async () => {
    const user = userEvent.setup();
    renderDesigner();

    await user.click(screen.getByRole('button', { name: 'Start' }));
    await user.click(screen.getByRole('button', { name: 'User Task' }));
    await user.click(screen.getByRole('button', { name: 'Approval Task' }));
    await user.click(screen.getByRole('button', { name: 'End' }));

    await waitFor(() => expect(document.querySelectorAll('.react-flow__node')).toHaveLength(4));
  });

  it('reports the initial definition as not dirty, then dirty after adding a node', async () => {
    const { onChange } = renderDesigner({ initialDefinition: SEQUENTIAL_DEFINITION });

    await waitFor(() => expect(onChange).toHaveBeenCalled());
    const [, initialDirty] = onChange.mock.calls[0];
    expect(initialDirty).toBe(false);

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'Start' }));

    await waitFor(() => {
      const lastCall = onChange.mock.calls.at(-1)!;
      expect(lastCall[1]).toBe(true);
    });
  });

  it('selects a node and shows/edits its properties', async () => {
    const user = userEvent.setup();
    renderDesigner({ initialDefinition: SEQUENTIAL_DEFINITION });

    await waitFor(() => expect(document.querySelectorAll('.react-flow__node')).toHaveLength(3));
    const approvalNode = screen.getByText('Manager Approval').closest('.react-flow__node')!;
    fireEvent.click(approvalNode);

    const nameInput = await screen.findByDisplayValue('Manager Approval');
    await user.clear(nameInput);
    await user.type(nameInput, 'Director Approval');

    await waitFor(() => expect(screen.getAllByText('Director Approval').length).toBeGreaterThan(0));
  });

  it('deletes a selected node and its connected edges', async () => {
    const user = userEvent.setup();
    renderDesigner({ initialDefinition: SEQUENTIAL_DEFINITION });

    await waitFor(() => expect(document.querySelectorAll('.react-flow__node')).toHaveLength(3));
    const approvalNode = screen.getByText('Manager Approval').closest('.react-flow__node')!;
    fireEvent.click(approvalNode);

    const deleteButton = (await screen.findByText('Delete')).closest('button')!;
    await user.click(deleteButton);

    await waitFor(() => expect(document.querySelectorAll('.react-flow__node')).toHaveLength(2));
  });

  it('supports undo and redo of a node addition', async () => {
    const user = userEvent.setup();
    renderDesigner({ initialDefinition: SEQUENTIAL_DEFINITION });

    await waitFor(() => expect(document.querySelectorAll('.react-flow__node')).toHaveLength(3));
    await user.click(screen.getByRole('button', { name: 'User Task' }));
    await waitFor(() => expect(document.querySelectorAll('.react-flow__node')).toHaveLength(4));

    await user.click(screen.getByRole('button', { name: /Undo/i }));
    await waitFor(() => expect(document.querySelectorAll('.react-flow__node')).toHaveLength(3));

    await user.click(screen.getByRole('button', { name: /Redo/i }));
    await waitFor(() => expect(document.querySelectorAll('.react-flow__node')).toHaveLength(4));
  });

  it('renders read-only with no palette, no undo/redo/save, and no delete action', async () => {
    renderDesigner({ initialDefinition: SEQUENTIAL_DEFINITION, readOnly: true });

    await waitFor(() => expect(document.querySelectorAll('.react-flow__node')).toHaveLength(3));
    expect(screen.queryByRole('button', { name: 'Start' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save Draft' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Undo/i })).not.toBeInTheDocument();

    const approvalNode = screen.getByText('Manager Approval').closest('.react-flow__node')!;
    fireEvent.click(approvalNode);
    expect(screen.queryByText('Delete')).not.toBeInTheDocument();
  });

  it('shows validation errors and highlights the mentioned node', async () => {
    renderDesigner({
      initialDefinition: SEQUENTIAL_DEFINITION,
      validationResult: {
        isValid: false,
        errors: [{ code: 'MISSING_APPROVAL_CONFIG', message: "ApprovalTask node 'approval' must specify an approval configuration." }],
      },
    });

    expect(await screen.findByText('Workflow Validation')).toBeInTheDocument();
    expect(screen.getByText('MISSING_APPROVAL_CONFIG')).toBeInTheDocument();
    expect(screen.getByText(/must specify an approval configuration/)).toBeInTheDocument();
    // Resolved to the affected node — rendered as `ApprovalTask "Manager Approval"`.
    expect(screen.getByText('ApprovalTask "Manager Approval"')).toBeInTheDocument();

    await waitFor(() => {
      const approvalNodeEl = screen.getByText('Manager Approval').closest('.react-flow__node')!;
      expect(approvalNodeEl.className).toContain('bpm-node-has-error');
    });
  });

  it('clicking a mapped validation error selects the affected node', async () => {
    renderDesigner({
      initialDefinition: SEQUENTIAL_DEFINITION,
      validationResult: {
        isValid: false,
        errors: [{ code: 'MISSING_APPROVAL_CONFIG', message: "ApprovalTask node 'approval' must specify an approval configuration." }],
      },
    });

    const user = userEvent.setup();
    const errorItem = await screen.findByText('ApprovalTask "Manager Approval"');
    await user.click(errorItem);

    // Selecting the node opens it in the Properties Panel, which shows its editable Name field.
    expect(await screen.findByDisplayValue('Manager Approval')).toBeInTheDocument();
  });

  it('shows a success message when validation passes', async () => {
    renderDesigner({
      initialDefinition: SEQUENTIAL_DEFINITION,
      validationResult: { isValid: true, errors: [] },
    });

    expect(await screen.findByText('Workflow definition is valid.')).toBeInTheDocument();
  });
});
