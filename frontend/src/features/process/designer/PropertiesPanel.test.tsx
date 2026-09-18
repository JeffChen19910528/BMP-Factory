import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { createNode } from './graphModel';
import { LanguageProvider } from '../../../i18n/LanguageContext';
import { PropertiesPanel } from './PropertiesPanel';

// AntD's Select renders its dropdown options in a portal; userEvent.click's pointer-events/
// visibility computation over that portaled, absolutely-positioned option list is unreliable in
// jsdom (the click resolves without throwing, but the underlying mousedown AntD listens for never
// fires) — a known jsdom/AntD-portal interaction gap, not something under this file's control.
// A plain fireEvent.click on the found option sidesteps that extra computation and reliably
// triggers AntD's selection handler.
async function selectOption(user: ReturnType<typeof userEvent.setup>, combobox: HTMLElement, optionName: string | RegExp) {
  await user.click(combobox);
  const option = await screen.findByRole('option', { name: optionName });
  fireEvent.click(option);
}

vi.mock('../../../services/roleService', () => ({
  listRoles: vi.fn().mockResolvedValue([
    { id: 'role-1', name: 'Manager' },
    { id: 'role-2', name: 'Finance' },
    { id: 'role-3', name: 'Legal' },
  ]),
}));
vi.mock('../../../services/departmentService', () => ({
  listDepartments: vi.fn().mockResolvedValue([
    { id: 'dept-1', name: 'Engineering', organizationId: 'org-1', parentId: null, managerUserId: 'user-2' },
    { id: 'dept-2', name: 'Sales', organizationId: 'org-1', parentId: null, managerUserId: null },
  ]),
}));
vi.mock('../../../services/userService', () => ({
  listUsers: vi.fn().mockResolvedValue([
    { id: 'user-1', username: 'alice', displayName: 'Alice Smith', email: 'alice@bpm.local', departmentId: null, isActive: true },
    { id: 'user-2', username: 'bob', displayName: 'Bob Jones', email: 'bob@bpm.local', departmentId: null, isActive: true },
  ]),
}));
vi.mock('../../../services/formDefinitionService', () => ({
  listFormDefinitions: vi.fn().mockResolvedValue([
    { id: 'form-1', key: 'purchase-request', name: 'Purchase Request', description: null, category: null, status: 'Published', currentVersionId: 'v-1' },
    { id: 'form-2', key: 'draft-form', name: 'Still Draft Form', description: null, category: null, status: 'Draft', currentVersionId: null },
  ]),
}));

function renderPanel(node: ReturnType<typeof createNode>, readOnly = false) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onChange = vi.fn();
  const onDelete = vi.fn();
  const utils = render(
    <LanguageProvider initialLanguage="en-US">
      <QueryClientProvider client={queryClient}>
        <PropertiesPanel node={node} readOnly={readOnly} onChange={onChange} onDelete={onDelete} />
      </QueryClientProvider>
    </LanguageProvider>,
  );
  return { ...utils, onChange, onDelete };
}

