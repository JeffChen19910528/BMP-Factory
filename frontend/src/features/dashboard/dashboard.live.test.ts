import axios from 'axios';
import { describe, expect, it } from 'vitest';

// Phase 7.1 — Dashboard Foundation, run against the real rebuilt Docker stack with genuinely
// distinct logged-in users, covering this phase's own Scenarios A-J over real HTTP, real
// PostgreSQL, and the real Workflow/Approval/SLA engines. No deterministic clock is exercised here
// (Part 23: "do not wait for real SLA time") — Warning/Overdue aggregation determinism is already
// covered exhaustively at the backend level (DashboardQueryServiceTests, using a FakeClock and the
// real SlaProcessor); this live test only proves the wiring end to end over real HTTP.
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

function uniqueSuffix() {
  return `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

type DashboardResponse = {
  myTasks: { total: number; overdue: number; dueSoon: number };
  pendingApprovals: { pending: number };
  sla: { active: number; warning: number; overdue: number; completed: number };
  processOverview: { running: number; completed: number; rejected: number };
  recentActivity: { timestamp: string; action: string; description: string; processInstanceId: string | null }[];
};

async function getDashboard(user: { api: ReturnType<typeof client>; auth: Record<string, string> }) {
  const res = await user.api.get<DashboardResponse>('/api/dashboard', { headers: user.auth });
  expect(res.status).toBe(200);
  return res.data;
}

describe('Dashboard against the live backend', () => {
  it('covers Scenarios A-C, F-I: shape, personal task/approval/SLA counts, recent activity, admin view, and userId-injection immunity', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();

    // ---- Scenario A: empty/initial dashboard for a brand-new user ----
    const applicant = await createAndLogin(admin, `dash-applicant-${suffix}`, 'Dashboard Applicant');
    const approver = await createAndLogin(admin, `dash-approver-${suffix}`, 'Dashboard Approver');

    const initial = await getDashboard(applicant);
    expect(initial.myTasks).toEqual({ total: 0, overdue: 0, dueSoon: 0 });
    expect(initial.pendingApprovals).toEqual({ pending: 0 });
    expect(initial.sla.active + initial.sla.warning + initial.sla.overdue + initial.sla.completed).toBe(0);
    expect(Array.isArray(initial.recentActivity)).toBe(true);

    // ---- Scenario I: unauthenticated request is rejected ----
    const unauthenticated = await client().get('/api/dashboard');
    expect(unauthenticated.status).toBe(401);

    // ---- Scenario I continued: userId query-parameter injection changes nothing ----
    const injected = await applicant.api.get(`/api/dashboard?userId=${admin.userId}`, { headers: applicant.auth });
    expect(injected.status).toBe(200);
    expect(injected.data.myTasks.total).toBe(0); // still the applicant's own (empty) scope, not admin's.

    const approverRole = await admin.api.post('/api/roles', { name: `DashApprover-${suffix}` }, { headers: admin.auth });
    expect(approverRole.status).toBe(200);
    await admin.api.post('/api/roles/assign', { userId: approver.userId, roleId: approverRole.data.id }, { headers: admin.auth });

    const processCreated = await admin.api.post('/api/process-definitions', { key: `dash-proc-${suffix}`, name: 'Dashboard Test Process' }, { headers: admin.auth });
    expect(processCreated.status).toBe(201);
    const processId = processCreated.data.id as string;

    const definition = {
      nodes: [
        { id: 'start', type: 'Start', name: 'Start' },
        { id: 'applicant', type: 'UserTask', name: 'Applicant Step', assignment: { type: 'ProcessInitiator', value: '' } },
        { id: 'approval', type: 'ApprovalTask', name: 'Approval', approval: { policy: 'AnyOne', assignments: [{ type: 'Role', value: approverRole.data.name }] } },
        { id: 'end', type: 'End', name: 'End' },
      ],
      transitions: [
        { id: 't1', source: 'start', target: 'applicant' },
        { id: 't2', source: 'applicant', target: 'approval' },
        { id: 't3', source: 'approval', target: 'end' },
      ],
    };

    const slaPolicy = await admin.api.post('/api/sla-policies', { processDefinitionId: processId, nodeId: 'applicant', enabled: true, durationMinutes: 60, warningOffsetMinutes: 30 }, { headers: admin.auth });
    expect(slaPolicy.status).toBe(200);

    await admin.api.post(`/api/process-definitions/${processId}/versions`, { definition }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processId}/publish`, null, { headers: admin.auth });

    const started = await applicant.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: applicant.auth });
    expect(started.status).toBe(201);
    const processInstanceId = started.data.id as string;

    // ---- Scenario B: personal task count ----
    const afterStart = await getDashboard(applicant);
    expect(afterStart.myTasks.total).toBe(1);

    // ---- Scenario D: SLA summary reflects the newly-created Active TaskSla ----
    expect(afterStart.sla.active).toBeGreaterThanOrEqual(1);

    // ---- Scenario F: Recent Activity contains the StartProcess event ----
    expect(afterStart.recentActivity.some((a) => a.action === 'StartProcess' && a.processInstanceId === processInstanceId)).toBe(true);

    const applicantTasks = await applicant.api.get('/api/tasks', { headers: applicant.auth });
    const applicantTask = applicantTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceId);
    await applicant.api.post(`/api/tasks/${applicantTask.id}/complete`, null, { headers: applicant.auth });

    // ---- Scenario C: pending approval count for the approver ----
    const approverDashboard = await getDashboard(approver);
    expect(approverDashboard.pendingApprovals.pending).toBeGreaterThanOrEqual(1);

    // ---- Scenario G: applicant cannot see the approver's pending-approval data ----
    const applicantAfterHandoff = await getDashboard(applicant);
    expect(applicantAfterHandoff.pendingApprovals.pending).toBe(0);
    expect(applicantAfterHandoff.myTasks.total).toBe(0); // the applicant's own task is now completed.

    // ---- Scenario H: Administrator sees system-wide aggregate at least as large as either user's own ----
    const adminDashboard = await getDashboard(admin);
    expect(adminDashboard.pendingApprovals.pending).toBeGreaterThanOrEqual(approverDashboard.pendingApprovals.pending);
    expect(adminDashboard.processOverview.running).toBeGreaterThanOrEqual(1);
  });

  it('covers Scenario E: process overview aggregates Running and Completed correctly for the Administrator', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();

    const initiator = await createAndLogin(admin, `dash-proc-${suffix}`, 'Dashboard Process Owner');
    const processCreated = await admin.api.post('/api/process-definitions', { key: `dash-simple-${suffix}`, name: 'Dashboard Simple Process' }, { headers: admin.auth });
    expect(processCreated.status).toBe(201);
    const processId = processCreated.data.id as string;

    const definition = {
      nodes: [
        { id: 'start', type: 'Start', name: 'Start' },
        { id: 'task', type: 'UserTask', name: 'Task', assignment: { type: 'ProcessInitiator', value: '' } },
        { id: 'end', type: 'End', name: 'End' },
      ],
      transitions: [
        { id: 't1', source: 'start', target: 'task' },
        { id: 't2', source: 'task', target: 'end' },
      ],
    };
    await admin.api.post(`/api/process-definitions/${processId}/versions`, { definition }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processId}/publish`, null, { headers: admin.auth });

    const runningInstance = await initiator.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: initiator.auth });
    expect(runningInstance.status).toBe(201);

    const completedInstance = await initiator.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: initiator.auth });
    expect(completedInstance.status).toBe(201);
    const completedTasks = await initiator.api.get('/api/tasks', { headers: initiator.auth });
    const completedTask = completedTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === completedInstance.data.id);
    await initiator.api.post(`/api/tasks/${completedTask.id}/complete`, null, { headers: initiator.auth });

    // The system-wide admin aggregate is NOT monotonic under this suite's real parallel execution
    // (other *.live.test.ts files complete their own Running instances concurrently, which can
    // transiently *decrease* the system-wide Running count between two snapshots even while this
    // test's own instance stays Running) — so exact/delta assertions against a "before" snapshot
    // would be genuinely flaky, not a product bug. Instead, assert against the initiator's own
    // personal dashboard, which is scoped to exactly the two instances this test created and is
    // therefore immune to any other test's concurrent activity.
    const initiatorDashboard = await getDashboard(initiator);
    expect(initiatorDashboard.processOverview.running).toBe(1);
    expect(initiatorDashboard.processOverview.completed).toBe(1);

    // The Administrator's aggregate must still be at least as large as the initiator's own scoped
    // view (a loose, parallel-safe sanity check that the admin branch is genuinely unfiltered).
    const adminDashboard = await getDashboard(admin);
    expect(adminDashboard.processOverview.running).toBeGreaterThanOrEqual(initiatorDashboard.processOverview.running);
    expect(adminDashboard.processOverview.completed).toBeGreaterThanOrEqual(initiatorDashboard.processOverview.completed);
  });
});
