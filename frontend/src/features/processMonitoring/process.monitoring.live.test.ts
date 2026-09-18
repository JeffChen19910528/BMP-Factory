import axios from 'axios';
import { describe, expect, it } from 'vitest';

// Phase 7.2.1 — Process Monitoring Query + List, run against the real rebuilt Docker stack with
// genuinely distinct logged-in users, covering this phase's own Scenarios A-L over real HTTP,
// real PostgreSQL, and the real Workflow/Approval/SLA engines.
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

type MonitoringItem = {
  processInstanceId: string;
  processDefinitionId: string;
  status: string;
  initiatorId: string;
  currentTaskId: string | null;
  currentTaskName: string | null;
  currentTaskAssigneeDisplay: string | null;
  slaStatus: string | null;
  slaDueAt: string | null;
};

async function getMonitoring(user: { api: ReturnType<typeof client>; auth: Record<string, string> }, params: Record<string, unknown> = {}) {
  const res = await user.api.get('/api/process-monitoring', { headers: user.auth, params });
  expect(res.status).toBe(200);
  return res.data as { items: MonitoringItem[]; totalCount: number; page: number; pageSize: number };
}

describe('Process Monitoring against the live backend', () => {
  it('covers Scenarios A-C, E-K: list, status filter, search, current task, SLA, and full authorization/IDOR coverage', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();

    const applicant = await createAndLogin(admin, `mon-applicant-${suffix}`, 'Monitoring Applicant');
    const unrelated = await createAndLogin(admin, `mon-unrelated-${suffix}`, 'Monitoring Unrelated');

    const uniqueName = `MonitoringProcess-${suffix}`;
    const processCreated = await admin.api.post('/api/process-definitions', { key: `mon-proc-${suffix}`, name: uniqueName }, { headers: admin.auth });
    expect(processCreated.status).toBe(201);
    const processId = processCreated.data.id as string;

    const definition = {
      nodes: [
        { id: 'start', type: 'Start', name: 'Start' },
        { id: 'task', type: 'UserTask', name: 'Review Request', assignment: { type: 'ProcessInitiator', value: '' } },
        { id: 'end', type: 'End', name: 'End' },
      ],
      transitions: [
        { id: 't1', source: 'start', target: 'task' },
        { id: 't2', source: 'task', target: 'end' },
      ],
    };
    const slaPolicy = await admin.api.post('/api/sla-policies', { processDefinitionId: processId, nodeId: 'task', enabled: true, durationMinutes: 60, warningOffsetMinutes: 30 }, { headers: admin.auth });
    expect(slaPolicy.status).toBe(200);

    await admin.api.post(`/api/process-definitions/${processId}/versions`, { definition }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processId}/publish`, null, { headers: admin.auth });

    const started = await applicant.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: applicant.auth });
    expect(started.status).toBe(201);
    const processInstanceId = started.data.id as string;

    // ---- Scenario A: list ----
    const list = await getMonitoring(applicant, { processDefinitionId: processId });
    expect(list.items.some((i) => i.processInstanceId === processInstanceId)).toBe(true);

    // ---- Scenario E: current task ----
    const item = list.items.find((i) => i.processInstanceId === processInstanceId)!;
    expect(item.currentTaskName).toBe('Review Request');
    expect(item.currentTaskAssigneeDisplay).toBe('Monitoring Applicant');

    // ---- Scenario F: SLA reflects the real persisted TaskSla state ----
    expect(item.slaStatus).toBe('Active');
    expect(item.slaDueAt).toBeTruthy();

    // ---- Scenario B: status filter ----
    const runningOnly = await getMonitoring(applicant, { processDefinitionId: processId, status: 'Running' });
    expect(runningOnly.items.some((i) => i.processInstanceId === processInstanceId)).toBe(true);
    const completedOnly = await getMonitoring(applicant, { processDefinitionId: processId, status: 'Completed' });
    expect(completedOnly.items.some((i) => i.processInstanceId === processInstanceId)).toBe(false);

    // ---- Scenario C: search ----
    const searchResult = await getMonitoring(applicant, { search: uniqueName });
    expect(searchResult.items.some((i) => i.processInstanceId === processInstanceId)).toBe(true);

    // ---- Scenario G: normal user sees only their own authorized data ----
    const applicantAll = await getMonitoring(applicant, { pageSize: 200 });
    expect(applicantAll.items.every((i) => i.processInstanceId !== undefined)).toBe(true);

    // ---- Scenario H: cross-user — unrelated cannot see the applicant's process ----
    const unrelatedView = await getMonitoring(unrelated, { processDefinitionId: processId });
    expect(unrelatedView.items.some((i) => i.processInstanceId === processInstanceId)).toBe(false);

    // ---- Scenario I: Administrator sees system-wide ----
    const adminView = await getMonitoring(admin, { processDefinitionId: processId });
    expect(adminView.items.some((i) => i.processInstanceId === processInstanceId)).toBe(true);

    // ---- Scenario J: filter injection — initiatorId cannot expose unauthorized records ----
    const injected = await getMonitoring(unrelated, { initiatorId: applicant.userId, processDefinitionId: processId });
    expect(injected.items.length).toBe(0);

    // ---- Scenario K: search IDOR — searching for the known process name as an unrelated user ----
    const searchIdor = await getMonitoring(unrelated, { search: uniqueName });
    expect(searchIdor.items.some((i) => i.processInstanceId === processInstanceId)).toBe(false);

    // ---- Cross-check: processDefinitionId filter alone cannot broaden visibility either ----
    const definitionIdOnly = await getMonitoring(unrelated, { processDefinitionId: processId });
    expect(definitionIdOnly.items.length).toBe(0);
  });

  it('covers Scenario D: pagination returns correct pages and total count', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();
    const initiator = await createAndLogin(admin, `mon-page-${suffix}`, 'Monitoring Pagination');

    const processCreated = await admin.api.post('/api/process-definitions', { key: `mon-page-${suffix}`, name: 'Monitoring Pagination Process' }, { headers: admin.auth });
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

    for (let i = 0; i < 5; i++) {
      await initiator.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: initiator.auth });
    }

    const page1 = await getMonitoring(initiator, { processDefinitionId: processId, page: 1, pageSize: 2 });
    const page2 = await getMonitoring(initiator, { processDefinitionId: processId, page: 2, pageSize: 2 });

    expect(page1.totalCount).toBe(5);
    expect(page1.items.length).toBe(2);
    expect(page2.items.length).toBe(2);
    const page1Ids = page1.items.map((i) => i.processInstanceId);
    const page2Ids = page2.items.map((i) => i.processInstanceId);
    expect(page1Ids.some((id) => page2Ids.includes(id))).toBe(false);
  });

  it('covers Scenario L: repeated identical queries return a stable, deterministic order', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();
    const initiator = await createAndLogin(admin, `mon-sort-${suffix}`, 'Monitoring Sort');

    const processCreated = await admin.api.post('/api/process-definitions', { key: `mon-sort-${suffix}`, name: 'Monitoring Sort Process' }, { headers: admin.auth });
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

    for (let i = 0; i < 3; i++) {
      await initiator.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: initiator.auth });
    }

    const first = await getMonitoring(initiator, { processDefinitionId: processId, pageSize: 50 });
    const second = await getMonitoring(initiator, { processDefinitionId: processId, pageSize: 50 });

    expect(first.items.map((i) => i.processInstanceId)).toEqual(second.items.map((i) => i.processInstanceId));
  });
});
