import axios from 'axios';
import { describe, expect, it } from 'vitest';

// Phase 7.3 — Reporting, run against the real rebuilt Docker stack with genuinely distinct
// logged-in users, real PostgreSQL, and the real Workflow/Approval/SLA engines. Covers: Summary/
// Breakdown/Task Summary/Approval Summary/SLA Summary, date/process/status/initiator/department
// filters, pagination, sorting, CSV export (+ formula-injection defense), unauthorized-user and
// Administrator scope, and a Reporting-vs-Process-Monitoring cross-check for the same isolated
// data (Part 38-41).
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

async function getSummary(user: User, params: Record<string, unknown> = {}) {
  const res = await user.api.get('/api/reports/summary', { headers: user.auth, params });
  expect(res.status).toBe(200);
  return res.data as {
    process: { total: number; running: number; completed: number; rejected: number };
    processBreakdown: { processDefinitionId: string; total: number; running: number; completed: number; rejected: number }[];
    taskSummary: { total: number; pending: number; completed: number };
    approvalSummary: { total: number; pending: number; approved: number; rejected: number };
    slaSummary: { active: number; warning: number; overdue: number; completed: number; complianceRate: number | null };
  };
}

async function getDetails(user: User, params: Record<string, unknown> = {}) {
  const res = await user.api.get('/api/reports/details', { headers: user.auth, params });
  expect(res.status).toBe(200);
  return res.data as { items: { processInstanceId: string }[]; totalCount: number };
}

