import axios from 'axios';
import { describe, expect, it } from 'vitest';

// Phase 8 — Process Governance & Lifecycle, run against the real rebuilt Docker stack with
// genuinely distinct logged-in users, real PostgreSQL, and the real Workflow/SLA engines.
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

const userTaskDefinition = (taskName = 'Review') => ({
  nodes: [
    { id: 'start', type: 'Start', name: 'Start' },
    { id: 'task', type: 'UserTask', name: taskName, assignment: { type: 'ProcessInitiator', value: '' } },
    { id: 'end', type: 'End', name: 'End' },
  ],
  transitions: [
    { id: 't1', source: 'start', target: 'task' },
    { id: 't2', source: 'task', target: 'end' },
  ],
});

const userTaskDefinitionV2 = {
  nodes: [
    { id: 'start', type: 'Start', name: 'Start' },
    { id: 'task', type: 'UserTask', name: 'Review', assignment: { type: 'ProcessInitiator', value: '' } },
    { id: 'review', type: 'UserTask', name: 'Second Review', assignment: { type: 'ProcessInitiator', value: '' } },
    { id: 'end', type: 'End', name: 'End' },
  ],
  transitions: [
    { id: 't1', source: 'start', target: 'task' },
    { id: 't3', source: 'task', target: 'review' },
    { id: 't4', source: 'review', target: 'end' },
  ],
};

