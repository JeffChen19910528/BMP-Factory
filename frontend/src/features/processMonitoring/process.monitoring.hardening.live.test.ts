import axios from 'axios';
import { describe, expect, it } from 'vitest';

// Phase 7.2.3 — Operational Monitoring Hardening, run against the real rebuilt Docker stack.
// This file deliberately does NOT re-test everything 7.2.1's process.monitoring.live.test.ts and
// 7.2.2's process.instance.detail.live.test.ts already cover (basic list/detail/IDOR/pagination/
// sorting/ProcessVersion/timeline) — it adds genuinely new coverage: cross-view SLA consistency
// (Dashboard vs. Process Monitoring vs. Process Detail — the actual bug this phase found and
// fixed), invalid-parameter handling, and a read-only concurrency/consistency smoke sequence.
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

async function getMonitoring(user: { api: ReturnType<typeof client>; auth: Record<string, string> }, params: Record<string, unknown> = {}) {
  return user.api.get('/api/process-monitoring', { headers: user.auth, params });
}

async function getDetail(user: { api: ReturnType<typeof client>; auth: Record<string, string> }, id: string) {
  return user.api.get(`/api/process-monitoring/${id}`, { headers: user.auth });
}

async function getDashboard(user: { api: ReturnType<typeof client>; auth: Record<string, string> }) {
  return user.api.get('/api/dashboard', { headers: user.auth });
}