const userTaskDefinition = {
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

function approvalDefinition(roleName: string) {
  return {
    nodes: [
      { id: 'start', type: 'Start', name: 'Start' },
      { id: 'approval', type: 'ApprovalTask', name: 'Approval', approval: { policy: 'AnyOne', assignments: [{ type: 'Role', value: roleName }] } },
      { id: 'end', type: 'End', name: 'End' },
    ],
    transitions: [
      { id: 't1', source: 'start', target: 'approval' },
      { id: 't2', source: 'approval', target: 'end' },
    ],
  };
}

describe('Reporting against the live backend', () => {
  it('covers Summary/Breakdown/Task/Approval/SLA aggregation, filters, pagination, sorting, export, and authorization', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();

    const orgs = await admin.api.get('/api/organizations', { headers: admin.auth });
    let orgId = orgs.data[0]?.id as string | undefined;
    if (!orgId) {
      const createOrg = await admin.api.post('/api/organizations', { name: `RptOrg-${suffix}`, parentId: null }, { headers: admin.auth });
      expect(createOrg.status).toBe(200);
      orgId = createOrg.data.id;
    }
    const dept = await admin.api.post('/api/departments', { name: `RptDept-${suffix}`, organizationId: orgId, parentId: null, managerUserId: null }, { headers: admin.auth });
    expect(dept.status).toBe(200);
    const deptId = dept.data.id as string;

    const applicant = await createAndLogin(admin, `rpt-applicant-${suffix}`, 'Report Applicant', deptId);
    const unrelated = await createAndLogin(admin, `rpt-unrelated-${suffix}`, 'Report Unrelated');

    // ---- Plain-UserTask process: one Completed, one left Running ----
    const uProcess = await admin.api.post('/api/process-definitions', { key: `rpt-u-${suffix}`, name: `RptUserTask-${suffix}` }, { headers: admin.auth });
    expect(uProcess.status).toBe(201);
    const uProcessId = uProcess.data.id as string;
    await admin.api.post(`/api/process-definitions/${uProcessId}/versions`, { definition: userTaskDefinition }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${uProcessId}/publish`, null, { headers: admin.auth });

    const runningInstance = await applicant.api.post('/api/process-instances', { processDefinitionKey: uProcess.data.key }, { headers: applicant.auth });
    expect(runningInstance.status).toBe(201);
    const completedInstance = await applicant.api.post('/api/process-instances', { processDefinitionKey: uProcess.data.key }, { headers: applicant.auth });
    expect(completedInstance.status).toBe(201);

    const applicantTasks = await applicant.api.get('/api/tasks', { headers: applicant.auth });
    const completeTask = applicantTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === completedInstance.data.id);
    await applicant.api.post(`/api/tasks/${completeTask.id}/complete`, null, { headers: applicant.auth });

    // ---- Approval process: one Rejected (Reporting can only reach Rejected via an ApprovalTask) ----
    const approverRole = await admin.api.post('/api/roles', { name: `rpt-role-${suffix}` }, { headers: admin.auth });
    expect(approverRole.status).toBe(200);
    await admin.api.post('/api/roles/assign', { userId: applicant.userId, roleId: approverRole.data.id }, { headers: admin.auth });

    const aProcess = await admin.api.post('/api/process-definitions', { key: `rpt-a-${suffix}`, name: `RptApproval-${suffix}` }, { headers: admin.auth });
    expect(aProcess.status).toBe(201);
    const aProcessId = aProcess.data.id as string;
    await admin.api.post(`/api/process-definitions/${aProcessId}/versions`, { definition: approvalDefinition(approverRole.data.name) }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${aProcessId}/publish`, null, { headers: admin.auth });

    const rejectedInstance = await applicant.api.post('/api/process-instances', { processDefinitionKey: aProcess.data.key }, { headers: applicant.auth });
    expect(rejectedInstance.status).toBe(201);
    const applicantTasksAfter = await applicant.api.get('/api/tasks', { headers: applicant.auth });
    const rejectTask = applicantTasksAfter.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === rejectedInstance.data.id);
    const rejectResult = await applicant.api.post(`/api/tasks/${rejectTask.id}/reject`, null, { headers: applicant.auth });
    expect(rejectResult.status).toBe(200);

    // ---- Process Summary: exact expected counts for this isolated scope ----
    const summary = await getSummary(applicant, { processDefinitionId: uProcessId });
    expect(summary.process.total).toBe(2);
    expect(summary.process.running).toBe(1);
    expect(summary.process.completed).toBe(1);
    expect(summary.process.rejected).toBe(0);

    const approvalSummary = await getSummary(applicant, { processDefinitionId: aProcessId });
    expect(approvalSummary.process.total).toBe(1);
    expect(approvalSummary.process.rejected).toBe(1);

    // ---- Process Breakdown ----
    const breakdown = await getSummary(applicant);
    const uBreakdown = breakdown.processBreakdown.find((b) => b.processDefinitionId === uProcessId)!;
    expect(uBreakdown.total).toBe(2);
    const aBreakdown = breakdown.processBreakdown.find((b) => b.processDefinitionId === aProcessId)!;
    expect(aBreakdown.rejected).toBe(1);

    // ---- Task Summary ----
    expect(summary.taskSummary.total).toBe(2);
    expect(summary.taskSummary.pending).toBe(1);
    expect(summary.taskSummary.completed).toBe(1);

    // ---- Approval Summary: reject leaves the assignment Rejected ----
    expect(approvalSummary.approvalSummary.total).toBe(1);
    expect(approvalSummary.approvalSummary.rejected).toBe(1);
    expect(approvalSummary.approvalSummary.approved).toBe(0);

    // ---- SLA Summary: no policy configured -> zero, compliance null ----
    expect(summary.slaSummary.completed).toBe(0);
    expect(summary.slaSummary.complianceRate).toBeNull();

    // ---- Date range filter: a future 'from' excludes everything ----
    const future = new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString();
    const futureSummary = await getSummary(applicant, { processDefinitionId: uProcessId, from: future });
    expect(futureSummary.process.total).toBe(0);

    // ---- Invalid date range: 400 via the existing error contract ----
    const past = new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString();
    const invalidRange = await applicant.api.get('/api/reports/summary', { headers: applicant.auth, params: { from: future, to: past } });
    expect(invalidRange.status).toBe(400);
    expect(invalidRange.data.code).toBe('REPORT_INVALID_DATE_RANGE');
    expect(invalidRange.data.message).not.toMatch(/Npgsql|PostgresException|System\./); // Part 46 — never a leaked SQL/stack trace.

    // ---- Status filter ----
    const runningOnly = await getSummary(applicant, { processDefinitionId: uProcessId, status: 'Running' });
    expect(runningOnly.process.total).toBe(1);
    expect(runningOnly.process.running).toBe(1);

    // ---- Department filter (real User.DepartmentId relationship) ----
    const deptFiltered = await getSummary(applicant, { departmentId: deptId });
    expect(deptFiltered.process.total).toBe(3); // all three instances were initiated by applicant, who is in deptId

    // ---- Details: pagination + sorting + cross-check against Summary ----
    const page1 = await getDetails(applicant, { page: 1, pageSize: 2, sortBy: 'StartedAt', sortDirection: 'Ascending' });
    expect(page1.totalCount).toBe(3);
    expect(page1.items.length).toBe(2);
    const page2 = await getDetails(applicant, { page: 2, pageSize: 2, sortBy: 'StartedAt', sortDirection: 'Ascending' });
    expect(page2.items.length).toBe(1);
    const allIds = [...page1.items, ...page2.items].map((i) => i.processInstanceId);
    expect(new Set(allIds).size).toBe(3);
    expect(allIds).toContain(runningInstance.data.id);
    expect(allIds).toContain(completedInstance.data.id);
    expect(allIds).toContain(rejectedInstance.data.id);

    // ---- Part 40: Reporting Detail must agree with Process Monitoring for the same instance ----
    const monitoring = await applicant.api.get('/api/process-monitoring', { headers: applicant.auth, params: { processDefinitionId: uProcessId } });
    expect(monitoring.status).toBe(200);
    const monitoringRunning = monitoring.data.items.find((i: { processInstanceId: string }) => i.processInstanceId === runningInstance.data.id);
    const detailRunning = (await getDetails(applicant, { processDefinitionId: uProcessId })).items.find((i: { processInstanceId: string; status?: string }) => i.processInstanceId === runningInstance.data.id) as unknown as { status: string };
    expect(detailRunning.status).toBe(monitoringRunning.status);

    // ---- Export CSV: row count matches Details' totalCount, contains expected data ----
    const exportRes = await applicant.api.get('/api/reports/export', { headers: applicant.auth, responseType: 'text' });
    expect(exportRes.status).toBe(200);
    const csvLines = (exportRes.data as string).trim().split('\n');
    expect(csvLines.length - 1).toBe(3); // header + 3 rows
    expect(exportRes.data).toContain(runningInstance.data.id);

    // ---- Export CSV formula-injection defense ----
    const maliciousSuffix = uniqueSuffix();
    const maliciousProcess = await admin.api.post('/api/process-definitions', { key: `rpt-inj-${maliciousSuffix}`, name: `=SUM(1+1)-${maliciousSuffix}` }, { headers: admin.auth });
    expect(maliciousProcess.status).toBe(201);
    await admin.api.post(`/api/process-definitions/${maliciousProcess.data.id}/versions`, { definition: userTaskDefinition }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${maliciousProcess.data.id}/publish`, null, { headers: admin.auth });
    await applicant.api.post('/api/process-instances', { processDefinitionKey: maliciousProcess.data.key }, { headers: applicant.auth });

    const injectionExport = await applicant.api.get('/api/reports/export', { headers: applicant.auth, params: { processDefinitionId: maliciousProcess.data.id }, responseType: 'text' });
    expect(injectionExport.status).toBe(200);
    expect(injectionExport.data as string).not.toMatch(/,=SUM/);
    expect(injectionExport.data as string).toContain(`'=SUM`);

    // ---- Part 39: Security — unrelated user cannot see applicant's data, cannot leak via filters/export ----
    const unrelatedSummary = await getSummary(unrelated, { processDefinitionId: uProcessId });
    expect(unrelatedSummary.process.total).toBe(0);
    expect(unrelatedSummary.processBreakdown.length).toBe(0);

    const unrelatedInjectedFilter = await getSummary(unrelated, { initiatorId: applicant.userId, processDefinitionId: uProcessId });
    expect(unrelatedInjectedFilter.process.total).toBe(0);

    const unrelatedExport = await unrelated.api.get('/api/reports/export', { headers: unrelated.auth, params: { processDefinitionId: uProcessId }, responseType: 'text' });
    expect(unrelatedExport.status).toBe(200);
    expect(unrelatedExport.data as string).not.toContain(runningInstance.data.id);

    // ---- Administrator: system-wide scope ----
    const adminSummary = await getSummary(admin, { processDefinitionId: uProcessId });
    expect(adminSummary.process.total).toBe(2);
  });
});
