import axios from 'axios';
import { describe, expect, it } from 'vitest';

// Phase 6.4 — SLA Scheduler / Warning / Overdue / Escalation, run against the real rebuilt Docker
// stack with a real, independently-ticking SlaSchedulerWorker (docker-compose.yml sets
// SlaScheduler__PollIntervalSeconds=5 for this dev/test stack specifically so this test doesn't
// wait through the production-scale 60s default — see docker-compose.yml's own comment). This test
// uses short, real SLA durations (2 minutes) and waits through real wall-clock time to observe the
// scheduler's own ticks — the same bounded, honest approach notification.delivery.live.test.ts
// already established for EmailDeliveryWorker's retry/backoff cycles in Phase 6.2 (waiting through
// a real, short poll interval, never "hours," and never Thread.Sleep/fake timing inside the
// backend itself — the backend's own clock is real; only this test's *patience* is bounded).
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

function sleep(ms: number) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

type Notification = { type: string; relatedEntityId: string | null };

async function notificationsFor(user: { api: ReturnType<typeof client>; auth: Record<string, string> }): Promise<Notification[]> {
  const res = await user.api.get('/api/notifications?pageSize=200', { headers: user.auth });
  expect(res.status).toBe(200);
  return res.data.items as Notification[];
}

function countOfType(notifications: Notification[], type: string, relatedEntityId: string): number {
  return notifications.filter((n) => n.type === type && n.relatedEntityId === relatedEntityId).length;
}