describe('Process Governance & Lifecycle against the live backend', () => {
  it('covers the full Suspend/Archive/Restore lifecycle, Owner authorization, Version Compare, concurrency, IDOR, and audit', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();

    const owner = await createAndLogin(admin, `gov-owner-${suffix}`, 'Governance Owner');
    const unrelated = await createAndLogin(admin, `gov-unrelated-${suffix}`, 'Governance Unrelated');

    // ---- 1-6: Create, assign owner, draft, change reason, publish, verify Published ----
    const created = await admin.api.post('/api/process-definitions', { key: `gov-${suffix}`, name: `GovernanceProcess-${suffix}` }, { headers: admin.auth });
    expect(created.status).toBe(201);
    const processId = created.data.id as string;
    expect(created.data.status).toBe('Draft');
    expect(created.data.ownerUserId).toBeNull();

    const ownerAssigned = await admin.api.post(`/api/process-definitions/${processId}/owner`, { ownerUserId: owner.userId, expectedVersion: created.data.rowVersion }, { headers: admin.auth });
    expect(ownerAssigned.status).toBe(200);
    expect(ownerAssigned.data.ownerUserId).toBe(owner.userId);

    const versionCreated = await admin.api.post(`/api/process-definitions/${processId}/versions`, { definition: userTaskDefinition() }, { headers: admin.auth });
    expect(versionCreated.status).toBe(200);

    const published = await admin.api.post(`/api/process-definitions/${processId}/publish`, { changeReason: 'Initial rollout' }, { headers: admin.auth });
    expect(published.status).toBe(200);
    expect(published.data.status).toBe('Published');
    expect(published.data.changeReason).toBe('Initial rollout');
    const v1Id = published.data.id as string;

    const afterPublish = await admin.api.get(`/api/process-definitions/${processId}`, { headers: admin.auth });
    expect(afterPublish.data.status).toBe('Published');
    expect(afterPublish.data.currentVersionId).toBe(v1Id);

    // ---- 7-8: Start Instance A, verify it uses Version 1 ----
    const instanceA = await owner.api.post('/api/process-instances', { processDefinitionKey: created.data.key }, { headers: owner.auth });
    expect(instanceA.status).toBe(201);
    expect(instanceA.data.processVersionId).toBe(v1Id);

    // ---- 9-11: Suspend, verify new start fails, verify Instance A still operational ----
    const suspended = await owner.api.post(`/api/process-definitions/${processId}/suspend`, { expectedVersion: afterPublish.data.rowVersion }, { headers: owner.auth });
    expect(suspended.status).toBe(200);
    expect(suspended.data.status).toBe('Suspended');

    const startAfterSuspend = await owner.api.post('/api/process-instances', { processDefinitionKey: created.data.key }, { headers: owner.auth });
    expect(startAfterSuspend.status).toBe(409);
    expect(startAfterSuspend.data.code).toBe('PROCESS_DEFINITION_NOT_PUBLISHED');

    const monitoringDuringSuspend = await admin.api.get('/api/process-monitoring', { headers: admin.auth, params: { processDefinitionId: processId } });
    expect(monitoringDuringSuspend.status).toBe(200);
    expect(monitoringDuringSuspend.data.items.some((i: { processInstanceId: string }) => i.processInstanceId === instanceA.data.id)).toBe(true);

    // ---- 12-13: Task remains actionable, SLA continues (no SLA policy configured here, so we
    // verify the task is still completable rather than asserting on SLA fields specifically —
    // Part 31 explicitly says do not modify SlaProcessor; this proves suspension doesn't block
    // task completion, which is the load-bearing guarantee) ----
    const ownerTasks = await owner.api.get('/api/tasks', { headers: owner.auth });
    const taskA = ownerTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === instanceA.data.id);
    expect(taskA).toBeTruthy();

    // ---- 14-15: Complete Instance A, verify completed history ----
    const completeA = await owner.api.post(`/api/tasks/${taskA.id}/complete`, null, { headers: owner.auth });
    expect(completeA.status).toBe(200);

    const instanceADetail = await admin.api.get(`/api/process-monitoring/${instanceA.data.id}`, { headers: admin.auth });
    expect(instanceADetail.status).toBe(200);
    expect(instanceADetail.data.status).toBe('Completed');

    // ---- 16-17: Restore, verify new start succeeds ----
    const restored = await owner.api.post(`/api/process-definitions/${processId}/restore`, { expectedVersion: suspended.data.rowVersion }, { headers: owner.auth });
    expect(restored.status).toBe(200);
    expect(restored.data.status).toBe('Published');

    const instanceAfterRestore = await owner.api.post('/api/process-instances', { processDefinitionKey: created.data.key }, { headers: owner.auth });
    expect(instanceAfterRestore.status).toBe(201);
    expect(instanceAfterRestore.data.processVersionId).toBe(v1Id); // still Version 1 — restore never changes CurrentVersionId

    // ---- 18-20: Create Version 2, change workflow, publish with ChangeReason ----
    const version2Created = await admin.api.post(`/api/process-definitions/${processId}/versions`, { definition: userTaskDefinitionV2 }, { headers: admin.auth });
    expect(version2Created.status).toBe(200);
    const published2 = await admin.api.post(`/api/process-definitions/${processId}/publish`, { changeReason: 'Adds a second review step' }, { headers: admin.auth });
    expect(published2.status).toBe(200);
    expect(published2.data.changeReason).toBe('Adds a second review step');
    const v2Id = published2.data.id as string;
    expect(v2Id).not.toBe(v1Id);

    // ---- 21: Verify Version 1 remains immutable ----
    const versions = await admin.api.get(`/api/process-definitions/${processId}/versions`, { headers: admin.auth });
    const v1AfterV2 = versions.data.find((v: { id: string }) => v.id === v1Id);
    expect(v1AfterV2.status).toBe('Published');
    expect(v1AfterV2.definition.nodes).toHaveLength(3); // unchanged — still the original 3-node graph

    // ---- 22-23: Start Instance B, verify it uses Version 2 ----
    const instanceB = await owner.api.post('/api/process-instances', { processDefinitionKey: created.data.key }, { headers: owner.auth });
    expect(instanceB.status).toBe(201);
    expect(instanceB.data.processVersionId).toBe(v2Id);

    // ---- 24-25: Compare Version 1 vs Version 2, verify expected diff ----
    const diff = await admin.api.get(`/api/process-definitions/${processId}/versions/compare`, { headers: admin.auth, params: { fromVersionId: v1Id, toVersionId: v2Id } });
    expect(diff.status).toBe(200);
    expect(diff.data.addedNodes).toHaveLength(1);
    expect(diff.data.addedNodes[0].nodeId).toBe('review');
    expect(diff.data.removedTransitions.map((t: { transitionId: string }) => t.transitionId)).toContain('t2');
    expect(diff.data.addedTransitions).toHaveLength(2);

    // ---- 26-27: Archive, verify new start fails ----
    const beforeArchive = await admin.api.get(`/api/process-definitions/${processId}`, { headers: admin.auth });
    const archived = await owner.api.post(`/api/process-definitions/${processId}/archive`, { expectedVersion: beforeArchive.data.rowVersion }, { headers: owner.auth });
    expect(archived.status).toBe(200);
    expect(archived.data.status).toBe('Archived');

    const startAfterArchive = await owner.api.post('/api/process-instances', { processDefinitionKey: created.data.key }, { headers: owner.auth });
    expect(startAfterArchive.status).toBe(409);

    // ---- 28: Verify existing/historical instances remain visible ----
    const monitoringAfterArchive = await admin.api.get('/api/process-monitoring', { headers: admin.auth, params: { processDefinitionId: processId } });
    expect(monitoringAfterArchive.status).toBe(200);
    const monitoringIds = monitoringAfterArchive.data.items.map((i: { processInstanceId: string }) => i.processInstanceId);
    expect(monitoringIds).toContain(instanceA.data.id);
    expect(monitoringIds).toContain(instanceAfterRestore.data.id);
    expect(monitoringIds).toContain(instanceB.data.id);

    // ---- 29-30: Reporting and Analytics still work and still include this data ----
    const reportSummary = await admin.api.get('/api/reports/summary', { headers: admin.auth, params: { processDefinitionId: processId } });
    expect(reportSummary.status).toBe(200);
    expect(reportSummary.data.process.total).toBe(3);

    const analyticsOverview = await admin.api.get('/api/analytics/overview', { headers: admin.auth, params: { processDefinitionId: processId } });
    expect(analyticsOverview.status).toBe(200);
    expect(analyticsOverview.data.totalProcesses).toBe(3);

    // ---- 31-32: Restore again, verify new start succeeds ----
    const restoredAgain = await owner.api.post(`/api/process-definitions/${processId}/restore`, { expectedVersion: archived.data.rowVersion }, { headers: owner.auth });
    expect(restoredAgain.status).toBe(200);
    const instanceAfterSecondRestore = await owner.api.post('/api/process-instances', { processDefinitionKey: created.data.key }, { headers: owner.auth });
    expect(instanceAfterSecondRestore.status).toBe(201);

    // ---- 33-34: Owner authorization + unrelated-user IDOR ----
    const currentDefinition = await admin.api.get(`/api/process-definitions/${processId}`, { headers: admin.auth });
    const unrelatedSuspendAttempt = await unrelated.api.post(`/api/process-definitions/${processId}/suspend`, { expectedVersion: currentDefinition.data.rowVersion }, { headers: unrelated.auth });
    expect(unrelatedSuspendAttempt.status).toBe(403);
    expect(unrelatedSuspendAttempt.data.code).toBe('PROCESS_DEFINITION_NOT_AUTHORIZED');

    const ownerSuspendAttempt = await owner.api.post(`/api/process-definitions/${processId}/suspend`, { expectedVersion: currentDefinition.data.rowVersion }, { headers: owner.auth });
    expect(ownerSuspendAttempt.status).toBe(200); // the owner CAN suspend their own process without the Administrator role

    // Cross-definition version comparison IDOR
    const otherProcess = await admin.api.post('/api/process-definitions', { key: `gov-other-${suffix}`, name: `OtherProcess-${suffix}` }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${otherProcess.data.id}/versions`, { definition: userTaskDefinition() }, { headers: admin.auth });
    const otherPublished = await admin.api.post(`/api/process-definitions/${otherProcess.data.id}/publish`, null, { headers: admin.auth });
    const crossDefinitionCompare = await admin.api.get(`/api/process-definitions/${processId}/versions/compare`, { headers: admin.auth, params: { fromVersionId: v1Id, toVersionId: otherPublished.data.id } });
    expect(crossDefinitionCompare.status).toBe(404);

    // ---- 35: Stale RowVersion concurrency ----
    const staleAttempt = await admin.api.post(`/api/process-definitions/${processId}/restore`, { expectedVersion: currentDefinition.data.rowVersion }, { headers: admin.auth });
    expect(staleAttempt.status).toBe(409);
    expect(staleAttempt.data.code).toBe('PROCESS_DEFINITION_CONCURRENCY_CONFLICT');

    // ---- 36: Verify AuditLog events ----
    // SuspendProcess/ArchiveProcess/RestoreProcess/ChangeProcessOwner are all logged with
    // EntityType=ProcessDefinition, EntityId=processId; PublishProcess is logged against the
    // ProcessVersion it published instead (EntityId=v1Id/v2Id) — the same, pre-existing
    // per-entity-type audit addressing this codebase already used before Phase 8 (see
    // WorkflowEngine.PublishVersionAsync), so it's verified separately here rather than expected
    // in the ProcessDefinition-scoped query.
    const auditLogs = await admin.api.get('/api/audit-logs', { headers: admin.auth, params: { entityId: processId, pageSize: 100 } });
    expect(auditLogs.status).toBe(200);
    const actions = auditLogs.data.items.map((a: { action: string }) => a.action);
    expect(actions).toContain('SuspendProcess');
    expect(actions).toContain('ArchiveProcess');
    expect(actions).toContain('RestoreProcess');
    expect(actions).toContain('ChangeProcessOwner');

    const versionAuditLogs = await admin.api.get('/api/audit-logs', { headers: admin.auth, params: { entityId: v1Id, pageSize: 100 } });
    expect(versionAuditLogs.data.items.map((a: { action: string }) => a.action)).toContain('PublishProcess');
  });

  // Explicit acceptance criterion (Part 26/48): Suspend/Archive must never cancel or otherwise
  // disturb a task that was already Pending before the transition — proven with its own isolated
  // scenario rather than folded into the larger test above, so a failure here is unambiguous.
  it('running-instance continuity: a Pending task remains completable after both Suspend and Archive', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();
    const applicant = await createAndLogin(admin, `gov-cont-${suffix}`, 'Continuity Applicant');

    async function setupPublishedProcess(keySuffix: string) {
      const def = await admin.api.post('/api/process-definitions', { key: `gov-cont-${keySuffix}`, name: `Continuity-${keySuffix}` }, { headers: admin.auth });
      await admin.api.post(`/api/process-definitions/${def.data.id}/versions`, { definition: userTaskDefinition() }, { headers: admin.auth });
      await admin.api.post(`/api/process-definitions/${def.data.id}/publish`, null, { headers: admin.auth });
      const published = await admin.api.get(`/api/process-definitions/${def.data.id}`, { headers: admin.auth });
      return { processId: def.data.id as string, key: def.data.key as string, rowVersion: published.data.rowVersion as string };
    }

    // Suspend path
    const suspendCase = await setupPublishedProcess(`${suffix}-a`);
    const instance1 = await applicant.api.post('/api/process-instances', { processDefinitionKey: suspendCase.key }, { headers: applicant.auth });
    expect(instance1.status).toBe(201);
    await admin.api.post(`/api/process-definitions/${suspendCase.processId}/suspend`, { expectedVersion: suspendCase.rowVersion }, { headers: admin.auth });
    const tasks1 = await applicant.api.get('/api/tasks', { headers: applicant.auth });
    const task1 = tasks1.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === instance1.data.id);
    const complete1 = await applicant.api.post(`/api/tasks/${task1.id}/complete`, null, { headers: applicant.auth });
    expect(complete1.status).toBe(200);
    const detail1 = await admin.api.get(`/api/process-monitoring/${instance1.data.id}`, { headers: admin.auth });
    expect(detail1.data.status).toBe('Completed');

    // Archive path
    const archiveCase = await setupPublishedProcess(`${suffix}-b`);
    const instance2 = await applicant.api.post('/api/process-instances', { processDefinitionKey: archiveCase.key }, { headers: applicant.auth });
    expect(instance2.status).toBe(201);
    await admin.api.post(`/api/process-definitions/${archiveCase.processId}/archive`, { expectedVersion: archiveCase.rowVersion }, { headers: admin.auth });
    const tasks2 = await applicant.api.get('/api/tasks', { headers: applicant.auth });
    const task2 = tasks2.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === instance2.data.id);
    const complete2 = await applicant.api.post(`/api/tasks/${task2.id}/complete`, null, { headers: applicant.auth });
    expect(complete2.status).toBe(200);
    const detail2 = await admin.api.get(`/api/process-monitoring/${instance2.data.id}`, { headers: admin.auth });
    expect(detail2.data.status).toBe('Completed');
  });

  // Metadata edit gate (Part 12) and invalid-owner rejection (Part 46), verified live end to end.
  it('blocks metadata edits while Suspended/Archived, and rejects an invalid owner assignment', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();

    const def = await admin.api.post('/api/process-definitions', { key: `gov-meta-${suffix}`, name: `MetaGate-${suffix}` }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${def.data.id}/versions`, { definition: userTaskDefinition() }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${def.data.id}/publish`, null, { headers: admin.auth });
    const published = await admin.api.get(`/api/process-definitions/${def.data.id}`, { headers: admin.auth });

    const suspended = await admin.api.post(`/api/process-definitions/${def.data.id}/suspend`, { expectedVersion: published.data.rowVersion }, { headers: admin.auth });
    expect(suspended.status).toBe(200);

    const editAttempt = await admin.api.put(`/api/process-definitions/${def.data.id}`, { name: 'Renamed While Suspended', description: null, category: null }, { headers: admin.auth });
    expect(editAttempt.status).toBe(409);
    expect(editAttempt.data.code).toBe('PROCESS_DEFINITION_NOT_EDITABLE');

    const invalidOwner = await admin.api.post(`/api/process-definitions/${def.data.id}/owner`, { ownerUserId: '00000000-0000-0000-0000-000000000000', expectedVersion: suspended.data.rowVersion }, { headers: admin.auth });
    expect(invalidOwner.status).toBe(400);
    expect(invalidOwner.data.code).toBe('INVALID_OWNER');
  });
});
