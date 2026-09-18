import axios from 'axios';
import { describe, expect, it } from 'vitest';

// Phase 6.1 — Notification Foundation, run against the real rebuilt Docker stack with genuinely
// distinct logged-in users. Covers Scenarios A-H: Task Assignment, Approval Required, Approval
// Action, Process Completion, Mark Read, Mark All Read, Cross-User Security, and Duplicate Action
// Safety — all over real HTTP against the real engine, never mocked.
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

describe('Notification Foundation against the live backend', () => {
  it('covers Scenarios A-H: TaskAssigned, ApprovalRequired, approval action, ProcessCompleted, mark read/all-read, cross-user security, duplicate-action safety', async () => {
    const admin = await loginAsAdmin();
    const suffix = Date.now();

    const initiator = await createAndLogin(admin, `notif-init-${suffix}`, 'Notif Initiator');
    const approverA = await createAndLogin(admin, `notif-appA-${suffix}`, 'Notif Approver A');
    const approverB = await createAndLogin(admin, `notif-appB-${suffix}`, 'Notif Approver B');
    const unrelated = await createAndLogin(admin, `notif-unrel-${suffix}`, 'Notif Unrelated');

    const approverRole = await admin.api.post('/api/roles', { name: `NotifApprover-${suffix}` }, { headers: admin.auth });
    expect(approverRole.status).toBe(200);
    await admin.api.post('/api/roles/assign', { userId: approverA.userId, roleId: approverRole.data.id }, { headers: admin.auth });
    await admin.api.post('/api/roles/assign', { userId: approverB.userId, roleId: approverRole.data.id }, { headers: admin.auth });

    // Applicant (ProcessInitiator) -> ApprovalTask (AnyOne, role) -> End.
    const processCreated = await admin.api.post('/api/process-definitions', { key: `notif-proc-${suffix}`, name: 'Notification Test Process' }, { headers: admin.auth });
    expect(processCreated.status).toBe(201);
    const definition = {
      nodes: [
        { id: 'start', type: 'Start', name: 'Start' },
        { id: 'applicant', type: 'UserTask', name: 'Applicant', assignment: { type: 'ProcessInitiator', value: '' } },
        { id: 'approval', type: 'ApprovalTask', name: 'Approval', approval: { policy: 'AnyOne', assignments: [{ type: 'Role', value: approverRole.data.name }] } },
        { id: 'end', type: 'End', name: 'End' },
      ],
      transitions: [
        { id: 't1', source: 'start', target: 'applicant' },
        { id: 't2', source: 'applicant', target: 'approval' },
        { id: 't3', source: 'approval', target: 'end' },
      ],
    };
    await admin.api.post(`/api/process-definitions/${processCreated.data.id}/versions`, { definition }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processCreated.data.id}/publish`, null, { headers: admin.auth });

    const started = await initiator.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: initiator.auth });
    expect(started.status).toBe(201);
    const processInstanceId = started.data.id as string;

    // ---- Scenario A: Task Assignment Notification ----
    const initiatorTasks = await initiator.api.get('/api/tasks', { headers: initiator.auth });
    const applicantTask = initiatorTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceId);
    expect(applicantTask).toBeTruthy();

    const initiatorUnreadBefore = await initiator.api.get('/api/notifications/unread-count', { headers: initiator.auth });
    expect(initiatorUnreadBefore.status).toBe(200);
    expect(initiatorUnreadBefore.data).toBeGreaterThan(0);

    const initiatorNotifications = await initiator.api.get('/api/notifications', { headers: initiator.auth });
    expect(initiatorNotifications.data.items.some((n: { type: string }) => n.type === 'TaskAssigned')).toBe(true);

    // Complete the Applicant step to advance to the Approval node.
    const completeApplicant = await initiator.api.post(`/api/tasks/${applicantTask.id}/complete`, null, { headers: initiator.auth });
    expect(completeApplicant.status).toBe(200);

    // ---- Scenario B: Approval Required ----
    const approverATasks = await approverA.api.get('/api/tasks', { headers: approverA.auth });
    const approvalTask = approverATasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceId);
    expect(approvalTask).toBeTruthy();

    const approverANotifications = await approverA.api.get('/api/notifications', { headers: approverA.auth });
    const approvalRequired = approverANotifications.data.items.find((n: { type: string }) => n.type === 'ApprovalRequired');
    expect(approvalRequired).toBeTruthy();
    expect(typeof approvalRequired.title).toBe('string');
    expect(approvalRequired.title.length).toBeGreaterThan(0);

    const approverAUnread = await approverA.api.get('/api/notifications/unread-count', { headers: approverA.auth });
    expect(approverAUnread.data).toBeGreaterThan(0);

    // AnyOne: approverB should also have been notified.
    const approverBNotifications = await approverB.api.get('/api/notifications', { headers: approverB.auth });
    expect(approverBNotifications.data.items.some((n: { type: string }) => n.type === 'ApprovalRequired')).toBe(true);

    // ---- Scenario C: Approval Action ----
    const approve = await approverA.api.post(`/api/tasks/${approvalTask.id}/approve`, null, { headers: approverA.auth });
    expect(approve.status).toBe(200);

    // ---- Scenario D: Process Completion ----
    const initiatorNotificationsAfter = await initiator.api.get('/api/notifications', { headers: initiator.auth });
    expect(initiatorNotificationsAfter.data.items.some((n: { type: string }) => n.type === 'ApprovalCompleted')).toBe(true);
    expect(initiatorNotificationsAfter.data.items.some((n: { type: string }) => n.type === 'ProcessCompleted')).toBe(true);

    const finalInstance = await admin.api.get(`/api/process-instances/${processInstanceId}`, { headers: admin.auth });
    expect(finalInstance.data.status).toBe('Completed');

    // ---- Scenario E: Mark Read (idempotent) ----
    const oneNotification = initiatorNotificationsAfter.data.items[0];
    const markRead1 = await initiator.api.post(`/api/notifications/${oneNotification.id}/read`, null, { headers: initiator.auth });
    expect(markRead1.status).toBe(204);

    const afterFirstRead = await initiator.api.get('/api/notifications/unread-count', { headers: initiator.auth });
    const countAfterFirstRead = afterFirstRead.data;

    // Repeat mark-read — must remain a successful no-op, not fail, not decrement further.
    const markRead2 = await initiator.api.post(`/api/notifications/${oneNotification.id}/read`, null, { headers: initiator.auth });
    expect(markRead2.status).toBe(204);
    const afterSecondRead = await initiator.api.get('/api/notifications/unread-count', { headers: initiator.auth });
    expect(afterSecondRead.data).toBe(countAfterFirstRead);

    // ---- Scenario F: Mark All Read ----
    const unreadBeforeAll = await initiator.api.get('/api/notifications/unread-count', { headers: initiator.auth });
    expect(unreadBeforeAll.data).toBeGreaterThan(0);

    const markAllRead = await initiator.api.post('/api/notifications/read-all', null, { headers: initiator.auth });
    expect(markAllRead.status).toBe(204);

    const unreadAfterAll = await initiator.api.get('/api/notifications/unread-count', { headers: initiator.auth });
    expect(unreadAfterAll.data).toBe(0);

    // ---- Scenario G: Cross-User Security ----
    const unrelatedNotifications = await unrelated.api.get('/api/notifications', { headers: unrelated.auth });
    expect(unrelatedNotifications.status).toBe(200);
    expect(unrelatedNotifications.data.items.length).toBe(0);

    // Query-parameter manipulation must not bypass caller-scoping.
    const unrelatedManipulated = await unrelated.api.get('/api/notifications', { params: { userId: initiator.userId }, headers: unrelated.auth });
    expect(unrelatedManipulated.data.items.length).toBe(0);

    // Route-ID manipulation: unrelated user cannot mark the initiator's notification as read.
    const idorMarkRead = await unrelated.api.post(`/api/notifications/${oneNotification.id}/read`, null, { headers: unrelated.auth });
    expect([403, 404]).toContain(idorMarkRead.status);

    // ---- Scenario H: Duplicate Action Safety ----
    // Re-approving an already-resolved task fails closed (existing idempotency-by-state-check) —
    // no duplicate ApprovalCompleted/ProcessCompleted notification is produced.
    const duplicateApprove = await approverA.api.post(`/api/tasks/${approvalTask.id}/approve`, null, { headers: approverA.auth });
    expect([400, 404, 409]).toContain(duplicateApprove.status);

    const initiatorAllNotifications = await initiator.api.get('/api/notifications', { headers: initiator.auth, params: { pageSize: 100 } });
    const approvalCompletedCount = initiatorAllNotifications.data.items.filter((n: { type: string }) => n.type === 'ApprovalCompleted').length;
    expect(approvalCompletedCount).toBe(1);
  });
});
