import axios from 'axios';
import { describe, expect, it } from 'vitest';

// Phase 10 — Production Hardening & Cross-Module Correctness, run against the real rebuilt
// Docker stack: real PostgreSQL, real MinIO, real JWT auth. Covers the full enterprise workflow
// path end to end (Administration -> Organization -> User -> Process -> Form -> Publish -> Start
// with a BusinessKey -> duplicate-start rejection -> Task -> Approval -> SLA -> Notification ->
// Complete -> Monitoring -> Reporting -> Analytics -> Audit), a genuinely concurrent Role
// assignment scenario, and a live confirmation of Form Version pinning.
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

async function createAndLogin(admin: Awaited<ReturnType<typeof loginAsAdmin>>, username: string, displayName: string, password = 'Passw0rd!123') {
  const created = await admin.api.post(
    '/api/users',
    { username, displayName, email: `${username}@bpm-tests.local`, password, departmentId: null },
    { headers: admin.auth },
  );
  expect(created.status).toBe(201);

  const api = client();
  const login = await api.post('/api/auth/login', { username, password });
  expect(login.status).toBe(200);
  return { api, auth: { Authorization: `Bearer ${login.data.accessToken}` }, userId: created.data.id as string, username };
}

function uniqueSuffix() {
  return `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

const minimalFormSchema = { fields: [{ key: 'note', type: 'Text', label: 'Note' }] };

const userTaskWithFormDefinition = (formKey: string, approverRole: string) => ({
  nodes: [
    { id: 'start', type: 'Start', name: 'Start' },
    { id: 'applicant', type: 'UserTask', name: 'Applicant', assignment: { type: 'ProcessInitiator', value: '' }, form: { formDefinitionKey: formKey } },
    { id: 'approve', type: 'ApprovalTask', name: 'Approve', approval: { policy: 'AnyOne', assignments: [{ type: 'Role', value: approverRole }], returnPolicy: { enabled: true } } },
    { id: 'end', type: 'End', name: 'End' },
  ],
  transitions: [
    { id: 't1', source: 'start', target: 'applicant' },
    { id: 't2', source: 'applicant', target: 'approve' },
    { id: 't3', source: 'approve', target: 'end' },
  ],
});

describe('Phase 10 Production Hardening & Cross-Module Correctness against the live backend', () => {
  it('covers the full enterprise workflow: Administration -> Organization -> User -> Process -> Form -> Publish -> Start with BusinessKey -> duplicate rejected -> Task -> Approval -> SLA -> Notification -> Complete -> Monitoring -> Reporting -> Analytics -> Audit', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();

    // ---- Administration: Organization + User ----
    const org = await admin.api.post('/api/organizations', { name: `Org-${suffix}`, parentId: null }, { headers: admin.auth });
    expect(org.status).toBe(200);

    const approverRole = `Approver-${suffix}`;
    const approver = await createAndLogin(admin, `p10-approver-${suffix}`, 'Phase10 Approver');
    const roleCreated = await admin.api.post('/api/roles', { name: approverRole }, { headers: admin.auth });
    expect(roleCreated.status).toBe(200);
    const assigned = await admin.api.post('/api/roles/assign', { userId: approver.userId, roleId: roleCreated.data.id }, { headers: admin.auth });
    expect(assigned.status).toBe(204);

    const applicant = await createAndLogin(admin, `p10-applicant-${suffix}`, 'Phase10 Applicant');

    // ---- Form: create, publish ----
    const formKey = `p10-form-${suffix}`;
    const formCreated = await admin.api.post('/api/form-definitions', { key: formKey, name: `Phase10Form-${suffix}` }, { headers: admin.auth });
    expect(formCreated.status).toBe(201);
    await admin.api.post(`/api/form-definitions/${formCreated.data.id}/versions`, { schema: minimalFormSchema }, { headers: admin.auth });
    const formPublished = await admin.api.post(`/api/form-definitions/${formCreated.data.id}/publish`, null, { headers: admin.auth });
    expect(formPublished.status).toBe(200);

    // ---- Process: create, version referencing the form + approval role, publish ----
    const graph = userTaskWithFormDefinition(formKey, approverRole);
    const processCreated = await admin.api.post('/api/process-definitions', { key: `p10-process-${suffix}`, name: `Phase10Process-${suffix}` }, { headers: admin.auth });
    expect(processCreated.status).toBe(201);
    await admin.api.post(`/api/process-definitions/${processCreated.data.id}/versions`, { definition: graph }, { headers: admin.auth });
    const processPublished = await admin.api.post(`/api/process-definitions/${processCreated.data.id}/publish`, null, { headers: admin.auth });
    expect(processPublished.status).toBe(200);

    // ---- SLA policy on the approval node ----
    const slaPolicy = await admin.api.post(
      '/api/sla-policies',
      { processDefinitionId: processCreated.data.id, nodeId: 'approve', enabled: true, durationMinutes: 480, warningOffsetMinutes: 60 },
      { headers: admin.auth },
    );
    expect(slaPolicy.status).toBe(200);

    // ---- Start with a BusinessKey ----
    const businessKey = `PO-${suffix}`;
    const started = await applicant.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key, businessKey }, { headers: applicant.auth });
    expect(started.status).toBe(201);
    expect(started.data.businessKey).toBe(businessKey);

    // ---- Duplicate Start with the same BusinessKey must be rejected cleanly ----
    const duplicateStart = await applicant.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key, businessKey }, { headers: applicant.auth });
    expect(duplicateStart.status).toBe(409);
    expect(duplicateStart.data.code).toBe('PROCESS_INSTANCE_DUPLICATE_BUSINESS_KEY');
    expect(duplicateStart.data.message).toBeTruthy();
    expect(duplicateStart.data.traceId).toBeTruthy();

    // ---- Task: applicant fills out and submits the form ----
    const applicantTasks = await applicant.api.get('/api/tasks', { headers: applicant.auth });
    const applicantTask = applicantTasks.data.items.find((t: { processInstanceId: string; nodeId: string }) => t.processInstanceId === started.data.id && t.nodeId === 'applicant');
    expect(applicantTask).toBeTruthy();

    const formInstances = await applicant.api.get('/api/form-instances', { headers: applicant.auth, params: { processInstanceId: started.data.id } });
    expect(formInstances.status).toBe(200);
    const formInstance = formInstances.data.find((f: { taskInstanceId: string }) => f.taskInstanceId === applicantTask.id);
    expect(formInstance).toBeTruthy();
    expect(formInstance.formVersionId).toBe(formPublished.data.id);

    const submitted = await applicant.api.post(`/api/form-instances/${formInstance.id}/submit`, null, { headers: applicant.auth });
    expect(submitted.status).toBe(200);

    // ---- Approval + SLA ----
    const approverTasks = await approver.api.get('/api/tasks', { headers: approver.auth });
    const approvalTask = approverTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === started.data.id);
    expect(approvalTask).toBeTruthy();

    const approvalTaskDetail = await approver.api.get(`/api/tasks/${approvalTask.id}`, { headers: approver.auth });
    expect(approvalTaskDetail.data.sla).toBeTruthy();
    expect(approvalTaskDetail.data.sla.status).toBe('Active');

    const approved = await approver.api.post(`/api/tasks/${approvalTask.id}/approve`, null, { headers: approver.auth });
    expect(approved.status).toBe(200);

    // ---- Notification: applicant should have received something about their process ----
    const applicantNotifications = await applicant.api.get('/api/notifications', { headers: applicant.auth });
    expect(applicantNotifications.status).toBe(200);
    expect(applicantNotifications.data.items.length).toBeGreaterThan(0);

    // ---- Complete: process should now be Completed ----
    const instanceDetail = await admin.api.get(`/api/process-monitoring/${started.data.id}`, { headers: admin.auth });
    expect(instanceDetail.status).toBe(200);
    expect(instanceDetail.data.status).toBe('Completed');

    // ---- Monitoring ----
    const monitoring = await admin.api.get('/api/process-monitoring', { headers: admin.auth, params: { processDefinitionId: processCreated.data.id } });
    expect(monitoring.status).toBe(200);
    expect(monitoring.data.items.some((i: { processInstanceId: string }) => i.processInstanceId === started.data.id)).toBe(true);

    // ---- Reporting ----
    const reportSummary = await admin.api.get('/api/reports/summary', { headers: admin.auth, params: { processDefinitionId: processCreated.data.id } });
    expect(reportSummary.status).toBe(200);
    expect(reportSummary.data.process.total).toBe(1);
    expect(reportSummary.data.process.completed).toBe(1);

    // ---- Analytics ----
    const analytics = await admin.api.get('/api/analytics/overview', { headers: admin.auth, params: { processDefinitionId: processCreated.data.id } });
    expect(analytics.status).toBe(200);
    expect(analytics.data.totalProcesses).toBe(1);
    expect(analytics.data.completedProcesses).toBe(1);

    // ---- Audit: StartProcess for this exact instance ----
    const auditLogs = await admin.api.get('/api/audit-logs', { headers: admin.auth, params: { entityId: started.data.id, pageSize: 50 } });
    expect(auditLogs.status).toBe(200);
    expect(auditLogs.data.items.map((a: { action: string }) => a.action)).toContain('StartProcess');
  });

  // MUST HAVE #1 concurrency proof, over real HTTP: two genuinely simultaneous Start requests with
  // the same BusinessKey must result in exactly one ProcessInstance, and the loser must fail
  // cleanly (never a raw 500).
  it('rejects a concurrent duplicate Process Start over real HTTP, creating exactly one instance', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();

    const processCreated = await admin.api.post('/api/process-definitions', { key: `p10-concurrent-${suffix}`, name: `Concurrent-${suffix}` }, { headers: admin.auth });
    const simpleGraph = {
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
    await admin.api.post(`/api/process-definitions/${processCreated.data.id}/versions`, { definition: simpleGraph }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processCreated.data.id}/publish`, null, { headers: admin.auth });

    const businessKey = `CONC-${suffix}`;
    const [first, second] = await Promise.all([
      admin.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key, businessKey }, { headers: admin.auth }),
      admin.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key, businessKey }, { headers: admin.auth }),
    ]);

    const statuses = [first.status, second.status].sort();
    expect(statuses).toEqual([201, 409]);
    const conflicted = first.status === 409 ? first : second;
    expect(conflicted.data.code).toBe('PROCESS_INSTANCE_DUPLICATE_BUSINESS_KEY');

    const monitoring = await admin.api.get('/api/process-monitoring', { headers: admin.auth, params: { processDefinitionId: processCreated.data.id } });
    expect(monitoring.data.items.length).toBe(1);
  });

  // MUST HAVE #2 concurrency proof, over real HTTP: two genuinely simultaneous role-assignment
  // requests for the same (user, role) pair must never surface a raw 500.
  it('handles a concurrent duplicate Role assignment over real HTTP without a raw 500', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();

    const target = await createAndLogin(admin, `p10-role-target-${suffix}`, 'Role Target');
    const role = await admin.api.post('/api/roles', { name: `ConcurrentRole-${suffix}` }, { headers: admin.auth });
    expect(role.status).toBe(200);

    const [first, second] = await Promise.all([
      admin.api.post('/api/roles/assign', { userId: target.userId, roleId: role.data.id }, { headers: admin.auth }),
      admin.api.post('/api/roles/assign', { userId: target.userId, roleId: role.data.id }, { headers: admin.auth }),
    ]);

    expect(first.status).toBe(204);
    expect(second.status).toBe(204);

    const members = await admin.api.get(`/api/roles/${role.data.id}/members`, { headers: admin.auth });
    expect(members.data.filter((m: { id: string }) => m.id === target.userId).length).toBe(1);
  });

  // MUST HAVE #3 — Form Version pinning, confirmed live: a ProcessVersion published while Form V1
  // is current must keep resolving V1 for every task it ever creates, even after Form V2 is
  // published, and even when the same node is re-entered via Return.
  it('keeps a published ProcessVersion pinned to the FormVersion that was current at its own publish time, even after the form is republished', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();

    const approverRole = `PinReviewer-${suffix}`;
    const reviewer = await createAndLogin(admin, `p10-pin-reviewer-${suffix}`, 'Pin Reviewer');
    const roleCreated = await admin.api.post('/api/roles', { name: approverRole }, { headers: admin.auth });
    await admin.api.post('/api/roles/assign', { userId: reviewer.userId, roleId: roleCreated.data.id }, { headers: admin.auth });

    const applicant = await createAndLogin(admin, `p10-pin-applicant-${suffix}`, 'Pin Applicant');

    const formKey = `p10-pin-form-${suffix}`;
    const formCreated = await admin.api.post('/api/form-definitions', { key: formKey, name: `PinForm-${suffix}` }, { headers: admin.auth });
    await admin.api.post(`/api/form-definitions/${formCreated.data.id}/versions`, { schema: minimalFormSchema }, { headers: admin.auth });
    const formV1 = await admin.api.post(`/api/form-definitions/${formCreated.data.id}/publish`, null, { headers: admin.auth });
    expect(formV1.status).toBe(200);

    const graph = userTaskWithFormDefinition(formKey, approverRole);
    const processCreated = await admin.api.post('/api/process-definitions', { key: `p10-pin-process-${suffix}`, name: `PinProcess-${suffix}` }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processCreated.data.id}/versions`, { definition: graph }, { headers: admin.auth });
    const processPublished = await admin.api.post(`/api/process-definitions/${processCreated.data.id}/publish`, null, { headers: admin.auth });

    // Confirm the published ProcessVersion's own graph shows the pinned FormVersionId.
    const publishedApplicantNode = processPublished.data.definition.nodes.find((n: { id: string }) => n.id === 'applicant');
    expect(publishedApplicantNode.form.formVersionId).toBe(formV1.data.id);

    // Instance A starts while Form V1 is current.
    const instanceA = await applicant.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: applicant.auth });
    expect(instanceA.status).toBe(201);
    const formInstancesA = await applicant.api.get('/api/form-instances', { headers: applicant.auth, params: { processInstanceId: instanceA.data.id } });
    expect(formInstancesA.data[0].formVersionId).toBe(formV1.data.id);

    // Republish the form to V2.
    await admin.api.post(`/api/form-definitions/${formCreated.data.id}/versions`, { schema: minimalFormSchema }, { headers: admin.auth });
    const formV2 = await admin.api.post(`/api/form-definitions/${formCreated.data.id}/publish`, null, { headers: admin.auth });
    expect(formV2.data.id).not.toBe(formV1.data.id);

    // Instance B starts AFTER the republish, on the SAME already-published ProcessVersion — must
    // still resolve V1, deterministically.
    const instanceB = await applicant.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: applicant.auth });
    expect(instanceB.status).toBe(201);
    const formInstancesB = await applicant.api.get('/api/form-instances', { headers: applicant.auth, params: { processInstanceId: instanceB.data.id } });
    expect(formInstancesB.data[0].formVersionId).toBe(formV1.data.id);

    // Submit instance B's form, then Return the approval task back to the applicant — the
    // newly-created applicant task must still resolve V1.
    const submitB = await applicant.api.post(`/api/form-instances/${formInstancesB.data[0].id}/submit`, null, { headers: applicant.auth });
    expect(submitB.status).toBe(200);

    const reviewerTasks = await reviewer.api.get('/api/tasks', { headers: reviewer.auth });
    const approvalTaskB = reviewerTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === instanceB.data.id);
    expect(approvalTaskB).toBeTruthy();

    const returned = await reviewer.api.post(`/api/tasks/${approvalTaskB.id}/return`, null, { headers: reviewer.auth });
    expect(returned.status).toBe(200);

    const applicantTasksAfterReturn = await applicant.api.get('/api/tasks', { headers: applicant.auth });
    const newApplicantTask = applicantTasksAfterReturn.data.items.find(
      (t: { processInstanceId: string; nodeId: string; status: string }) => t.processInstanceId === instanceB.data.id && t.nodeId === 'applicant' && t.status !== 'Returned',
    );
    expect(newApplicantTask).toBeTruthy();

    const formInstancesAfterReturn = await applicant.api.get('/api/form-instances', { headers: applicant.auth, params: { processInstanceId: instanceB.data.id } });
    const returnedFormInstance = formInstancesAfterReturn.data.find((f: { taskInstanceId: string }) => f.taskInstanceId === newApplicantTask.id);
    expect(returnedFormInstance).toBeTruthy();
    expect(returnedFormInstance.formVersionId).toBe(formV1.data.id);
  });
});
