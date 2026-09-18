import axios from 'axios';
import { describe, expect, it } from 'vitest';
import { createField, createRule, deserializeFormSchema, serializeFormSchema } from '../form/designer/formSchemaModel';

// Phase 5.4.4 §32's headline live scenario, run against the real rebuilt Docker stack: the full
// Process + Form execution path this phase exists to prove and harden — built with the actual
// Designer serialization functions (not hand-written JSON), through a real multi-step approval
// chain (Applicant -> Manager -> Finance), exercising conditional-required and calculated-field
// rules, Save Draft/Resume, duplicate-submit rejection, and IDOR protection, all against the real
// engine and a real login for every actor involved (not the same admin account playing every
// role, unlike earlier phases' live tests — this phase specifically wants to prove cross-user
// authorization).
const baseURL = import.meta.env.VITE_API_BASE_URL || 'http://localhost:5080';

function client() {
  return axios.create({ baseURL, validateStatus: () => true });
}

async function loginAsAdmin() {
  const api = client();
  const login = await api.post('/api/auth/login', { username: 'admin', password: 'ChangeMe123!' });
  expect(login.status).toBe(200);
  return { api, auth: { Authorization: `Bearer ${login.data.accessToken}` } };
}

async function createAndLogin(admin: { api: ReturnType<typeof client>; auth: Record<string, string> }, username: string, displayName: string) {
  const password = 'Passw0rd!123';
  const created = await admin.api.post(
    '/api/users',
    { username, displayName, email: `${username}@bpm-tests.local`, password, departmentId: null },
    { headers: admin.auth },
  );
  expect(created.status).toBe(201);

  const api = client();
  const login = await api.post('/api/auth/login', { username, password });
  expect(login.status).toBe(200);
  return { api, auth: { Authorization: `Bearer ${login.data.accessToken}` }, userId: created.data.id as string };
}

