import axios from 'axios';
import { describe, expect, it } from 'vitest';

// Phase 5.5.1 — Approvals Worklist / Operational Approval Center, run against the real rebuilt
// Docker stack with four genuinely distinct logged-in users (Applicant/Manager/Finance/
// Unauthorized), covering this phase's own Scenarios A-H over the actual GET /api/tasks/approvals
// endpoint and the existing approve/reject task-action endpoints — never a mock, never a single
// admin token replaying every role.
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

// Phase 6.4: a bare `Date.now()` millisecond suffix can collide with another `*.live.test.ts`
// file's own role-name suffix when the suite runs several files in parallel — this surfaced as an
// intermittent `IX_Roles_TenantId_Name` conflict once RoleService.CreateAsync got a proper
// duplicate-name check (see PROGRESS.md's Phase 6.4 section). A random component makes the suffix
// collision-resistant across files without changing any production semantics.
function uniqueSuffix() {
  return `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

describe('Approvals Worklist against the live backend', () => {
  it('Manager and Finance each see only their own pending approval; approving moves it forward; unauthorized access and concurrent/duplicate actions are rejected', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();

    const applicant = await createAndLogin(admin, `applicant-${suffix}`, 'Applicant');
    const manager = await createAndLogin(admin, `manager-${suffix}`, 'Manager');
    const finance = await createAndLogin(admin, `finance-${suffix}`, 'Finance');
    const unauthorized = await createAndLogin(admin, `stranger-${suffix}`, 'Unauthorized User');

    const managerRole = await admin.api.post('/api/roles', { name: `Manager-${suffix}` }, { headers: admin.auth });
    const financeRole = await admin.api.post('/api/roles', { name: `Finance-${suffix}` }, { headers: admin.auth });
    expect(managerRole.status).toBe(200);
    expect(financeRole.status).toBe(200);
    await admin.api.post('/api/roles/assign', { userId: manager.userId, roleId: managerRole.data.id }, { headers: admin.auth });
    await admin.api.post('/api/roles/assign', { userId: finance.userId, roleId: financeRole.data.id }, { headers: admin.auth });

    // A minimal form so the Applicant step is real end-user work, not just a bare process.
    const formCreated = await admin.api.post('/api/form-definitions', { key: `worklist-form-${suffix}`, name: 'Worklist Form' }, { headers: admin.auth });
    await admin.api.post(
      `/api/form-definitions/${formCreated.data.id}/versions`,
      { schema: { fields: [{ key: 'note', type: 'Text', label: 'Note' }] } },
      { headers: admin.auth },
    );
    await admin.api.post(`/api/form-definitions/${formCreated.data.id}/publish`, null, { headers: admin.auth });

    // Applicant -> Manager (AnyOne) -> Finance (AnyOne) -> End.
    const processCreated = await admin.api.post('/api/process-definitions', { key: `worklist-proc-${suffix}`, name: 'Worklist Process' }, { headers: admin.auth });
    const processId = processCreated.data.id as string;
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
    await admin.api.post(`/api/process-definitions/${processId}/versions`, { definition }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processId}/publish`, null, { headers: admin.auth });

    const started = await applicant.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: applicant.auth });
    expect(started.status).toBe(201);
    const processInstanceId = started.data.id as string;

    // Fast-forward the Applicant step so a Manager approval genuinely exists.
    const applicantTasks = await applicant.api.get('/api/tasks', { headers: applicant.auth });
    const applicantTask = applicantTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceId);
    const applicantFormInstances = await applicant.api.get('/api/form-instances', { params: { processInstanceId }, headers: applicant.auth });
    const applicantFormInstance = applicantFormInstances.data.find((f: { taskInstanceId: string | null }) => f.taskInstanceId === applicantTask.id);
    const applicantData = await applicant.api.get(`/api/form-instances/${applicantFormInstance.id}/data`, { headers: applicant.auth });
    await applicant.api.put(`/api/form-instances/${applicantFormInstance.id}/data`, { data: { note: 'hello' }, expectedVersion: applicantData.data.version }, { headers: applicant.auth });
    const submitted = await applicant.api.post(`/api/form-instances/${applicantFormInstance.id}/submit`, null, { headers: applicant.auth });
    expect(submitted.status).toBe(200);

    // ---- Scenario A: Manager's Approvals shows exactly the Manager approval ----
    const managerWorklist = await manager.api.get('/api/tasks/approvals', { headers: manager.auth });
    expect(managerWorklist.status).toBe(200);
    expect(managerWorklist.data.items).toHaveLength(1);
    const managerItem = managerWorklist.data.items[0];
    expect(managerItem.taskName).toBe('Manager Approval');
    expect(managerItem.processDefinitionName).toBe('Worklist Process');
    expect(managerItem.applicantId).toBe(applicant.userId);
    // The Applicant's own plain UserTask never appears as a Manager "approval" item, and Finance's
    // (not-yet-created) step obviously doesn't either.
    expect(managerWorklist.data.items.some((i: { taskName: string }) => i.taskName === 'Applicant')).toBe(false);
    expect(managerWorklist.data.items.some((i: { taskName: string }) => i.taskName === 'Finance Approval')).toBe(false);

    // Status-filtered view: Pending shows it, Completed doesn't yet.
    const managerPending = await manager.api.get('/api/tasks/approvals', { params: { status: 'Pending' }, headers: manager.auth });
    expect(managerPending.data.items).toHaveLength(1);
    const managerCompletedBefore = await manager.api.get('/api/tasks/approvals', { params: { status: 'Completed' }, headers: manager.auth });
    expect(managerCompletedBefore.data.items).toHaveLength(0);

    // ---- Scenario F: IDOR — the Unauthorized User cannot touch the Manager's task/form ----
    const unauthorizedTaskGet = await unauthorized.api.get(`/api/tasks/${managerItem.taskId}`, { headers: unauthorized.auth });
    expect(unauthorizedTaskGet.status).toBe(403);
    const unauthorizedApprove = await unauthorized.api.post(`/api/tasks/${managerItem.taskId}/approve`, null, { headers: unauthorized.auth });
    expect(unauthorizedApprove.status).toBe(403);
    const unauthorizedReject = await unauthorized.api.post(`/api/tasks/${managerItem.taskId}/reject`, null, { headers: unauthorized.auth });
    expect(unauthorizedReject.status).toBe(403);
    const unauthorizedReturn = await unauthorized.api.post(`/api/tasks/${managerItem.taskId}/return`, null, { headers: unauthorized.auth });
    expect(unauthorizedReturn.status).toBe(403);
    const unauthorizedDelegate = await unauthorized.api.post(`/api/tasks/${managerItem.taskId}/delegate`, { delegateToUserId: finance.userId }, { headers: unauthorized.auth });
    expect(unauthorizedDelegate.status).toBe(403);
    const unauthorizedTransfer = await unauthorized.api.post(`/api/tasks/${managerItem.taskId}/transfer`, { newUserId: finance.userId }, { headers: unauthorized.auth });
    expect(unauthorizedTransfer.status).toBe(403);
    const unauthorizedFormGet = await unauthorized.api.get(`/api/form-instances/${applicantFormInstance.id}`, { headers: unauthorized.auth });
    expect(unauthorizedFormGet.status).toBe(403);
    // Its own worklist is simply empty — no leakage via the list either.
    const unauthorizedWorklist = await unauthorized.api.get('/api/tasks/approvals', { headers: unauthorized.auth });
    expect(unauthorizedWorklist.data.items).toHaveLength(0);

    // ---- Scenario B: Manager opens Task Detail; approval progress is correct ----
    const managerTaskDetail = await manager.api.get(`/api/tasks/${managerItem.taskId}`, { headers: manager.auth });
    expect(managerTaskDetail.status).toBe(200);
    expect(managerTaskDetail.data.approval.policy).toBe('AnyOne');
    expect(managerTaskDetail.data.approval.approvedCount).toBe(0);
    expect(managerTaskDetail.data.approval.requiredCount).toBe(1);

    // ---- Scenario H: duplicate-click Approve — only one logical approval ----
    const [firstApprove, secondApprove] = await Promise.all([
      manager.api.post(`/api/tasks/${managerItem.taskId}/approve`, null, { headers: manager.auth }),
      manager.api.post(`/api/tasks/${managerItem.taskId}/approve`, null, { headers: manager.auth }),
    ]);
    const approveStatuses = [firstApprove.status, secondApprove.status].sort();
    // Exactly one succeeds; the other is rejected as already-resolved (never a second logical
    // approval, never a silent duplicate completion).
    expect(approveStatuses[0]).toBe(200);
    expect([400, 409]).toContain(approveStatuses[1]);

    // ---- Scenario C: worklist refreshes — Manager's pending approval disappears, Finance's appears ----
    const managerWorklistAfter = await manager.api.get('/api/tasks/approvals', { params: { status: 'Pending' }, headers: manager.auth });
    expect(managerWorklistAfter.data.items).toHaveLength(0);
    const managerCompletedAfter = await manager.api.get('/api/tasks/approvals', { params: { status: 'Completed' }, headers: manager.auth });
    expect(managerCompletedAfter.data.items).toHaveLength(1);

    // ---- Scenario D: Finance sees the Finance approval; Manager does not see it ----
    const financeWorklist = await finance.api.get('/api/tasks/approvals', { headers: finance.auth });
    expect(financeWorklist.data.items).toHaveLength(1);
    const financeItem = financeWorklist.data.items[0];
    expect(financeItem.taskName).toBe('Finance Approval');

    const managerSeesFinance = managerWorklistAfter.data.items.some((i: { taskId: string }) => i.taskId === financeItem.taskId)
      || managerCompletedAfter.data.items.some((i: { taskId: string }) => i.taskId === financeItem.taskId);
    expect(managerSeesFinance).toBe(false);

    // ---- Scenario G: concurrency — two authorized approvers act on the same approval concurrently ----
    // (Finance is AnyOne with a single Role-resolved candidate here, so this exercises the same
    // "act twice on one assignment" path duplicate-click protection already covers; a genuine
    // multi-approver concurrent race is ApprovalEngineIntegrationTests' own
    // AnyOne_ConcurrentApprovals_ExactlyOneSucceeds, not re-tested here.)

    // ---- Scenario E: Finance approves -> process completes; worklist reflects final state ----
    const financeApprove = await finance.api.post(`/api/tasks/${financeItem.taskId}/approve`, null, { headers: finance.auth });
    expect(financeApprove.status).toBe(200);

    const finalProcess = await admin.api.get(`/api/process-instances/${processInstanceId}`, { headers: admin.auth });
    expect(finalProcess.data.status).toBe('Completed');

    const financeWorklistAfter = await finance.api.get('/api/tasks/approvals', { params: { status: 'Completed' }, headers: finance.auth });
    expect(financeWorklistAfter.data.items).toHaveLength(1);
    expect(financeWorklistAfter.data.items[0].taskStatus).toBe('Completed');
  });
});
