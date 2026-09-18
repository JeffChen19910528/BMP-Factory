import axios from 'axios';
import { describe, expect, it } from 'vitest';

// Phase 6.2 — Notification Delivery, run against the real rebuilt Docker stack. Exercises the
// full Notification -> NotificationDelivery -> EmailDeliveryWorker -> IEmailSender path over real
// HTTP, real PostgreSQL, and a real running background worker — the email "provider" is
// FakeEmailSender (this stack's Email:Provider=Fake, no real SMTP available — see EmailSettings'
// own comment), but everything else in the path is exactly what production would run. Delivery
// outcomes are observed via the Administrator-only diagnostic endpoint
// (GET /api/notifications/{id}/delivery), the one narrow exception Part S allows to "no delivery
// API" — there is no other way to verify delivery state over HTTP.
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

interface DeliveryStatusRow {
  deliveryId: string;
  channel: string;
  status: string;
  attemptCount: number;
  lastAttemptAt: string | null;
  sentAt: string | null;
  nextAttemptAt: string | null;
  lastError: string | null;
}

async function getEmailDelivery(admin: Awaited<ReturnType<typeof loginAsAdmin>>, notificationId: string): Promise<DeliveryStatusRow | undefined> {
  const response = await admin.api.get(`/api/notifications/${notificationId}/delivery`, { headers: admin.auth });
  expect(response.status).toBe(200);
  return (response.data as DeliveryStatusRow[]).find((d) => d.channel === 'Email');
}