describe('Process Monitoring hardening against the live backend', () => {
  it('Scenario: Dashboard, Process Monitoring, and Process Detail never contradict each other for the same TaskSla', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();
    const applicant = await createAndLogin(admin, `hard-sla-${suffix}`, 'Hardening SLA User');

    const processCreated = await admin.api.post('/api/process-definitions', { key: `hard-sla-proc-${suffix}`, name: 'Hardening SLA Process' }, { headers: admin.auth });
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
    await admin.api.post('/api/sla-policies', { processDefinitionId: processId, nodeId: 'task', enabled: true, durationMinutes: 60, warningOffsetMinutes: 30 }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processId}/versions`, { definition }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processId}/publish`, null, { headers: admin.auth });

    const started = await applicant.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: applicant.auth });
    const processInstanceId = started.data.id as string;

    // ---- Scenario A: Running process — list and detail agree ----
    const listBefore = await getMonitoring(applicant, { processDefinitionId: processId });
    const itemBefore = listBefore.data.items.find((i: { processInstanceId: string }) => i.processInstanceId === processInstanceId);
    const detailBefore = await getDetail(applicant, processInstanceId);
    expect(itemBefore.status).toBe(detailBefore.data.status);
    expect(itemBefore.slaStatus).toBe('Active');
    expect(detailBefore.data.slaStatus).toBe('Active');

    const dashboardBefore = await getDashboard(applicant);
    expect(dashboardBefore.data.sla.active).toBeGreaterThanOrEqual(1);

    // ---- Scenario D/cross-view: no deterministic SLA time-travel available over live HTTP (the
    // backend's real SlaSchedulerWorker runs on its own configured interval — Warning/Overdue
    // determinism is already exhaustively covered at the backend-test level with a FakeClock; see
    // ProcessMonitoringQueryServiceTests/ProcessInstanceDetailQueryServiceTests/
    // DashboardQueryServiceTests). This live scenario instead proves the three endpoints' SLA
    // fields are *structurally* consistent right now, for the same real persisted TaskSla. ----
    expect(itemBefore.slaDueAt).toBe(detailBefore.data.slaSummary.dueAt);

    // ---- Scenario G: Completed process — no current task, consistent across list/detail ----
    const tasks = await applicant.api.get('/api/tasks', { headers: applicant.auth });
    const task = tasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceId);
    await applicant.api.post(`/api/tasks/${task.id}/complete`, null, { headers: applicant.auth });

    const listAfter = await getMonitoring(applicant, { processDefinitionId: processId });
    const itemAfter = listAfter.data.items.find((i: { processInstanceId: string }) => i.processInstanceId === processInstanceId);
    const detailAfter = await getDetail(applicant, processInstanceId);
    expect(itemAfter.status).toBe('Completed');
    expect(detailAfter.data.status).toBe('Completed');
    expect(itemAfter.currentTaskId).toBeNull();
    expect(detailAfter.data.currentTask).toBeNull();
    expect(itemAfter.activeTaskCount).toBe(0);
    expect(detailAfter.data.activeTaskCount).toBe(0);
  });

  it('Scenario: read-only concurrency smoke — monitoring always reflects the latest persisted state across a real task transition', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();
    const initiator = await createAndLogin(admin, `hard-concurrency-${suffix}`, 'Hardening Concurrency');

    const processCreated = await admin.api.post('/api/process-definitions', { key: `hard-conc-${suffix}`, name: 'Hardening Concurrency Process' }, { headers: admin.auth });
    const processId = processCreated.data.id as string;
    const definition = {
      nodes: [
        { id: 'start', type: 'Start', name: 'Start' },
        { id: 'first', type: 'UserTask', name: 'First', assignment: { type: 'ProcessInitiator', value: '' } },
        { id: 'second', type: 'UserTask', name: 'Second', assignment: { type: 'ProcessInitiator', value: '' } },
        { id: 'end', type: 'End', name: 'End' },
      ],
      transitions: [
        { id: 't1', source: 'start', target: 'first' },
        { id: 't2', source: 'first', target: 'second' },
        { id: 't3', source: 'second', target: 'end' },
      ],
    };
    await admin.api.post(`/api/process-definitions/${processId}/versions`, { definition }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processId}/publish`, null, { headers: admin.auth });

    const started = await initiator.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: initiator.auth });
    const processInstanceId = started.data.id as string;

    // 1. Task is active.
    const detail1 = await getDetail(initiator, processInstanceId);
    expect(detail1.data.currentTask.nodeId).toBe('first');

    // 2. Complete task.
    const tasks = await initiator.api.get('/api/tasks', { headers: initiator.auth });
    const firstTask = tasks.data.items.find((t: { processInstanceId: string; nodeId: string }) => t.processInstanceId === processInstanceId && t.nodeId === 'first');
    await initiator.api.post(`/api/tasks/${firstTask.id}/complete`, null, { headers: initiator.auth });

    // 3. Query again — the next task now shows as current, the first shows Completed in history.
    const detail2 = await getDetail(initiator, processInstanceId);
    expect(detail2.data.currentTask.nodeId).toBe('second');
    const firstInHistory = detail2.data.tasks.find((t: { nodeId: string }) => t.nodeId === 'first');
    expect(firstInHistory.status).toBe('Completed');

    // Process Monitoring list agrees.
    const listResult = await getMonitoring(initiator, { processDefinitionId: processId });
    const item = listResult.data.items.find((i: { processInstanceId: string }) => i.processInstanceId === processInstanceId);
    expect(item.currentTaskName).toBe('Second');
  });

  it('Scenario: invalid query parameters degrade safely rather than erroring', async () => {
    const admin = await loginAsAdmin();

    // page=0 and negative page must not throw / must clamp to page 1 semantics.
    const zeroPage = await getMonitoring(admin, { page: 0, pageSize: 10 });
    expect(zeroPage.status).toBe(200);

    const negativePage = await getMonitoring(admin, { page: -5, pageSize: 10 });
    expect(negativePage.status).toBe(200);

    // A page large enough to previously overflow int arithmetic (Part 18 / this phase's own
    // found-and-fixed bug) must return 200 with an empty result, never a 500 leaking SQL.
    const hugePage = await getMonitoring(admin, { page: 2147483647, pageSize: 200 });
    expect(hugePage.status).toBe(200);
    expect(hugePage.data.items).toHaveLength(0);

    // pageSize beyond the maximum must be clamped, not rejected outright or passed through raw.
    const hugePageSize = await getMonitoring(admin, { pageSize: 999999 });
    expect(hugePageSize.status).toBe(200);
    expect(hugePageSize.data.pageSize).toBeLessThanOrEqual(200);

    // Invalid/unrecognized sortBy must never be treated as a raw SQL fragment. ASP.NET Core's own
    // model binder rejects a value that doesn't match the ProcessMonitoringSortBy enum with a
    // clean 400 (safe, not a crash/500) — a 200 with a default-sort fallback would also be safe;
    // both are acceptable, anything else (500, an SQL error surfacing) is not.
    const invalidSort = await getMonitoring(admin, { sortBy: 'Robert\'); DROP TABLE "ProcessInstances"; --' });
    expect([200, 400]).toContain(invalidSort.status);

    // Invalid status/date values must not crash the endpoint.
    const invalidStatus = await getMonitoring(admin, { status: 'NotARealStatus' });
    expect([200, 400]).toContain(invalidStatus.status);

    const invalidDate = await getMonitoring(admin, { startedFrom: 'not-a-date' });
    expect([200, 400]).toContain(invalidDate.status);
  });

  it('Scenario: Administrator sees a coherent, IDOR-safe system-wide monitoring scope', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();
    const userA = await createAndLogin(admin, `hard-admin-a-${suffix}`, 'Hardening Admin User A');
    const userB = await createAndLogin(admin, `hard-admin-b-${suffix}`, 'Hardening Admin User B');

    const processCreated = await admin.api.post('/api/process-definitions', { key: `hard-adm-${suffix}`, name: 'Hardening Admin Process' }, { headers: admin.auth });
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

    const started = await userA.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: userA.auth });
    const processInstanceId = started.data.id as string;

    // userB cannot see userA's process via list or detail, or by manipulating filters.
    const listAsB = await getMonitoring(userB, { processDefinitionId: processId });
    expect(listAsB.data.items.some((i: { processInstanceId: string }) => i.processInstanceId === processInstanceId)).toBe(false);
    const detailAsB = await getDetail(userB, processInstanceId);
    expect(detailAsB.status).toBe(403);
    const initiatorFilterAsB = await getMonitoring(userB, { initiatorId: userA.userId });
    expect(initiatorFilterAsB.data.items.some((i: { processInstanceId: string }) => i.processInstanceId === processInstanceId)).toBe(false);

    // Administrator sees it via both endpoints.
    const listAsAdmin = await getMonitoring(admin, { processDefinitionId: processId });
    expect(listAsAdmin.data.items.some((i: { processInstanceId: string }) => i.processInstanceId === processInstanceId)).toBe(true);
    const detailAsAdmin = await getDetail(admin, processInstanceId);
    expect(detailAsAdmin.status).toBe(200);
  });
});
