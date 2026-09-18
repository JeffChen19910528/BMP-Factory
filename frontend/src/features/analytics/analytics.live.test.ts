import axios from 'axios';
import { describe, expect, it } from 'vitest';

// Phase 7.4 — Analytics, run against the real rebuilt Docker stack with genuinely distinct
// logged-in users, real PostgreSQL, and the real Workflow/Approval/SLA engines. Exact duration
// figures are already covered deterministically in BPM.Tests (real elapsed time here cannot be
// controlled precisely) — this test focuses on exact COUNTS (volume/comparison/node executions),
// date/department filtering, historical ProcessVersion correctness, and full IDOR/authorization
// coverage (Part 54/56/57).
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

function uniqueSuffix() {
  return `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

type User = { api: ReturnType<typeof client>; auth: Record<string, string>; userId: string };

async function getOverview(user: User, params: Record<string, unknown> = {}) {
  const res = await user.api.get('/api/analytics/overview', { headers: user.auth, params });
  expect(res.status).toBe(200);
  return res.data as {
    totalProcesses: number;
    processComparison: { processDefinitionId: string; total: number; completed: number; rejected: number }[];
    nodeAnalytics: { nodeId: string; nodeName: string; executions: number }[];
    volumeTrend: { bucketStart: string; started: number }[];
  };
}

const userTaskDefinition = {
  nodes: [
    { id: 'start', type: 'Start', name: 'Start' },
    { id: 'task', type: 'UserTask', name: 'Review', assignment: { type: 'ProcessInitiator', value: '' } },
    { id: 'end', type: 'End', name: 'End' },
  ],
  transitions: [
    { id: 't1', source: 'start', target: 'task' },
    { id: 't2', source: 'task', target: 'end' },
  ],
};

describe('Analytics against the live backend', () => {
  it('covers exact counts, filters, node analytics, historical ProcessVersion, and full authorization/IDOR coverage', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();

    const orgs = await admin.api.get('/api/organizations', { headers: admin.auth });
    let orgId = orgs.data[0]?.id as string | undefined;
    if (!orgId) {
      const createOrg = await admin.api.post('/api/organizations', { name: `AnOrg-${suffix}`, parentId: null }, { headers: admin.auth });
      expect(createOrg.status).toBe(200);
      orgId = createOrg.data.id;
    }
    const dept = await admin.api.post('/api/departments', { name: `AnDept-${suffix}`, organizationId: orgId, parentId: null, managerUserId: null }, { headers: admin.auth });
    expect(dept.status).toBe(200);
    const deptId = dept.data.id as string;

    const applicant = await createAndLogin(admin, `an-applicant-${suffix}`, 'Analytics Applicant', deptId);
    const unrelated = await createAndLogin(admin, `an-unrelated-${suffix}`, 'Analytics Unrelated');

    const processCreated = await admin.api.post('/api/process-definitions', { key: `an-proc-${suffix}`, name: `AnalyticsProcess-${suffix}` }, { headers: admin.auth });
    expect(processCreated.status).toBe(201);
    const processId = processCreated.data.id as string;
    await admin.api.post(`/api/process-definitions/${processId}/versions`, { definition: userTaskDefinition }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processId}/publish`, null, { headers: admin.auth });

    // Two instances: one completed, one left running.
    const runningInstance = await applicant.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: applicant.auth });
    expect(runningInstance.status).toBe(201);
    const completedInstance = await applicant.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: applicant.auth });
    expect(completedInstance.status).toBe(201);

    const applicantTasks = await applicant.api.get('/api/tasks', { headers: applicant.auth });
    const completeTask = applicantTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === completedInstance.data.id);
    await applicant.api.post(`/api/tasks/${completeTask.id}/complete`, null, { headers: applicant.auth });

    // ---- Exact counts ----
    const overview = await getOverview(applicant, { processDefinitionId: processId });
    expect(overview.totalProcesses).toBe(2);
    const comparison = overview.processComparison.find((c) => c.processDefinitionId === processId)!;
    expect(comparison.total).toBe(2);
    expect(comparison.completed).toBe(1);

    // ---- Node analytics: exact execution count for this process's own node ----
    const node = overview.nodeAnalytics.find((n) => n.nodeId === 'task')!;
    expect(node.nodeName).toBe('Review');
    expect(node.executions).toBe(2);

    // ---- Date range filter ----
    const future = new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString();
    const futureOverview = await getOverview(applicant, { processDefinitionId: processId, from: future });
    expect(futureOverview.totalProcesses).toBe(0);

    // ---- Invalid date range ----
    const past = new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString();
    const invalidRange = await applicant.api.get('/api/analytics/overview', { headers: applicant.auth, params: { from: future, to: past } });
    expect(invalidRange.status).toBe(400);
    expect(invalidRange.data.code).toBe('ANALYTICS_INVALID_DATE_RANGE');
    expect(invalidRange.data.message).not.toMatch(/Npgsql|PostgresException|System\./);

    // ---- Department filter ----
    const deptOverview = await getOverview(applicant, { departmentId: deptId });
    expect(deptOverview.totalProcesses).toBe(2);

    // ---- Part 57: historical ProcessVersion — republish must not change node names Analytics
    // reports for the already-created instances ----
    const renamedDefinition = {
      ...userTaskDefinition,
      nodes: userTaskDefinition.nodes.map((n) => (n.id === 'task' ? { ...n, name: 'Renamed Review' } : n)),
    };
    await admin.api.post(`/api/process-definitions/${processId}/versions`, { definition: renamedDefinition }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processId}/publish`, null, { headers: admin.auth });

    const afterRepublish = await getOverview(applicant, { processDefinitionId: processId });
    const nodeAfter = afterRepublish.nodeAnalytics.find((n) => n.nodeId === 'task')!;
    expect(nodeAfter.nodeName).toBe('Review'); // still the original instances' own snapshot

    // ---- Part 56: security — unrelated user sees none of this data, filter injection leaks nothing ----
    const unrelatedOverview = await getOverview(unrelated, { processDefinitionId: processId });
    expect(unrelatedOverview.totalProcesses).toBe(0);
    expect(unrelatedOverview.processComparison.length).toBe(0);
    expect(unrelatedOverview.nodeAnalytics.length).toBe(0);

    const unrelatedInjected = await getOverview(unrelated, { initiatorId: applicant.userId, processDefinitionId: processId });
    expect(unrelatedInjected.totalProcesses).toBe(0);

    // ---- Administrator: system-wide scope ----
    const adminOverview = await getOverview(admin, { processDefinitionId: processId });
    expect(adminOverview.totalProcesses).toBe(2);

    // ---- Part 50: Monitoring cross-check — same process/status agreement ----
    const monitoring = await applicant.api.get('/api/process-monitoring', { headers: applicant.auth, params: { processDefinitionId: processId } });
    expect(monitoring.status).toBe(200);
    const monitoringIds = monitoring.data.items.map((i: { processInstanceId: string }) => i.processInstanceId);
    expect(monitoringIds).toContain(runningInstance.data.id);
    expect(monitoringIds).toContain(completedInstance.data.id);
  });
});