describe('Expense Request E2E — Process + Form Integration & Hardening (live)', () => {
  it('Applicant submits (conditional rule + calculated field) -> Manager approves -> Finance approves -> Process Completed; duplicate submit and IDOR are both rejected', async () => {
    const admin = await loginAsAdmin();
    const suffix = Date.now();

    const applicant = await createAndLogin(admin, `applicant-${suffix}`, 'Applicant');
    const manager = await createAndLogin(admin, `manager-${suffix}`, 'Manager');
    const finance = await createAndLogin(admin, `finance-${suffix}`, 'Finance');
    const stranger = await createAndLogin(admin, `stranger-${suffix}`, 'Stranger');

    const managerRole = await admin.api.post('/api/roles', { name: `Manager-${suffix}` }, { headers: admin.auth });
    const financeRole = await admin.api.post('/api/roles', { name: `Finance-${suffix}` }, { headers: admin.auth });
    expect(managerRole.status).toBe(200);
    expect(financeRole.status).toBe(200);
    const managerRoleAssign = await admin.api.post('/api/roles/assign', { userId: manager.userId, roleId: managerRole.data.id }, { headers: admin.auth });
    expect(managerRoleAssign.status).toBe(204);
    const financeRoleAssign = await admin.api.post('/api/roles/assign', { userId: finance.userId, roleId: financeRole.data.id }, { headers: admin.auth });
    expect(financeRoleAssign.status).toBe(204);

    // Build the Expense form with the Designer's own functions: Expense Type (conditional
    // trigger), Description (conditionally required), Quantity/UnitPrice/Total (Calculated).
    const formCreated = await admin.api.post(
      '/api/form-definitions',
      { key: `expense-e2e-${suffix}`, name: 'Expense Request' },
      { headers: admin.auth },
    );
    expect(formCreated.status).toBe(201);
    const starterVersion = await admin.api.post(
      `/api/form-definitions/${formCreated.data.id}/versions`,
      { schema: { fields: [{ key: 'field1', type: 'Text', label: 'x' }] } },
      { headers: admin.auth },
    );
    let fields = deserializeFormSchema(starterVersion.data.schema).filter((f) => f.key !== 'field1');

    const expenseType = createField('Select', new Set(fields.map((f) => f.key)));
    expenseType.key = 'expenseType';
    expenseType.label = 'Expense Type';
    expenseType.required = true;
    expenseType.options = [{ value: 'TRAVEL', label: 'Travel' }, { value: 'OTHER', label: 'Other' }];
    fields = [...fields, expenseType];

    const description = createField('Text', new Set(fields.map((f) => f.key)));
    description.key = 'description';
    description.label = 'Description';
    fields = [...fields, description];

    const quantity = createField('Number', new Set(fields.map((f) => f.key)));
    quantity.key = 'quantity';
    quantity.label = 'Quantity';
    quantity.required = true;
    fields = [...fields, quantity];

    const unitPrice = createField('Currency', new Set(fields.map((f) => f.key)));
    unitPrice.key = 'unitPrice';
    unitPrice.label = 'Unit Price';
    unitPrice.required = true;
    fields = [...fields, unitPrice];

    const total = createField('Currency', new Set(fields.map((f) => f.key)));
    total.key = 'total';
    total.label = 'Total';
    fields = [...fields, total];

    const requiredRule = createRule('Required', 'description', new Set());
    requiredRule.condition = { kind: 'field', field: 'expenseType', operator: 'Equals', value: 'OTHER' };
    const calcRule = createRule('Calculated', 'total', new Set([requiredRule.id]));
    calcRule.formula = 'quantity * unitPrice';

    const schema = serializeFormSchema(fields, [requiredRule, calcRule]);
    await admin.api.put(
      `/api/form-definitions/${formCreated.data.id}/versions/${starterVersion.data.id}`,
      { schema, expectedVersion: starterVersion.data.rowVersion },
      { headers: admin.auth },
    );
    const formPublish = await admin.api.post(`/api/form-definitions/${formCreated.data.id}/publish`, null, { headers: admin.auth });
    expect(formPublish.status).toBe(200);

    // Applicant -> Manager (AnyOne, Role) -> Finance (AnyOne, Role) -> End.
    const processCreated = await admin.api.post(
      '/api/process-definitions',
      { key: `expense-e2e-proc-${suffix}`, name: 'Expense Process' },
      { headers: admin.auth },
    );
    const processId = processCreated.data.id as string;
    const definition = {
      nodes: [
        { id: 'start', type: 'Start', name: 'Start' },
        { id: 'applicant', type: 'UserTask', name: 'Applicant', assignment: { type: 'ProcessInitiator', value: '' }, form: { formDefinitionKey: formCreated.data.key } },
        { id: 'manager', type: 'ApprovalTask', name: 'Manager Approval', approval: { policy: 'AnyOne', assignments: [{ type: 'Role', value: managerRole.data.name }] } },
        { id: 'finance', type: 'ApprovalTask', name: 'Finance Approval', approval: { policy: 'AnyOne', assignments: [{ type: 'Role', value: financeRole.data.name }] } },
        { id: 'end', type: 'End', name: 'End' },
      ],
      transitions: [
        { id: 't1', source: 'start', target: 'applicant' },
        { id: 't2', source: 'applicant', target: 'manager' },
        { id: 't3', source: 'manager', target: 'finance' },
        { id: 't4', source: 'finance', target: 'end' },
      ],
    };
    await admin.api.post(`/api/process-definitions/${processId}/versions`, { definition }, { headers: admin.auth });
    const processPublish = await admin.api.post(`/api/process-definitions/${processId}/publish`, null, { headers: admin.auth });
    expect(processPublish.status).toBe(200);

    // Applicant starts the process.
    const started = await applicant.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: applicant.auth });
    expect(started.status).toBe(201);
    const processInstanceId = started.data.id as string;

    const applicantTasks = await applicant.api.get('/api/tasks', { headers: applicant.auth });
    const applicantTask = applicantTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceId);
    expect(applicantTask).toBeTruthy();

    const formInstances = await applicant.api.get('/api/form-instances', { params: { processInstanceId }, headers: applicant.auth });
    const boundInstance = formInstances.data.find((f: { taskInstanceId: string | null }) => f.taskInstanceId === applicantTask.id);
    expect(boundInstance).toBeTruthy();

    // ---- IDOR: a stranger cannot see or touch the applicant's task or form ----
    const strangerTaskGet = await stranger.api.get(`/api/tasks/${applicantTask.id}`, { headers: stranger.auth });
    expect(strangerTaskGet.status).toBe(403);
    const strangerFormGet = await stranger.api.get(`/api/form-instances/${boundInstance.id}`, { headers: stranger.auth });
    expect(strangerFormGet.status).toBe(403);
    const strangerFormData = await stranger.api.get(`/api/form-instances/${boundInstance.id}/data`, { headers: stranger.auth });
    expect(strangerFormData.status).toBe(403);
    const strangerSubmit = await stranger.api.post(`/api/form-instances/${boundInstance.id}/submit`, null, { headers: stranger.auth });
    expect(strangerSubmit.status).toBe(403);

    // ---- Save Draft / Resume ----
    const dataQuery = await applicant.api.get(`/api/form-instances/${boundInstance.id}/data`, { headers: applicant.auth });
    const draftSave = await applicant.api.put(
      `/api/form-instances/${boundInstance.id}/data`,
      { data: { expenseType: 'OTHER', quantity: 3, unitPrice: 100 }, expectedVersion: dataQuery.data.version },
      { headers: applicant.auth },
    );
    expect(draftSave.status).toBe(200);
    expect(draftSave.data.data.total).toBe(300); // Calculated field, recomputed server-side.

    const resumed = await applicant.api.get(`/api/form-instances/${boundInstance.id}/data`, { headers: applicant.auth });
    expect(resumed.data.data.quantity).toBe(3);

    // Submit without Description (required because expenseType=OTHER) -> rejected.
    const rejectedSubmit = await applicant.api.post(`/api/form-instances/${boundInstance.id}/submit`, null, { headers: applicant.auth });
    expect(rejectedSubmit.status).toBe(400);
    expect(rejectedSubmit.data.errors.some((e: { code: string }) => e.code === 'FIELD_REQUIRED')).toBe(true);

    // Spoof total, fill in Description, submit for real.
    const finalSave = await applicant.api.put(
      `/api/form-instances/${boundInstance.id}/data`,
      { data: { description: 'Client dinner', total: 999999 }, expectedVersion: resumed.data.version },
      { headers: applicant.auth },
    );
    expect(finalSave.status).toBe(200);
    expect(finalSave.data.data.total).toBe(300); // Spoofed value never trusted.

    const submitted = await applicant.api.post(`/api/form-instances/${boundInstance.id}/submit`, null, { headers: applicant.auth });
    expect(submitted.status).toBe(200);

    // Duplicate submit -> rejected, not a silent no-op success and not a second transition.
    const duplicateSubmit = await applicant.api.post(`/api/form-instances/${boundInstance.id}/submit`, null, { headers: applicant.auth });
    expect(duplicateSubmit.status).toBe(409);

    // ---- Manager approves ----
    const managerTasks = await manager.api.get('/api/tasks', { headers: manager.auth });
    const managerTask = managerTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceId);
    expect(managerTask).toBeTruthy();
    expect(managerTask.approval).toBeTruthy();

    // A stranger cannot approve the manager's task.
    const strangerApprove = await stranger.api.post(`/api/tasks/${managerTask.id}/approve`, null, { headers: stranger.auth });
    expect(strangerApprove.status).toBe(403);

    const managerApprove = await manager.api.post(`/api/tasks/${managerTask.id}/approve`, null, { headers: manager.auth });
    expect(managerApprove.status).toBe(200);

    // ---- Finance approves -> process completes ----
    const financeTasks = await finance.api.get('/api/tasks', { headers: finance.auth });
    const financeTask = financeTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceId);
    expect(financeTask).toBeTruthy();

    const financeApprove = await finance.api.post(`/api/tasks/${financeTask.id}/approve`, null, { headers: finance.auth });
    expect(financeApprove.status).toBe(200);

    const finalProcess = await admin.api.get(`/api/process-instances/${processInstanceId}`, { headers: admin.auth });
    expect(finalProcess.data.status).toBe('Completed');

    // Completed state is read-only: the applicant's now-locked form rejects further edits.
    const editAfterCompletion = await applicant.api.put(
      `/api/form-instances/${boundInstance.id}/data`,
      { data: { description: 'changed' }, expectedVersion: finalSave.data.version },
      { headers: applicant.auth },
    );
    expect(editAfterCompletion.status).toBe(409);
  });
});