describe('SLA Scheduler against the live backend', () => {
  it(
    'covers Scenarios A-E, I-J: Warning, Overdue, Escalation, completed-before-due safety, email delivery, and cross-user security',
    { timeout: 180_000 },
    async () => {
      const admin = await loginAsAdmin();
      const suffix = uniqueSuffix();

      const assignee = await createAndLogin(admin, `sched-assignee-${suffix}`, 'Scheduler Assignee');
      const manager = await createAndLogin(admin, `sched-manager-${suffix}`, 'Scheduler Manager');
      const unrelated = await createAndLogin(admin, `sched-unrel-${suffix}`, 'Scheduler Unrelated');

      const processCreated = await admin.api.post('/api/process-definitions', { key: `sched-proc-${suffix}`, name: 'SLA Scheduler Test Process' }, { headers: admin.auth });
      expect(processCreated.status).toBe(201);
      const processId = processCreated.data.id as string;

      const definition = {
        nodes: [
          { id: 'start', type: 'Start', name: 'Start' },
          { id: 'task', type: 'UserTask', name: 'Reviewable Task', assignment: { type: 'User', value: assignee.userId } },
          { id: 'end', type: 'End', name: 'End' },
        ],
        transitions: [
          { id: 't1', source: 'start', target: 'task' },
          { id: 't2', source: 'task', target: 'end' },
        ],
      };

      // Duration=2min, WarningOffset=1min -> WarningAt = T+60s, DueAt = T+120s. Escalation delay=0
      // -> EscalationAt = OverdueAt (fires the same tick the task becomes Overdue).
      const slaPolicy = await admin.api.post('/api/sla-policies', { processDefinitionId: processId, nodeId: 'task', enabled: true, durationMinutes: 2, warningOffsetMinutes: 1 }, { headers: admin.auth });
      expect(slaPolicy.status).toBe(200);
      const escalationPolicy = await admin.api.post('/api/escalation-policies', { processDefinitionId: processId, nodeId: 'task', enabled: true, delayMinutes: 0, targetType: 'User', targetValue: manager.userId }, { headers: admin.auth });
      expect(escalationPolicy.status).toBe(200);

      await admin.api.post(`/api/process-definitions/${processId}/versions`, { definition }, { headers: admin.auth });
      await admin.api.post(`/api/process-definitions/${processId}/publish`, null, { headers: admin.auth });

      // ---- Scenario J (cross-user security), checked before anything else needs waiting ----
      const policyAccessDenied = await unrelated.api.get('/api/sla-policies', { headers: unrelated.auth });
      expect(policyAccessDenied.status).toBe(403);
      const escalationAccessDenied = await unrelated.api.get('/api/escalation-policies', { headers: unrelated.auth });
      expect(escalationAccessDenied.status).toBe(403);
      const escalationMutationDenied = await unrelated.api.post('/api/escalation-policies', { processDefinitionId: processId, nodeId: 'task', enabled: true, delayMinutes: 0, targetType: 'User', targetValue: unrelated.userId }, { headers: unrelated.auth });
      expect(escalationMutationDenied.status).toBe(403);

      // Instance 1: left running through Warning -> Overdue -> Escalation.
      const started = await assignee.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: assignee.auth });
      expect(started.status).toBe(201);
      const processInstanceId = started.data.id as string;
      const tasks = await assignee.api.get('/api/tasks', { headers: assignee.auth });
      const task = tasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceId);
      expect(task).toBeTruthy();
      const taskId = task.id as string;

      // ---- Scenario J continued: an unrelated user cannot view this task or its SLA ----
      const taskAccessDenied = await unrelated.api.get(`/api/tasks/${taskId}`, { headers: unrelated.auth });
      expect(taskAccessDenied.status).toBe(403);

      // Instance 2: completed well before its own Warning/Due — proves a completed SLA is
      // permanently safe from the scheduler regardless of how much wall-clock time passes
      // afterward (Scenario C / Part Z).
      const startedCompleted = await assignee.api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: assignee.auth });
      expect(startedCompleted.status).toBe(201);
      const completedInstanceId = startedCompleted.data.id as string;
      const completedTasks = await assignee.api.get('/api/tasks', { headers: assignee.auth });
      const completedTask = completedTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === completedInstanceId);
      const completedTaskId = completedTask.id as string;
      const completeNow = await assignee.api.post(`/api/tasks/${completedTaskId}/complete`, null, { headers: assignee.auth });
      expect(completeNow.status).toBe(200);

      // ---- Scenario A: Warning (wait until safely past WarningAt=T+60s, still before DueAt) ----
      await sleep(75_000);

      const detailAtWarning = await assignee.api.get(`/api/tasks/${taskId}`, { headers: assignee.auth });
      expect(detailAtWarning.data.sla.status).toBe('Active');

      const assigneeNotificationsAtWarning = await notificationsFor(assignee);
      expect(countOfType(assigneeNotificationsAtWarning, 'SlaWarning', taskId)).toBe(1);
      expect(countOfType(assigneeNotificationsAtWarning, 'SlaOverdue', taskId)).toBe(0);

      // ---- Scenario D: Email Delivery for the SlaWarning notification ----
      const fullList = await assignee.api.get('/api/notifications?pageSize=200', { headers: assignee.auth });
      const warningFull = fullList.data.items.find((n: { type: string; relatedEntityId: string | null }) => n.type === 'SlaWarning' && n.relatedEntityId === taskId);
      expect(warningFull).toBeTruthy();
      const deliveryStatus = await admin.api.get(`/api/notifications/${warningFull.id}/delivery`, { headers: admin.auth });
      expect(deliveryStatus.status).toBe(200);
      expect(deliveryStatus.data.some((d: { status: string }) => d.status === 'Sent')).toBe(true);

      // ---- Scenario B: Overdue + Scenario E: Escalation (wait until safely past DueAt=T+120s) ----
      await sleep(60_000);

      const detailAtOverdue = await assignee.api.get(`/api/tasks/${taskId}`, { headers: assignee.auth });
      expect(detailAtOverdue.data.sla.status).toBe('Overdue');

      const assigneeNotificationsAtOverdue = await notificationsFor(assignee);
      expect(countOfType(assigneeNotificationsAtOverdue, 'SlaOverdue', taskId)).toBe(1);
      // Warning must still be exactly 1 — never re-fired after Overdue.
      expect(countOfType(assigneeNotificationsAtOverdue, 'SlaWarning', taskId)).toBe(1);

      const managerNotifications = await notificationsFor(manager);
      expect(countOfType(managerNotifications, 'SlaEscalated', taskId)).toBe(1);

      // ---- Scenario C verified after real wall-clock time has passed well beyond its own DueAt ----
      const completedSlaAfterWait = await assignee.api.get(`/api/tasks/${completedTaskId}`, { headers: assignee.auth });
      expect(completedSlaAfterWait.data.sla.status).toBe('Completed');
      const assigneeOverdueForCompletedTask = countOfType(assigneeNotificationsAtOverdue, 'SlaOverdue', completedTaskId);
      expect(assigneeOverdueForCompletedTask).toBe(0);
    },
  );
});
