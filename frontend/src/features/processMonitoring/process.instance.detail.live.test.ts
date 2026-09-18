import axios from 'axios';
import { describe, expect, it } from 'vitest';

// Phase 7.2.2 — Process Instance Detail + Timeline, run against the real rebuilt Docker stack with
// genuinely distinct logged-in users, covering this phase's own Scenarios A-M over real HTTP, real
// PostgreSQL, and the real Workflow/Approval/SLA engines.
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

async function getDetail(user: { api: ReturnType<typeof client>; auth: Record<string, string> }, id: string) {
  return user.api.get(`/api/process-monitoring/${id}`, { headers: user.auth });
}

describe('Process Instance Detail against the live backend', () => {
  it('covers Scenarios A-F, K: summary, current task, task history, approval, SLA, timeline, and historical ProcessVersion', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();

    const applicant = await createAndLogin(admin, `detail-applicant-${suffix}`, 'Detail Applicant');
    const approver = await createAndLogin(admin, `detail-approver-${suffix}`, 'Detail Approver');

    const approverRole = await admin.api.post('/api/roles', { name: `DetailApprover-${suffix}` }, { headers: admin.auth });
    expect(approverRole.status).toBe(200);
    await admin.api.post('/api/roles/assign', { userId: approver.userId, roleId: approverRole.data.id }, { headers: admin.auth });

    const processCreated = await admin.api.post('/api/process-definitions', { key: `detail-proc-${suffix}`, name: 'Detail Test Process' }, { headers: admin.auth });
    expect(processCreated.status).toBe(201);
    const processId = processCreated.data.id as string;

    const definitionV1 = {
      nodes: [
        { id: 'start', type: 'Start', name: 'Start' },
        { id: 'applicant', type: 'UserTask', name: 'Applicant Step', assignment: { type: 'ProcessInitiator', value: '' } },
        { id: 'approval', type: 'ApprovalTask', name: 'Approval Step', approval: { policy: 'AnyOne', assignments: [{ type: 'Role', value: approverRole.data.name }] } },
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

    await admin.api.post(`/api/process-definitions/${processId}/versions`, { definition: definitionV1 }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processId}/publish`, null, { headers: admin.auth });

    const started = await applicant.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: applicant.auth });
    expect(started.status).toBe(201);
    const processInstanceId = started.data.id as string;

    // ---- Scenario A: basic detail ----
    const initial = await getDetail(applicant, processInstanceId);
    expect(initial.status).toBe(200);
    expect(initial.data.processInstanceId).toBe(processInstanceId);
    expect(initial.data.processDefinitionName).toBe('Detail Test Process');
    expect(initial.data.status).toBe('Running');
    expect(initial.data.initiatorId).toBe(applicant.userId);
    expect(initial.data.startedAt).toBeTruthy();

    // ---- Scenario B: current task ----
    expect(initial.data.activeTaskCount).toBe(1);
    expect(initial.data.currentTask.nodeName).toBe('Applicant Step');

    // ---- Scenario E: SLA ----
    expect(initial.data.slaSummary.status).toBe('Active');
    expect(initial.data.slaSummary.dueAt).toBeTruthy();

    // ---- Scenario K: publish a second version, verify detail still uses the original ----
    const definitionV2 = {
      nodes: [
        { id: 'start', type: 'Start', name: 'Start' },
        { id: 'onlyStep', type: 'UserTask', name: 'Only Step (V2)', assignment: { type: 'ProcessInitiator', value: '' } },
        { id: 'end', type: 'End', name: 'End' },
      ],
      transitions: [
        { id: 't1', source: 'start', target: 'onlyStep' },
        { id: 't2', source: 'onlyStep', target: 'end' },
      ],
    };
    await admin.api.post(`/api/process-definitions/${processId}/versions`, { definition: definitionV2 }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processId}/publish`, null, { headers: admin.auth });

    const afterNewVersion = await getDetail(applicant, processInstanceId);
    expect(afterNewVersion.data.currentTask.nodeName).toBe('Applicant Step'); // still V1's node, not V2's.
    expect(afterNewVersion.data.workflowProgress.some((p: { nodeId: string }) => p.nodeId === 'onlyStep')).toBe(false);

    // ---- Scenario C: task history / Scenario F: timeline / Scenario D: approval ----
    const applicantTasks = await applicant.api.get('/api/tasks', { headers: applicant.auth });
    const applicantTask = applicantTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceId);
    await applicant.api.post(`/api/tasks/${applicantTask.id}/complete`, null, { headers: applicant.auth });

    const approverTasks = await approver.api.get('/api/tasks', { headers: approver.auth });
    const approvalTask = approverTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceId);
    await approver.api.post(`/api/tasks/${approvalTask.id}/approve`, null, { headers: approver.auth });

    const afterApproval = await getDetail(admin, processInstanceId);
    expect(afterApproval.data.status).toBe('Completed');
    expect(afterApproval.data.completedAt).toBeTruthy();
    expect(afterApproval.data.activeTaskCount).toBe(0);
    expect(afterApproval.data.currentTask).toBeNull();

    // Task History (Scenario C).
    expect(afterApproval.data.tasks).toHaveLength(2);
    const applicantHistory = afterApproval.data.tasks.find((t: { nodeId: string }) => t.nodeId === 'applicant');
    const approvalHistory = afterApproval.data.tasks.find((t: { nodeId: string }) => t.nodeId === 'approval');
    expect(applicantHistory.status).toBe('Completed');
    expect(approvalHistory.status).toBe('Completed');

    // Approval data rides on the existing TaskDto.approval (Scenario D).
    expect(approvalHistory.approval.policy).toBe('AnyOne');
    expect(approvalHistory.approval.assignments.some((a: { userId: string }) => a.userId === approver.userId)).toBe(true);

    // Timeline (Scenario F).
    const eventTypes = afterApproval.data.timeline.map((t: { eventType: string }) => t.eventType);
    expect(eventTypes).toContain('StartProcess');
    expect(eventTypes).toContain('TaskCompleted');
    expect(eventTypes).toContain('ApprovalApproved');
    expect(eventTypes).toContain('ApprovalCompleted');
    expect(eventTypes).toContain('ProcessCompleted');
    expect(eventTypes).not.toContain('WorkflowTransition');

    // Newest first, deterministic.
    const timestamps = afterApproval.data.timeline.map((t: { timestamp: string }) => new Date(t.timestamp).getTime());
    for (let i = 1; i < timestamps.length; i++) {
      expect(timestamps[i - 1]).toBeGreaterThanOrEqual(timestamps[i]);
    }
  });

  it('covers Scenarios H, I, J: cross-user IDOR, Administrator access, and rejected-process workflow progress', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();

    const approver = await createAndLogin(admin, `detail-rej-approver-${suffix}`, 'Reject Approver');
    const initiator = await createAndLogin(admin, `detail-rej-initiator-${suffix}`, 'Reject Initiator');
    const stranger = await createAndLogin(admin, `detail-stranger-${suffix}`, 'Detail Stranger');

    const role = await admin.api.post('/api/roles', { name: `DetailReject-${suffix}` }, { headers: admin.auth });
    await admin.api.post('/api/roles/assign', { userId: approver.userId, roleId: role.data.id }, { headers: admin.auth });

    const processCreated = await admin.api.post('/api/process-definitions', { key: `detail-rej-${suffix}`, name: 'Detail Reject Process' }, { headers: admin.auth });
    const processId = processCreated.data.id as string;
    const definition = {
      nodes: [
        { id: 'start', type: 'Start', name: 'Start' },
        { id: 'approval', type: 'ApprovalTask', name: 'Approval', approval: { policy: 'AnyOne', assignments: [{ type: 'Role', value: role.data.name }] } },
        { id: 'end', type: 'End', name: 'End' },
      ],
      transitions: [
        { id: 't1', source: 'start', target: 'approval' },
        { id: 't2', source: 'approval', target: 'end' },
      ],
    };
    await admin.api.post(`/api/process-definitions/${processId}/versions`, { definition }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processId}/publish`, null, { headers: admin.auth });

    const started = await initiator.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: initiator.auth });
    const processInstanceId = started.data.id as string;

    // ---- Scenario I: cross-user IDOR ----
    const idor = await getDetail(stranger, processInstanceId);
    expect(idor.status).toBe(403);

    // ---- Scenario I continued: unauthenticated ----
    const unauthenticated = await client().get(`/api/process-monitoring/${processInstanceId}`);
    expect(unauthenticated.status).toBe(401);

    // ---- Scenario I continued: arbitrary guessed ID ----
    const guessed = await getDetail(stranger, '00000000-0000-0000-0000-000000000000');
    expect(guessed.status).toBe(404);

    // ---- Scenario J: Administrator access ----
    const adminView = await getDetail(admin, processInstanceId);
    expect(adminView.status).toBe(200);
    expect(adminView.data.processInstanceId).toBe(processInstanceId);

    // Reject the approval.
    const approverTasks = await approver.api.get('/api/tasks', { headers: approver.auth });
    const approvalTask = approverTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceId);
    await approver.api.post(`/api/tasks/${approvalTask.id}/reject`, null, { headers: approver.auth });

    // ---- Scenario H (rejected process workflow progress): End never falsely marked Completed ----
    const rejectedDetail = await getDetail(initiator, processInstanceId);
    expect(rejectedDetail.data.status).toBe('Rejected');
    const progress = Object.fromEntries(rejectedDetail.data.workflowProgress.map((p: { nodeId: string; state: string }) => [p.nodeId, p.state]));
    expect(progress.approval).toBe('Completed');
    expect(progress.end).toBe('Pending');

    // The rejected task's own history entry is Rejected, not silently Completed.
    const approvalHistory = rejectedDetail.data.tasks.find((t: { nodeId: string }) => t.nodeId === 'approval');
    expect(approvalHistory.status).toBe('Rejected');

    // Cross-user still rejected after the process resolved.
    const idorAfter = await getDetail(stranger, processInstanceId);
    expect(idorAfter.status).toBe(403);
  });
});