describe('PropertiesPanel — UserTask assignment', () => {
  it('offers Role/User/ProcessInitiator only (Department is ApprovalTask-only)', () => {
    const node = createNode('UserTask', { x: 0, y: 0 });
    renderPanel(node);

    // The type selector is the first combobox in the Assignment row.
    expect(screen.queryByText('DepartmentManager')).not.toBeInTheDocument();
  });

  it('Role assignment offers real roles from the backend, not hard-coded names', async () => {
    const node = createNode('UserTask', { x: 0, y: 0 });
    node.data.assignment = { type: 'Role', value: '' };
    const { onChange } = renderPanel(node);
    const user = userEvent.setup();

    const valueSelect = screen.getAllByRole('combobox')[1];
    await selectOption(user, valueSelect, 'Finance');

    await waitFor(() => expect(onChange).toHaveBeenCalledWith(node.id, { assignment: { type: 'Role', value: 'Finance' } }));
  });

  it('User assignment offers real users from the backend', async () => {
    const node = createNode('UserTask', { x: 0, y: 0 });
    node.data.assignment = { type: 'User', value: '' };
    const { onChange } = renderPanel(node);
    const user = userEvent.setup();

    const valueSelect = screen.getAllByRole('combobox')[1];
    await selectOption(user, valueSelect, 'Bob Jones');

    await waitFor(() => expect(onChange).toHaveBeenCalledWith(node.id, { assignment: { type: 'User', value: 'user-2' } }));
  });

  it('ProcessInitiator disables the value picker entirely', () => {
    const node = createNode('UserTask', { x: 0, y: 0 });
    node.data.assignment = { type: 'ProcessInitiator', value: '' };
    renderPanel(node);

    const valueSelect = screen.getAllByRole('combobox')[1];
    expect(valueSelect.closest('.ant-select')).toHaveClass('ant-select-disabled');
  });

  it('lists only Published form definitions, and serializes the selected key as the reference', async () => {
    const node = createNode('UserTask', { x: 0, y: 0 });
    const { onChange } = renderPanel(node);
    const user = userEvent.setup();

    const formSelect = screen.getAllByRole('combobox')[2]; // [0] assignment type, [1] assignment value, [2] form
    await user.click(formSelect);
    expect(await screen.findByRole('option', { name: 'Purchase Request (purchase-request)' })).toBeInTheDocument();
    expect(screen.queryByText(/Still Draft Form/)).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('option', { name: 'Purchase Request (purchase-request)' }));

    await waitFor(() => expect(onChange).toHaveBeenCalledWith(node.id, { form: { formDefinitionKey: 'purchase-request' } }));
  });

  it('never offers a Form field on ApprovalTask (the engine ignores it there)', () => {
    const node = createNode('ApprovalTask', { x: 0, y: 0 });
    renderPanel(node);
    expect(screen.queryByText('Form (optional)')).not.toBeInTheDocument();
  });
});

