import axios from 'axios';
import { describe, expect, it } from 'vitest';

// Phase 6.3 — SLA Foundation, run against the real rebuilt Docker stack with genuinely distinct
// logged-in users, covering this phase's own Scenarios A-H over real HTTP, real PostgreSQL, and
// the real Workflow/Approval Engine. No scheduler, no background worker, no automatic Warning/
// Overdue transitions exist yet (Phase 6.4) — every assertion here is about SLA *creation* and
// *lifecycle-linked completion*, never time-based state transitions.
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

async function createAndLogin(admin: Awaited<ReturnType<typeof loginAsAdmin>>, username: string, displayName: string) {
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

// A collision-resistant suffix (not just Date.now()) — this repo's live suite runs several
// *.live.test.ts files in parallel, and a bare millisecond timestamp has occasionally collided
// across files (a known, pre-existing flake in other live tests, not introduced here).
function uniqueSuffix() {
  return `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

describe('SLA Foundation against the live backend', () => {
  it('covers Scenarios A-C, E-H: UserTask/ApprovalTask SLA creation, no-policy, completion, policy-change stability, and cross-user security', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();

    const applicant = await createAndLogin(admin, `sla-app-${suffix}`, 'SLA Applicant');
    const approver = await createAndLogin(admin, `sla-appr-${suffix}`, 'SLA Approver');
    const unrelated = await createAndLogin(admin, `sla-unrel-${suffix}`, 'SLA Unrelated');

    const approverRole = await admin.api.post('/api/roles', { name: `SlaApprover-${suffix}` }, { headers: admin.auth });
    expect(approverRole.status).toBe(200);
    await admin.api.post('/api/roles/assign', { userId: approver.userId, roleId: approverRole.data.id }, { headers: admin.auth });

    // Applicant (UserTask, ProcessInitiator, WITH an SLA policy) -> Approval (ApprovalTask, WITH
    // an SLA policy) -> NoSla (UserTask, deliberately no policy — Scenario C) -> End.
    const processCreated = await admin.api.post('/api/process-definitions', { key: `sla-proc-${suffix}`, name: 'SLA Test Process' }, { headers: admin.auth });
    expect(processCreated.status).toBe(201);
    const processId = processCreated.data.id as string;
    const definition = {
      nodes: [
        { id: 'start', type: 'Start', name: 'Start' },
        { id: 'applicant', type: 'UserTask', name: 'Applicant', assignment: { type: 'ProcessInitiator', value: '' } },
        { id: 'approval', type: 'ApprovalTask', name: 'Approval', approval: { policy: 'AnyOne', assignments: [{ type: 'Role', value: approverRole.data.name }] } },
        { id: 'nosla', type: 'UserTask', name: 'No SLA Step', assignment: { type: 'ProcessInitiator', value: '' } },
        { id: 'end', type: 'End', name: 'End' },
      ],
      transitions: [
        { id: 't1', source: 'start', target: 'applicant' },
        { id: 't2', source: 'applicant', target: 'approval' },
        { id: 't3', source: 'approval', target: 'nosla' },
        { id: 't4', source: 'nosla', target: 'end' },
      ],
    };

    // ---- Scenario F setup: create the Applicant policy BEFORE starting Task A ----
    const applicantPolicy = await admin.api.post('/api/sla-policies', { processDefinitionId: processId, nodeId: 'applicant', enabled: true, durationMinutes: 60, warningOffsetMinutes: 30 }, { headers: admin.auth });
    expect(applicantPolicy.status).toBe(200);
    const approvalPolicy = await admin.api.post('/api/sla-policies', { processDefinitionId: processId, nodeId: 'approval', enabled: true, durationMinutes: 480, warningOffsetMinutes: 60 }, { headers: admin.auth });
    expect(approvalPolicy.status).toBe(200);
    // 'nosla' deliberately gets no policy at all (Scenario C).

    await admin.api.post(`/api/process-definitions/${processId}/versions`, { definition }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processId}/publish`, null, { headers: admin.auth });

    const startedA = await applicant.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: applicant.auth });
    expect(startedA.status).toBe(201);
    const processInstanceIdA = startedA.data.id as string;

    // ---- Scenario A: UserTask SLA ----
    const applicantTasksA = await applicant.api.get('/api/tasks', { headers: applicant.auth });
    const applicantTaskA = applicantTasksA.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceIdA);
    expect(applicantTaskA).toBeTruthy();

    const applicantTaskDetailA = await applicant.api.get(`/api/tasks/${applicantTaskA.id}`, { headers: applicant.auth });
    expect(applicantTaskDetailA.status).toBe(200);
    expect(applicantTaskDetailA.data.sla).toBeTruthy();
    expect(applicantTaskDetailA.data.sla.status).toBe('Active');
    expect(applicantTaskDetailA.data.dueAt).toBeTruthy(); // TaskInstance.DueAt mirror.
    const taskADueAt = applicantTaskDetailA.data.sla.dueAt as string;
    const startedAtA = new Date(applicantTaskDetailA.data.sla.startedAt).getTime();
    const dueAtA = new Date(taskADueAt).getTime();
    expect(dueAtA - startedAtA).toBeCloseTo(60 * 60 * 1000, -3); // 60 minutes, within ~seconds tolerance.

    // ---- Scenario G (cross-user security): Unrelated cannot view Applicant's task or its SLA ----
    const idorAccess = await unrelated.api.get(`/api/tasks/${applicantTaskA.id}`, { headers: unrelated.auth });
    expect(idorAccess.status).toBe(403);
    // The admin-only SLA policy endpoints reject a non-admin outright too.
    const policyAccessDenied = await unrelated.api.get('/api/sla-policies', { headers: unrelated.auth });
    expect(policyAccessDenied.status).toBe(403);

    await applicant.api.post(`/api/tasks/${applicantTaskA.id}/complete`, null, { headers: applicant.auth });

    // ---- Scenario B: ApprovalTask SLA ----
    const approverTasksA = await approver.api.get('/api/tasks', { headers: approver.auth });
    const approvalTaskA = approverTasksA.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceIdA);
    expect(approvalTaskA).toBeTruthy();

    const approvalTaskDetail = await approver.api.get(`/api/tasks/${approvalTaskA.id}`, { headers: approver.auth });
    expect(approvalTaskDetail.data.sla).toBeTruthy();
    expect(approvalTaskDetail.data.sla.status).toBe('Active');
    expect(approvalTaskDetail.data.assigneeRole ?? approvalTaskDetail.data.approval).toBeTruthy(); // real approver/task relationship exists.

    const approve = await approver.api.post(`/api/tasks/${approvalTaskA.id}/approve`, null, { headers: approver.auth });
    expect(approve.status).toBe(200);

    // ---- Scenario E: Completion ----
    const approvalTaskAfterApprove = await approver.api.get(`/api/tasks/${approvalTaskA.id}`, { headers: approver.auth });
    expect(approvalTaskAfterApprove.data.sla.status).toBe('Completed');
    expect(approvalTaskAfterApprove.data.sla.completedAt).toBeTruthy();
    // Historical WarningAt/DueAt values remain exactly what was calculated at creation.
    expect(approvalTaskAfterApprove.data.sla.dueAt).toBe(approvalTaskDetail.data.sla.dueAt);
    expect(approvalTaskAfterApprove.data.sla.warningAt).toBe(approvalTaskDetail.data.sla.warningAt);

    // ---- Scenario C: No Policy ----
    const applicantTasksNoSla = await applicant.api.get('/api/tasks', { headers: applicant.auth });
    const noSlaTask = applicantTasksNoSla.data.items.find((t: { processInstanceId: string; nodeId: string }) => t.processInstanceId === processInstanceIdA && t.nodeId === 'nosla');
    expect(noSlaTask).toBeTruthy();
    const noSlaTaskDetail = await applicant.api.get(`/api/tasks/${noSlaTask.id}`, { headers: applicant.auth });
    expect(noSlaTaskDetail.data.sla).toBeNull();
    expect(noSlaTaskDetail.data.dueAt).toBeNull();

    // ---- Scenario F: Policy Change ----
    // Task A's Applicant SLA (already completed above) keeps its original 60-minute-derived DueAt
    // even after the policy changes to 180 minutes for future tasks.
    const updatedPolicy = await admin.api.put(`/api/sla-policies/${applicantPolicy.data.id}`, { enabled: true, durationMinutes: 180, warningOffsetMinutes: 90, expectedVersion: applicantPolicy.data.rowVersion }, { headers: admin.auth });
    expect(updatedPolicy.status).toBe(200);

    const applicantTaskAAfterPolicyChange = await admin.api.get(`/api/tasks/${applicantTaskA.id}`, { headers: admin.auth });
    expect(applicantTaskAAfterPolicyChange.data.sla.dueAt).toBe(taskADueAt);

    const startedB = await createAndLogin(admin, `sla-app2-${suffix}`, 'SLA Applicant 2');
    const startedProcessB = await startedB.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: startedB.auth });
    expect(startedProcessB.status).toBe(201);
    const applicantTasksB = await startedB.api.get('/api/tasks', { headers: startedB.auth });
    const applicantTaskB = applicantTasksB.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === startedProcessB.data.id);
    const applicantTaskDetailB = await startedB.api.get(`/api/tasks/${applicantTaskB.id}`, { headers: startedB.auth });

    const startedAtB = new Date(applicantTaskDetailB.data.sla.startedAt).getTime();
    const dueAtB = new Date(applicantTaskDetailB.data.sla.dueAt).getTime();
    expect(dueAtB - startedAtB).toBeCloseTo(180 * 60 * 1000, -3); // Task B uses the new 180-minute duration.

    // ---- Scenario D: Disabled Policy ----
    const disablePolicy = await admin.api.put(`/api/sla-policies/${approvalPolicy.data.id}`, { enabled: false, durationMinutes: 480, warningOffsetMinutes: 60, expectedVersion: approvalPolicy.data.rowVersion }, { headers: admin.auth });
    expect(disablePolicy.status).toBe(200);

    await startedB.api.post(`/api/tasks/${applicantTaskB.id}/complete`, null, { headers: startedB.auth });
    const startedBApprovalTasks = await approver.api.get('/api/tasks', { headers: approver.auth });
    const approvalTaskB = startedBApprovalTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === startedProcessB.data.id);
    expect(approvalTaskB).toBeTruthy();
    const approvalTaskBDetail = await approver.api.get(`/api/tasks/${approvalTaskB.id}`, { headers: approver.auth });
    expect(approvalTaskBDetail.data.sla).toBeNull(); // Disabled policy -> no new Active SLA created.
  });
});
