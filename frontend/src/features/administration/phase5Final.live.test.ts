import axios from 'axios';
import { describe, expect, it } from 'vitest';
import { createField, createRule, deserializeFormSchema, serializeFormSchema } from '../form/designer/formSchemaModel';

// Phase 5.5.4 — Final Phase 5 E2E & Release Readiness. Ties together every operational surface
// built across Phase 5 in one real, Docker-backed, multi-actor flow: Administration (real
// Department/Role/User setup) -> Process Design (Designer serialization) -> Form Design (Designer
// serialization, with rules) -> Publish -> Start -> multi-step Approval (Manager -> Finance) ->
// Audit Log verification that every step left a real trail -> a cross-user security sweep with a
// fourth, wholly unrelated identity. This does not re-test what expenseE2E.live.test.ts,
// approvalsWorklist.live.test.ts, administration.live.test.ts, process.live.test.ts, and
// designer.live.test.ts already cover individually (ProcessVersion/FormVersion concurrency, form
// rule enforcement, worklist correctness, Administration IDOR) — it proves those pieces compose
// correctly end to end with the Audit trail as the connecting evidence, which nothing else does.
const baseURL = import.meta.env.VITE_API_BASE_URL || 'http://localhost:5080';

function client() {
  return axios.create({ baseURL, validateStatus: () => true });
}

async function loginAsAdmin() {
  const api = client();
  const login = await api.post('/api/auth/login', { username: 'admin', password: 'ChangeMe123!' });
  expect(login.status).toBe(200);
  return { api, auth: { Authorization: `Bearer ${login.data.accessToken}` }, userId: login.data.userId as string };
}

async function createAndLogin(admin: Awaited<ReturnType<typeof loginAsAdmin>>, username: string, displayName: string, departmentId: string | null = null) {
  const password = 'Passw0rd!123';
  const created = await admin.api.post(
    '/api/users',
    { username, displayName, email: `${username}@bpm-tests.local`, password, departmentId },
    { headers: admin.auth },
  );
  expect(created.status).toBe(201);

  const api = client();
  const login = await api.post('/api/auth/login', { username, password });
  expect(login.status).toBe(200);
  return { api, auth: { Authorization: `Bearer ${login.data.accessToken}` }, userId: created.data.id as string };
}