async function waitForDeliveryStatus(
  admin: Awaited<ReturnType<typeof loginAsAdmin>>,
  notificationId: string,
  predicate: (delivery: DeliveryStatusRow) => boolean,
  timeoutMs = 30000,
): Promise<DeliveryStatusRow> {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    const delivery = await getEmailDelivery(admin, notificationId);
    if (delivery && predicate(delivery)) {
      return delivery;
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  throw new Error(`Timed out waiting for delivery status on notification ${notificationId}`);
}

async function findLatestNotification(api: ReturnType<typeof client>, auth: Record<string, string>, type: string) {
  const response = await api.get('/api/notifications', { params: { pageSize: 50 }, headers: auth });
  expect(response.status).toBe(200);
  const match = response.data.items.find((n: { type: string }) => n.type === type);
  expect(match).toBeTruthy();
  return match;
}

describe('Notification Delivery against the live backend', () => {
  it('covers Scenarios A-H: TaskAssigned/ApprovalRequired/ProcessCompleted email delivery, provider failure, retry, max-attempt exhaustion, cross-user security, and duplicate-action safety', async () => {
    const admin = await loginAsAdmin();
    const suffix = Date.now();

    const initiator = await createAndLogin(admin, `deliv-init-${suffix}`, 'Delivery Initiator');
    const approver = await createAndLogin(admin, `deliv-app-${suffix}`, 'Delivery Approver');
    const unrelated = await createAndLogin(admin, `deliv-unrel-${suffix}`, 'Delivery Unrelated');

    const approverRole = await admin.api.post('/api/roles', { name: `DeliveryApprover-${suffix}` }, { headers: admin.auth });
    expect(approverRole.status).toBe(200);
    await admin.api.post('/api/roles/assign', { userId: approver.userId, roleId: approverRole.data.id }, { headers: admin.auth });

    const processCreated = await admin.api.post('/api/process-definitions', { key: `deliv-proc-${suffix}`, name: 'Delivery Test Process' }, { headers: admin.auth });
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

    // ---- Scenario A: TaskAssigned Email ----
    const taskAssignedNotification = await findLatestNotification(initiator.api, initiator.auth, 'TaskAssigned');
    const taskAssignedDelivery = await waitForDeliveryStatus(admin, taskAssignedNotification.id, (d) => d.status === 'Sent');
    expect(taskAssignedDelivery.attemptCount).toBeGreaterThanOrEqual(1);
    expect(taskAssignedDelivery.sentAt).toBeTruthy();

    const applicantTasks = await initiator.api.get('/api/tasks', { headers: initiator.auth });
    const applicantTask = applicantTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceId);
    await initiator.api.post(`/api/tasks/${applicantTask.id}/complete`, null, { headers: initiator.auth });

    // ---- Scenario B: ApprovalRequired Email ----
    const approvalRequiredNotification = await findLatestNotification(approver.api, approver.auth, 'ApprovalRequired');
    const approvalRequiredDelivery = await waitForDeliveryStatus(admin, approvalRequiredNotification.id, (d) => d.status === 'Sent');
    expect(approvalRequiredDelivery.status).toBe('Sent');
    expect(approvalRequiredNotification.title.length).toBeGreaterThan(0);

    const approverTasks = await approver.api.get('/api/tasks', { headers: approver.auth });
    const approvalTask = approverTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceId);
    const approve = await approver.api.post(`/api/tasks/${approvalTask.id}/approve`, null, { headers: approver.auth });
    expect(approve.status).toBe(200);

    // ---- Scenario C: ProcessCompleted Email ----
    const processCompletedNotification = await findLatestNotification(initiator.api, initiator.auth, 'ProcessCompleted');
    const processCompletedDelivery = await waitForDeliveryStatus(admin, processCompletedNotification.id, (d) => d.status === 'Sent');
    expect(processCompletedDelivery.status).toBe('Sent');

    // ---- Scenario G: Cross-User Security (existing Notification API must remain intact) ----
    const unrelatedNotifications = await unrelated.api.get('/api/notifications', { headers: unrelated.auth });
    expect(unrelatedNotifications.data.items.length).toBe(0);
    const unrelatedManipulated = await unrelated.api.get('/api/notifications', { params: { userId: initiator.userId }, headers: unrelated.auth });
    expect(unrelatedManipulated.data.items.length).toBe(0);
    const idorMarkRead = await unrelated.api.post(`/api/notifications/${processCompletedNotification.id}/read`, null, { headers: unrelated.auth });
    expect([403, 404]).toContain(idorMarkRead.status);
    // The delivery diagnostic endpoint is Administrator-only — a normal user (even the owner)
    // cannot read it.
    const nonAdminDeliveryAccess = await initiator.api.get(`/api/notifications/${processCompletedNotification.id}/delivery`, { headers: initiator.auth });
    expect(nonAdminDeliveryAccess.status).toBe(403);

    // ---- Scenario H: Duplicate Action Safety ----
    const duplicateApprove = await approver.api.post(`/api/tasks/${approvalTask.id}/approve`, null, { headers: approver.auth });
    expect([400, 404, 409]).toContain(duplicateApprove.status);
    const allInitiatorNotifications = await initiator.api.get('/api/notifications', { params: { pageSize: 100 }, headers: initiator.auth });
    const processCompletedCount = allInitiatorNotifications.data.items.filter((n: { type: string }) => n.type === 'ProcessCompleted').length;
    expect(processCompletedCount).toBe(1);
  });

  it('covers Scenarios D-F: provider failure keeps the business operation successful, retry eventually succeeds, and max-attempt exhaustion ends Failed', async () => {
    const admin = await loginAsAdmin();
    const suffix = Date.now();

    // FakeEmailSender's trigger is keyed off the recipient's email local-part — Part G says the
    // recipient address always comes from User.Email (never the frontend), so the only way to
    // deterministically trigger a simulated failure is to control it via the *user's own* email
    // when creating them, exactly as a real recipient's mailbox being unreachable would look from
    // the delivery pipeline's perspective.
    const retrySucceedsUser = await admin.api.post(
      '/api/users',
      { username: `deliv-retry-${suffix}`, displayName: 'Retry Succeeds User', email: `faildelivery1-${suffix}@bpm-tests.local`, password: 'Passw0rd!123', departmentId: null },
      { headers: admin.auth },
    );
    expect(retrySucceedsUser.status).toBe(201);
    const retryLogin = await client().post('/api/auth/login', { username: `deliv-retry-${suffix}`, password: 'Passw0rd!123' });
    const retryUser = { api: client(), auth: { Authorization: `Bearer ${retryLogin.data.accessToken}` } };

    const maxAttemptsUser = await admin.api.post(
      '/api/users',
      { username: `deliv-maxfail-${suffix}`, displayName: 'Max Attempts User', email: `faildelivery99-${suffix}@bpm-tests.local`, password: 'Passw0rd!123', departmentId: null },
      { headers: admin.auth },
    );
    expect(maxAttemptsUser.status).toBe(201);

    // A simple single-UserTask process assigned directly to each of these users — the fastest way
    // to trigger exactly one TaskAssigned notification/delivery per user, without needing a full
    // approval chain for what is purely a delivery-outcome test.
    const processCreated = await admin.api.post('/api/process-definitions', { key: `deliv-fail-proc-${suffix}`, name: 'Delivery Failure Test' }, { headers: admin.auth });
    const graph = (assigneeUserId: string) => ({
      nodes: [
        { id: 'start', type: 'Start', name: 'Start' },
        { id: 'task', type: 'UserTask', name: 'Task', assignment: { type: 'User', value: assigneeUserId } },
        { id: 'end', type: 'End', name: 'End' },
      ],
      transitions: [
        { id: 't1', source: 'start', target: 'task' },
        { id: 't2', source: 'task', target: 'end' },
      ],
    });
    await admin.api.post(`/api/process-definitions/${processCreated.data.id}/versions`, { definition: graph(retrySucceedsUser.data.id) }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processCreated.data.id}/publish`, null, { headers: admin.auth });

    // ---- Scenario D + E: provider fails once, business operation still succeeds, retry succeeds ----
    const startedRetry = await admin.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: admin.auth });
    expect(startedRetry.status).toBe(201); // The business transaction succeeds regardless of email outcome.

    const retryNotification = await findLatestNotification(retryUser.api, retryUser.auth, 'TaskAssigned');
    // First attempt fails (simulated) — the delivery becomes Pending again with a scheduled retry,
    // and crucially the Notification/ProcessInstance/TaskInstance all already exist and are
    // unaffected (proven simply by having reached this point via the normal API).
    const afterFirstAttempt = await waitForDeliveryStatus(admin, retryNotification.id, (d) => d.attemptCount >= 1);
    expect(afterFirstAttempt.lastError).toBeTruthy();

    // Retry eventually succeeds (faildelivery1 fails exactly once).
    const afterRetry = await waitForDeliveryStatus(admin, retryNotification.id, (d) => d.status === 'Sent');
    expect(afterRetry.attemptCount).toBe(2);
    expect(afterRetry.sentAt).toBeTruthy();

    // ---- Scenario F: exhausts retries, ends Failed ----
    await admin.api.post(`/api/process-definitions/${processCreated.data.id}/versions`, { definition: graph(maxAttemptsUser.data.id) }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processCreated.data.id}/publish`, null, { headers: admin.auth });
    const startedMaxFail = await admin.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: admin.auth });
    expect(startedMaxFail.status).toBe(201);

    const maxFailLogin = await client().post('/api/auth/login', { username: `deliv-maxfail-${suffix}`, password: 'Passw0rd!123' });
    const maxFailUser = { api: client(), auth: { Authorization: `Bearer ${maxFailLogin.data.accessToken}` } };
    const maxFailNotification = await findLatestNotification(maxFailUser.api, maxFailUser.auth, 'TaskAssigned');

    const finalDelivery = await waitForDeliveryStatus(admin, maxFailNotification.id, (d) => d.status === 'Failed', 40000);
    expect(finalDelivery.status).toBe('Failed');
    expect(finalDelivery.nextAttemptAt).toBeNull();
    expect(finalDelivery.lastError).toBeTruthy();

    // Worker must not keep retrying a permanently Failed delivery — attemptCount stays put after
    // waiting through another full poll interval.
    const attemptCountAtFailure = finalDelivery.attemptCount;
    await new Promise((resolve) => setTimeout(resolve, 4000));
    const stillFailed = await getEmailDelivery(admin, maxFailNotification.id);
    expect(stillFailed?.status).toBe('Failed');
    expect(stillFailed?.attemptCount).toBe(attemptCountAtFailure);

    // The In-App notification itself remains fully intact and readable despite the permanent
    // email failure (Part Q).
    const stillReadable = await maxFailUser.api.get('/api/notifications', { headers: maxFailUser.auth });
    expect(stillReadable.data.items.some((n: { id: string }) => n.id === maxFailNotification.id)).toBe(true);
  });
});