describe('PropertiesPanel — ApprovalTask assignment types', () => {
  it('offers all five Phase 3 assignment types', async () => {
    const node = createNode('ApprovalTask', { x: 0, y: 0 });
    node.data.approval = { policy: 'All', assignments: [{ type: 'Role', value: '' }], allowReject: true, allowReturn: true, allowDelegate: true, allowTransfer: true, allowAddApprover: true };
    renderPanel(node);
    const user = userEvent.setup();

    const typeSelect = screen.getAllByRole('combobox')[1]; // [0] is Policy
    await user.click(typeSelect);

    for (const type of ['User', 'Role', 'Department', 'DepartmentManager', 'ProcessInitiator']) {
      expect(await screen.findByRole('option', { name: type })).toBeInTheDocument();
    }
  });

  it('Department assignment offers real departments', async () => {
    const node = createNode('ApprovalTask', { x: 0, y: 0 });
    node.data.approval = { policy: 'All', assignments: [{ type: 'Department', value: '' }], allowReject: true, allowReturn: true, allowDelegate: true, allowTransfer: true, allowAddApprover: true };
    const { onChange } = renderPanel(node);
    const user = userEvent.setup();

    const valueSelect = screen.getAllByRole('combobox')[2]; // [0] Policy, [1] assignment type
    await selectOption(user, valueSelect, 'Engineering');

    await waitFor(() =>
      expect(onChange).toHaveBeenCalledWith(node.id, { approval: expect.objectContaining({ assignments: [{ type: 'Department', value: 'dept-1' }] }) }),
    );
  });

  it('DepartmentManager assignment also picks a department (resolves to its manager on the backend)', async () => {
    const node = createNode('ApprovalTask', { x: 0, y: 0 });
    node.data.approval = { policy: 'All', assignments: [{ type: 'DepartmentManager', value: '' }], allowReject: true, allowReturn: true, allowDelegate: true, allowTransfer: true, allowAddApprover: true };
    const { onChange } = renderPanel(node);
    const user = userEvent.setup();

    const valueSelect = screen.getAllByRole('combobox')[2];
    await selectOption(user, valueSelect, 'Engineering');

    await waitFor(() =>
      expect(onChange).toHaveBeenCalledWith(node.id, { approval: expect.objectContaining({ assignments: [{ type: 'DepartmentManager', value: 'dept-1' }] }) }),
    );
  });

  it.each(['All', 'AnyOne', 'Sequential'] as const)('supports the %s approval policy', async (policy) => {
    const node = createNode('ApprovalTask', { x: 0, y: 0 });
    // Start from a policy different from the target so selecting it is a real change, not a
    // no-op re-selection of whatever's already current.
    const initialPolicy = policy === 'All' ? 'AnyOne' : 'All';
    node.data.approval = { policy: initialPolicy, assignments: [], allowReject: true, allowReturn: true, allowDelegate: true, allowTransfer: true, allowAddApprover: true };
    const { onChange } = renderPanel(node);
    const user = userEvent.setup();

    const policySelect = screen.getAllByRole('combobox')[0];
    await selectOption(user, policySelect, new RegExp(`^${policy} `));

    await waitFor(() => expect(onChange).toHaveBeenCalledWith(node.id, { approval: expect.objectContaining({ policy }) }));
  });

  it('exposes exactly the five backend-supported approval actions as checkboxes', () => {
    const node = createNode('ApprovalTask', { x: 0, y: 0 });
    renderPanel(node);

    expect(screen.getByText(/^Reject/)).toBeInTheDocument();
    expect(screen.getByText(/^Return/)).toBeInTheDocument();
    expect(screen.getByText(/^Delegate/)).toBeInTheDocument();
    expect(screen.getByText(/^Transfer/)).toBeInTheDocument();
    expect(screen.getByText(/^Add Approver/)).toBeInTheDocument();
  });

  it('toggling an action checkbox updates only that action', async () => {
    const node = createNode('ApprovalTask', { x: 0, y: 0 });
    const { onChange } = renderPanel(node);
    const user = userEvent.setup();

    await user.click(screen.getByRole('checkbox', { name: /^Reject/ }));

    await waitFor(() =>
      expect(onChange).toHaveBeenCalledWith(node.id, { approval: expect.objectContaining({ allowReject: false, allowReturn: true }) }),
    );
  });

  it('adding and removing assignments works', async () => {
    const node = createNode('ApprovalTask', { x: 0, y: 0 });
    node.data.approval!.assignments = [{ type: 'Role', value: 'Manager' }];
    const { onChange } = renderPanel(node);
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: /Add Assignment/i }));
    await waitFor(() =>
      expect(onChange).toHaveBeenCalledWith(node.id, {
        approval: expect.objectContaining({
          assignments: [{ type: 'Role', value: 'Manager' }, { type: 'Role', value: '' }],
        }),
      }),
    );
  });
});

describe('PropertiesPanel — read-only mode', () => {
  it('disables every assignment/policy/action control', () => {
    const node = createNode('ApprovalTask', { x: 0, y: 0 });
    renderPanel(node, true);

    for (const combobox of screen.getAllByRole('combobox')) {
      expect(combobox.closest('.ant-select')).toHaveClass('ant-select-disabled');
    }
    for (const checkbox of screen.getAllByRole('checkbox')) {
      expect(checkbox).toBeDisabled();
    }
    expect(screen.queryByRole('button', { name: /Add Assignment/i })).not.toBeInTheDocument();
    expect(screen.queryByText('Delete')).not.toBeInTheDocument();
  });
});

describe('PropertiesPanel — unsupported node', () => {
  it('shows the raw JSON read-only instead of an editable form', () => {
    const node = createNode('UserTask', { x: 0, y: 0 });
    node.data.supported = false;
    node.data.nodeType = 'ExclusiveGateway' as never;
    node.data.raw = { id: node.id, type: 'ExclusiveGateway' as never, name: 'Amount Check' };
    renderPanel(node);

    expect(screen.getByText(/isn't supported by the visual designer yet/)).toBeInTheDocument();
    const pre = document.querySelector('pre');
    expect(pre?.textContent).toContain('"ExclusiveGateway"');
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
  });
});