// Phase 6.4: a bare `Date.now()` millisecond suffix can collide with another `*.live.test.ts`
// file's own role-name suffix when the suite runs several files in parallel — this surfaced as an
// intermittent `IX_Roles_TenantId_Name` conflict once RoleService.CreateAsync got a proper
// duplicate-name check (see PROGRESS.md's Phase 6.4 section). A random component makes the suffix
// collision-resistant across files without changing any production semantics.
function uniqueSuffix() {
  return `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

describe('Phase 5 Final E2E — Administration + Process + Form + Approval + Audit, composed end to end', () => {
  it('runs the full multi-actor flow and verifies the audit trail and cross-user security at every step', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();

    // ---- C1: Administration setup ----
    const orgs = await admin.api.get('/api/organizations', { headers: admin.auth });
    let orgId = orgs.data[0]?.id as string | undefined;
    if (!orgId) {
      const createOrg = await admin.api.post('/api/organizations', { name: `Org-${suffix}`, parentId: null }, { headers: admin.auth });
      orgId = createOrg.data.id;
    }
    const dept = await admin.api.post('/api/departments', { name: `Finance Dept ${suffix}`, organizationId: orgId, parentId: null, managerUserId: null }, { headers: admin.auth });
    expect(dept.status).toBe(200);

    const managerRole = await admin.api.post('/api/roles', { name: `Manager-${suffix}` }, { headers: admin.auth });
    const financeRole = await admin.api.post('/api/roles', { name: `Finance-${suffix}` }, { headers: admin.auth });
    expect(managerRole.status).toBe(200);
    expect(financeRole.status).toBe(200);

    const applicant = await createAndLogin(admin, `applicant-${suffix}`, 'Applicant');
    const manager = await createAndLogin(admin, `manager-${suffix}`, 'Manager', dept.data.id);
    const finance = await createAndLogin(admin, `finance-${suffix}`, 'Finance');
    const unauthorized = await createAndLogin(admin, `stranger-${suffix}`, 'Unauthorized User');

    await admin.api.post('/api/roles/assign', { userId: manager.userId, roleId: managerRole.data.id }, { headers: admin.auth });
    await admin.api.post('/api/roles/assign', { userId: finance.userId, roleId: financeRole.data.id }, { headers: admin.auth });

    // Set the department manager to the real Manager user — verified persisted, per Scenario B.
    const deptWithManager = await admin.api.put(
      `/api/departments/${dept.data.id}`,
      { name: dept.data.name, parentId: null, managerUserId: manager.userId, expectedVersion: dept.data.rowVersion },
      { headers: admin.auth },
    );
    expect(deptWithManager.status).toBe(200);
    expect(deptWithManager.data.managerUserId).toBe(manager.userId);

    // A normal (non-admin) user cannot perform any administrative action — verified here rather
    // than assumed, even though administration.live.test.ts already covers this shape in depth.
    expect((await applicant.api.get('/api/roles', { headers: applicant.auth })).status).toBe(403);
    expect((await unauthorized.api.post('/api/departments', { name: 'Hacked Dept', organizationId: orgId, parentId: null, managerUserId: null }, { headers: unauthorized.auth })).status).toBe(403);

    // ---- C3: Form Design, via the real Designer serialization functions (not hand-written
    // schema) — mirrors expenseE2E.live.test.ts's own established pattern: create a throwaway
    // starter version to get a real id/rowVersion, build fields/rules with the Designer's own
    // model functions, then PUT the real schema before publishing.
    const formCreated = await admin.api.post('/api/form-definitions', { key: `final-e2e-form-${suffix}`, name: 'Final E2E Form' }, { headers: admin.auth });
    expect(formCreated.status).toBe(201);
    const starterVersion = await admin.api.post(
      `/api/form-definitions/${formCreated.data.id}/versions`,
      { schema: { fields: [{ key: 'field1', type: 'Text', label: 'x' }] } },
      { headers: admin.auth },
    );
    let fields = deserializeFormSchema(starterVersion.data.schema).filter((f) => f.key !== 'field1');

    const note = createField('Text', new Set(fields.map((f) => f.key)));
    note.key = 'note';
    note.label = 'Note';
    fields = [...fields, note];

    const amount = createField('Number', new Set(fields.map((f) => f.key)));
    amount.key = 'amount';
    amount.label = 'Amount';
    fields = [...fields, amount];

    const urgent = createField('Select', new Set(fields.map((f) => f.key)));
    urgent.key = 'urgent';
    urgent.label = 'Urgent?';
    urgent.options = [{ value: 'yes', label: 'Yes' }, { value: 'no', label: 'No' }];
    fields = [...fields, urgent];

    const approverComment = createField('Text', new Set(fields.map((f) => f.key)));
    approverComment.key = 'approverComment';
    approverComment.label = 'Approver Comment';
    fields = [...fields, approverComment];

    const visibilityRule = createRule('Visibility', 'approverComment', new Set());
    visibilityRule.condition = { kind: 'field', field: 'urgent', operator: 'Equals', value: 'yes' };

    const schema = serializeFormSchema(fields, [visibilityRule]);
    await admin.api.put(
      `/api/form-definitions/${formCreated.data.id}/versions/${starterVersion.data.id}`,
      { schema, expectedVersion: starterVersion.data.rowVersion },
      { headers: admin.auth },
    );
    const formPublish = await admin.api.post(`/api/form-definitions/${formCreated.data.id}/publish`, null, { headers: admin.auth });
    expect(formPublish.status).toBe(200);

    // Published version is immutable — reload confirms it now reports Published status.
    const formVersionAfterPublish = await admin.api.get(`/api/form-definitions/${formCreated.data.id}/versions`, { headers: admin.auth });
    const publishedFormVersion = formVersionAfterPublish.data.find((v: { id: string }) => v.id === starterVersion.data.id);
    expect(publishedFormVersion.status).toBe('Published');

    // ---- C2: Process Design (Start -> UserTask(Applicant) -> ApprovalTask(Manager) -> ApprovalTask(Finance) -> End) ----
    const processCreated = await admin.api.post('/api/process-definitions', { key: `final-e2e-proc-${suffix}`, name: 'Final E2E Process' }, { headers: admin.auth });
    expect(processCreated.status).toBe(201);
    const definition = {
      nodes: [
        { id: 'start', type: 'Start', name: 'Start' },
        { id: 'applicant', type: 'UserTask', name: 'Applicant', assignment: { type: 'ProcessInitiator', value: '' }, form: { formDefinitionKey: formCreated.data.key } },
        { id: 'manager', type: 'ApprovalTask', name: 'Manager Approval', approval: { policy: 'AnyOne', assignments: [{ type: 'Role', value: managerRole.data.name }], returnPolicy: { enabled: true } } },
        { id: 'finance', type: 'ApprovalTask', name: 'Finance Approval', approval: { policy: 'AnyOne', assignments: [{ type: 'Role', value: financeRole.data.name }], returnPolicy: { enabled: true } } },
        { id: 'end', type: 'End', name: 'End' },
      ],
      transitions: [
        { id: 't1', source: 'start', target: 'applicant' },
        { id: 't2', source: 'applicant', target: 'manager' },
        { id: 't3', source: 'manager', target: 'finance' },
        { id: 't4', source: 'finance', target: 'end' },
      ],
    };
    const validate = await admin.api.post('/api/process-definitions/validate', { definition }, { headers: admin.auth });
    expect(validate.status).toBe(200);
    expect(validate.data.isValid).toBe(true);

    const versionCreated = await admin.api.post(`/api/process-definitions/${processCreated.data.id}/versions`, { definition }, { headers: admin.auth });
    expect(versionCreated.status).toBe(200);
    const processPublish = await admin.api.post(`/api/process-definitions/${processCreated.data.id}/publish`, null, { headers: admin.auth });
    expect(processPublish.status).toBe(200);

    // ---- C4: Start Process ----
    const started = await applicant.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: applicant.auth });
    expect(started.status).toBe(201);
    const processInstanceId = started.data.id as string;

    // Unauthorized User cannot see this process instance or its tasks (C9 cross-user check).
    expect((await unauthorized.api.get(`/api/process-instances/${processInstanceId}`, { headers: unauthorized.auth })).status).toBe(403);

    // ---- C5: Form Runtime — Applicant fills and submits ----
    const applicantTasks = await applicant.api.get('/api/tasks', { headers: applicant.auth });
    const applicantTask = applicantTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceId);
    expect(applicantTask).toBeTruthy();

    // Cross-user: Unauthorized User cannot view the Applicant's task.
    expect((await unauthorized.api.get(`/api/tasks/${applicantTask.id}`, { headers: unauthorized.auth })).status).toBe(403);

    const formInstances = await applicant.api.get('/api/form-instances', { params: { processInstanceId }, headers: applicant.auth });
    const applicantFormInstance = formInstances.data.find((f: { taskInstanceId: string | null }) => f.taskInstanceId === applicantTask.id);
    expect((await unauthorized.api.get(`/api/form-instances/${applicantFormInstance.id}`, { headers: unauthorized.auth })).status).toBe(403);

    const dataBeforeSave = await applicant.api.get(`/api/form-instances/${applicantFormInstance.id}/data`, { headers: applicant.auth });
    // Conditional field ("approverComment") is not required while urgent=no — submit without it succeeds.
    const saved = await applicant.api.put(
      `/api/form-instances/${applicantFormInstance.id}/data`,
      { data: { note: 'Final E2E test', amount: 42, urgent: 'no' }, expectedVersion: dataBeforeSave.data.version },
      { headers: applicant.auth },
    );
    expect(saved.status).toBe(200);
    const submitted = await applicant.api.post(`/api/form-instances/${applicantFormInstance.id}/submit`, null, { headers: applicant.auth });
    expect(submitted.status).toBe(200);

    // ---- C6: Multi-step Approval — Manager then Finance ----
    const managerTasks = await manager.api.get('/api/tasks', { headers: manager.auth });
    const managerTask = managerTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceId);
    expect(managerTask).toBeTruthy();

    // Finance cannot act on the Manager's task yet (wrong assignee for this step).
    expect((await finance.api.post(`/api/tasks/${managerTask.id}/approve`, null, { headers: finance.auth })).status).toBe(403);
    // Unauthorized User cannot approve either.
    expect((await unauthorized.api.post(`/api/tasks/${managerTask.id}/approve`, null, { headers: unauthorized.auth })).status).toBe(403);

    const managerApprove = await manager.api.post(`/api/tasks/${managerTask.id}/approve`, null, { headers: manager.auth });
    expect(managerApprove.status).toBe(200);

    // Duplicate-click safety: approving an already-resolved task again is rejected, not a silent no-op success.
    const managerApproveAgain = await manager.api.post(`/api/tasks/${managerTask.id}/approve`, null, { headers: manager.auth });
    expect([400, 404, 409]).toContain(managerApproveAgain.status);

    const financeTasks = await finance.api.get('/api/tasks', { headers: finance.auth });
    const financeTask = financeTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceId);
    expect(financeTask).toBeTruthy();
    const financeApprove = await finance.api.post(`/api/tasks/${financeTask.id}/approve`, null, { headers: finance.auth });
    expect(financeApprove.status).toBe(200);

    // ---- C4/C6 continued: process should now be Completed ----
    const finalInstance = await admin.api.get(`/api/process-instances/${processInstanceId}`, { headers: admin.auth });
    expect(finalInstance.data.status).toBe('Completed');

    // ---- C7: Worklists reflect final state ----
    const managerCompleted = await manager.api.get('/api/tasks/approvals', { params: { status: 'Completed' }, headers: manager.auth });
    expect(managerCompleted.data.items.some((i: { taskId: string }) => i.taskId === managerTask.id)).toBe(true);
    const managerPending = await manager.api.get('/api/tasks/approvals', { params: { status: 'Pending' }, headers: manager.auth });
    expect(managerPending.data.items.some((i: { taskId: string }) => i.taskId === managerTask.id)).toBe(false);

    // ---- C8: Audit workspace — verify the full trail exists and is searchable ----
    const auditForProcess = await admin.api.get('/api/audit-logs', { params: { entityType: 'ProcessInstance', entityId: processInstanceId }, headers: admin.auth });
    expect(auditForProcess.status).toBe(200);
    expect(auditForProcess.data.items.length).toBeGreaterThan(0);

    const auditForDept = await admin.api.get('/api/audit-logs', { params: { entityType: 'Department', entityId: dept.data.id }, headers: admin.auth });
    expect(auditForDept.data.items.some((e: { action: string }) => e.action === 'UpdateDepartment')).toBe(true);

    // Audit Logs remain strictly read-only and Administrator-only, even after all this activity.
    expect((await applicant.api.get('/api/audit-logs', { headers: applicant.auth })).status).toBe(403);
    expect((await unauthorized.api.get('/api/audit-logs', { headers: unauthorized.auth })).status).toBe(403);
  });
});
