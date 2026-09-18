import axios from 'axios';
import { describe, expect, it } from 'vitest';

// Phase 9 — System Administration & Configuration, run against the real rebuilt Docker stack:
// real PostgreSQL, real JWT auth, real distinct logged-in users. Covers SLA Policy administration,
// Organization CRUD + concurrency + audit, Administrator password reset, self-service change
// password, Role rename + Administrator-protection + concurrency, Operational Health, and a final
// check that Phase 6/7/8 features (SLA calculation, Monitoring/Reporting/Analytics, Governance
// lifecycle) remain unaffected by this phase's additions.
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

const userTaskDefinition = {
  nodes: [
    { id: 'start', type: 'Start', name: 'Start' },
    { id: 'approve', type: 'UserTask', name: 'Approve', assignment: { type: 'ProcessInitiator', value: '' } },
    { id: 'end', type: 'End', name: 'End' },
  ],
  transitions: [
    { id: 't1', source: 'start', target: 'approve' },
    { id: 't2', source: 'approve', target: 'end' },
  ],
};

describe('Phase 9 System Administration & Configuration against the live backend', () => {
  it('covers Organization CRUD+concurrency+audit, SLA Policy admin, password reset/self-service change, Role rename/Administrator-protection, Operational Health, and existing-phase regression', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();

    // ---- 1: Login as Administrator (already done above) ----

    // ---- 2-4: Create Organization, verify update, stale update -> 409 ----
    const org = await admin.api.post('/api/organizations', { name: `Org-${suffix}`, parentId: null }, { headers: admin.auth });
    expect(org.status).toBe(200);
    expect(org.data.rowVersion).toBeTruthy();

    const orgUpdated = await admin.api.put(
      `/api/organizations/${org.data.id}`,
      { name: `Org-${suffix}-Renamed`, parentId: null, expectedVersion: org.data.rowVersion },
      { headers: admin.auth },
    );
    expect(orgUpdated.status).toBe(200);
    expect(orgUpdated.data.name).toBe(`Org-${suffix}-Renamed`);
    expect(orgUpdated.data.rowVersion).not.toBe(org.data.rowVersion);

    const orgStaleUpdate = await admin.api.put(
      `/api/organizations/${org.data.id}`,
      { name: `Org-${suffix}-Stale`, parentId: null, expectedVersion: org.data.rowVersion },
      { headers: admin.auth },
    );
    expect(orgStaleUpdate.status).toBe(409);
    expect(orgStaleUpdate.data.code).toBe('ORGANIZATION_CONCURRENCY_CONFLICT');

    // ---- 5: Verify Organization audit (Create + Update) ----
    const orgAudit = await admin.api.get('/api/audit-logs', { headers: admin.auth, params: { entityId: org.data.id, pageSize: 50 } });
    expect(orgAudit.status).toBe(200);
    const orgActions = orgAudit.data.items.map((a: { action: string }) => a.action);
    expect(orgActions).toContain('CreateOrganization');
    expect(orgActions).toContain('ModifyOrganization');

    // ---- 6-11: Create SLA Policy, verify appears, edit, validation, verify SLA audit ----
    const processDef = await admin.api.post('/api/process-definitions', { key: `sla-admin-${suffix}`, name: `SlaAdmin-${suffix}` }, { headers: admin.auth });
    expect(processDef.status).toBe(201);
    await admin.api.post(`/api/process-definitions/${processDef.data.id}/versions`, { definition: userTaskDefinition }, { headers: admin.auth });
    await admin.api.post(`/api/process-definitions/${processDef.data.id}/publish`, null, { headers: admin.auth });

    const slaPolicy = await admin.api.post(
      '/api/sla-policies',
      { processDefinitionId: processDef.data.id, nodeId: 'approve', enabled: true, durationMinutes: 480, warningOffsetMinutes: 60 },
      { headers: admin.auth },
    );
    expect(slaPolicy.status).toBe(200);

    const slaList = await admin.api.get('/api/sla-policies', { headers: admin.auth });
    expect(slaList.status).toBe(200);
    expect(slaList.data.some((p: { id: string }) => p.id === slaPolicy.data.id)).toBe(true);

    const slaUpdated = await admin.api.put(
      `/api/sla-policies/${slaPolicy.data.id}`,
      { enabled: true, durationMinutes: 240, warningOffsetMinutes: 30, expectedVersion: slaPolicy.data.rowVersion },
      { headers: admin.auth },
    );
    expect(slaUpdated.status).toBe(200);
    expect(slaUpdated.data.durationMinutes).toBe(240);

    const slaAudit = await admin.api.get('/api/audit-logs', { headers: admin.auth, params: { entityId: slaPolicy.data.id, pageSize: 50 } });
    const slaActions = slaAudit.data.items.map((a: { action: string }) => a.action);
    expect(slaActions).toContain('CreateSlaPolicy');
    expect(slaActions).toContain('UpdateSlaPolicy');

    // ---- 12-16: Create test user, reset password, verify old rejected/new accepted, verify audit ----
    const testUser = await createAndLogin(admin, `phase9-user-${suffix}`, 'Phase9 Target User', 'OriginalPassw0rd!1');
    const userDetail = await admin.api.get(`/api/users/${testUser.userId}`, { headers: admin.auth });
    expect(userDetail.status).toBe(200);

    const resetResult = await admin.api.post(
      `/api/users/${testUser.userId}/reset-password`,
      { newPassword: 'ResetByAdmin!123', expectedVersion: userDetail.data.rowVersion },
      { headers: admin.auth },
    );
    expect(resetResult.status).toBe(200);

    const loginWithOldPassword = await client().post('/api/auth/login', { username: testUser.username, password: 'OriginalPassw0rd!1' });
    expect(loginWithOldPassword.status).toBe(401);

    const loginWithNewPassword = await client().post('/api/auth/login', { username: testUser.username, password: 'ResetByAdmin!123' });
    expect(loginWithNewPassword.status).toBe(200);
    const testUserAuth = { Authorization: `Bearer ${loginWithNewPassword.data.accessToken}` };

    const resetAudit = await admin.api.get('/api/audit-logs', { headers: admin.auth, params: { entityId: testUser.userId, pageSize: 50 } });
    const resetActions = resetAudit.data.items.map((a: { action: string }) => a.action);
    expect(resetActions).toContain('ResetPassword');

    // ---- 17-19: Login as test user, self-service change password, verify old rejected/new accepted ----
    const changeResult = await client().post(
      '/api/users/me/change-password',
      { currentPassword: 'ResetByAdmin!123', newPassword: 'SelfChanged!456' },
      { headers: testUserAuth },
    );
    expect(changeResult.status).toBe(204);

    const loginWithResetPassword = await client().post('/api/auth/login', { username: testUser.username, password: 'ResetByAdmin!123' });
    expect(loginWithResetPassword.status).toBe(401);

    const loginWithSelfChangedPassword = await client().post('/api/auth/login', { username: testUser.username, password: 'SelfChanged!456' });
    expect(loginWithSelfChangedPassword.status).toBe(200);

    // A user can never change another user's password through this endpoint — the caller identity
    // always comes from the JWT, never the request body (there is no userId field to even try).
    const otherUser = await createAndLogin(admin, `phase9-other-${suffix}`, 'Phase9 Other User');
    const crossUserChangeAttempt = await client().post(
      '/api/users/me/change-password',
      { currentPassword: 'Passw0rd!123', newPassword: 'ShouldNeverApply!789' },
      { headers: { Authorization: `Bearer ${loginWithSelfChangedPassword.data.accessToken}` } },
    );
    // Applies only to the caller (testUser) — verify it did NOT touch otherUser's credentials.
    expect(crossUserChangeAttempt.status).toBe(400); // wrong current password for testUser itself
    const otherUserStillWorks = await client().post('/api/auth/login', { username: otherUser.username, password: 'Passw0rd!123' });
    expect(otherUserStillWorks.status).toBe(200);

    // ---- 20-23: Login admin again (still authenticated), rename test Role, verify, stale update -> 409, verify audit ----
    const testRole = await admin.api.post('/api/roles', { name: `Reviewer-${suffix}` }, { headers: admin.auth });
    expect(testRole.status).toBe(200);

    const roleRenamed = await admin.api.put(
      `/api/roles/${testRole.data.id}`,
      { name: `SeniorReviewer-${suffix}`, expectedVersion: testRole.data.rowVersion },
      { headers: admin.auth },
    );
    expect(roleRenamed.status).toBe(200);
    expect(roleRenamed.data.name).toBe(`SeniorReviewer-${suffix}`);

    const roleStaleUpdate = await admin.api.put(
      `/api/roles/${testRole.data.id}`,
      { name: `StaleName-${suffix}`, expectedVersion: testRole.data.rowVersion },
      { headers: admin.auth },
    );
    expect(roleStaleUpdate.status).toBe(409);
    expect(roleStaleUpdate.data.code).toBe('ROLE_CONCURRENCY_CONFLICT');

    const roleAudit = await admin.api.get('/api/audit-logs', { headers: admin.auth, params: { entityId: testRole.data.id, pageSize: 50 } });
    const roleActions = roleAudit.data.items.map((a: { action: string }) => a.action);
    expect(roleActions).toContain('CreateRole');
    expect(roleActions).toContain('ModifyRole');

    // Administrator role protection: renaming it is rejected regardless of caller.
    const roles = await admin.api.get('/api/roles', { headers: admin.auth });
    const adminRole = roles.data.find((r: { name: string }) => r.name === 'Administrator');
    expect(adminRole).toBeTruthy();
    const renameAdminAttempt = await admin.api.put(
      `/api/roles/${adminRole.id}`,
      { name: `NotAdministrator-${suffix}`, expectedVersion: adminRole.rowVersion },
      { headers: admin.auth },
    );
    expect(renameAdminAttempt.status).toBe(409);
    expect(renameAdminAttempt.data.code).toBe('CANNOT_RENAME_ADMINISTRATOR_ROLE');

    // ---- 24: Open Operational Health, verify safe response ----
    const health = await admin.api.get('/api/operational-health', { headers: admin.auth });
    expect(health.status).toBe(200);
    expect(health.data.database.status).toBe('Healthy');
    expect(['Healthy', 'Unhealthy']).toContain(health.data.objectStorage.status);
    expect(health.data.cache.status).toBe('NotInstrumented');

    // ---- 25: Verify no secrets in the Operational Health response ----
    const healthJson = JSON.stringify(health.data);
    expect(healthJson).not.toMatch(/ChangeMe123!|password|Password=|Secret|connectionstring/i);

    // ---- 26: Verify Operational Health is Administrator-only ----
    const nonAdminHealthAttempt = await client().post('/api/auth/login', { username: otherUser.username, password: 'Passw0rd!123' });
    const nonAdminHealth = await client().get('/api/operational-health', { headers: { Authorization: `Bearer ${nonAdminHealthAttempt.data.accessToken}` }, validateStatus: () => true });
    expect(nonAdminHealth.status).toBe(403);

    // ---- 27: Verify Notification Delivery diagnostic ----
    expect(health.data.notificationDelivery).toEqual(
      expect.objectContaining({
        pendingCount: expect.any(Number),
        processingCount: expect.any(Number),
        failedCount: expect.any(Number),
        sentLast24Hours: expect.any(Number),
      }),
    );

    // ---- 28: Verify SLA scheduler configuration signal is present and honestly labeled ----
    expect(typeof health.data.configuration.slaScheduler.enabled).toBe('boolean');

    // ---- 29-30: Run existing workflow scenario, verify TaskSla still calculates using the policy ----
    const instance = await testUser.api.post('/api/process-instances', { processDefinitionKey: processDef.data.key }, { headers: testUserAuth });
    expect(instance.status).toBe(201);
    const tasks = await testUser.api.get('/api/tasks', { headers: testUserAuth });
    const task = tasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === instance.data.id);
    expect(task).toBeTruthy();
    const taskDetail = await testUser.api.get(`/api/tasks/${task.id}`, { headers: testUserAuth });
    expect(taskDetail.data.sla).toBeTruthy();
    expect(taskDetail.data.sla.status).toBe('Active');

    const complete = await testUser.api.post(`/api/tasks/${task.id}/complete`, null, { headers: testUserAuth });
    expect(complete.status).toBe(200);

    // Updating the SLA policy afterward must never rewrite this already-created TaskSla's history.
    const secondInstance = await testUser.api.post('/api/process-instances', { processDefinitionKey: processDef.data.key }, { headers: testUserAuth });
    const secondTasks = await testUser.api.get('/api/tasks', { headers: testUserAuth });
    const secondTask = secondTasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === secondInstance.data.id);
    const secondTaskDetailBefore = await testUser.api.get(`/api/tasks/${secondTask.id}`, { headers: testUserAuth });
    const dueAtBeforePolicyChange = secondTaskDetailBefore.data.sla.dueAt;

    await admin.api.put(
      `/api/sla-policies/${slaPolicy.data.id}`,
      { enabled: true, durationMinutes: 999, warningOffsetMinutes: 30, expectedVersion: slaUpdated.data.rowVersion },
      { headers: admin.auth },
    );
    const secondTaskDetailAfter = await testUser.api.get(`/api/tasks/${secondTask.id}`, { headers: testUserAuth });
    expect(secondTaskDetailAfter.data.sla.dueAt).toBe(dueAtBeforePolicyChange);

    // ---- 31: Verify Monitoring/Reporting/Analytics still work ----
    const monitoring = await admin.api.get('/api/process-monitoring', { headers: admin.auth, params: { processDefinitionId: processDef.data.id } });
    expect(monitoring.status).toBe(200);
    expect(monitoring.data.items.length).toBeGreaterThanOrEqual(2);

    const reportSummary = await admin.api.get('/api/reports/summary', { headers: admin.auth, params: { processDefinitionId: processDef.data.id } });
    expect(reportSummary.status).toBe(200);
    expect(reportSummary.data.process.total).toBe(2);

    const analyticsOverview = await admin.api.get('/api/analytics/overview', { headers: admin.auth, params: { processDefinitionId: processDef.data.id } });
    expect(analyticsOverview.status).toBe(200);
    expect(analyticsOverview.data.totalProcesses).toBe(2);

    // ---- 32: Verify Phase 8 lifecycle governance still works ----
    const beforeSuspend = await admin.api.get(`/api/process-definitions/${processDef.data.id}`, { headers: admin.auth });
    const suspended = await admin.api.post(`/api/process-definitions/${processDef.data.id}/suspend`, { expectedVersion: beforeSuspend.data.rowVersion }, { headers: admin.auth });
    expect(suspended.status).toBe(200);
    expect(suspended.data.status).toBe('Suspended');
    const restored = await admin.api.post(`/api/process-definitions/${processDef.data.id}/restore`, { expectedVersion: suspended.data.rowVersion }, { headers: admin.auth });
    expect(restored.status).toBe(200);
    expect(restored.data.status).toBe('Published');
  });

  // Password security is verified through real login attempts (not merely HTTP status codes) —
  // a dedicated, isolated scenario proving a stale ExpectedVersion is rejected before any
  // credential is touched, so a partial-failure race can't silently apply a reset.
  it('rejects a password reset with a stale ExpectedVersion, leaving the original password intact', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();
    const user = await createAndLogin(admin, `phase9-stale-${suffix}`, 'Stale Reset User', 'OriginalPassw0rd!1');
    const userDetail = await admin.api.get(`/api/users/${user.userId}`, { headers: admin.auth });

    const staleReset = await admin.api.post(
      `/api/users/${user.userId}/reset-password`,
      { newPassword: 'ShouldNotApply!123', expectedVersion: 'AAAAAAAAAAA=' },
      { headers: admin.auth },
    );
    expect(staleReset.status).toBe(409);
    expect(staleReset.data.code).toBe('USER_CONCURRENCY_CONFLICT');

    const loginStillWorks = await client().post('/api/auth/login', { username: user.username, password: 'OriginalPassw0rd!1' });
    expect(loginStillWorks.status).toBe(200);
    void userDetail;
  });

  // Authorization/IDOR: a non-Administrator can never reset another user's password, and a
  // password reset for a nonexistent user id returns 404 rather than leaking existence info via a
  // different status.
  it('rejects password reset from a non-Administrator caller and for a nonexistent user', async () => {
    const admin = await loginAsAdmin();
    const suffix = uniqueSuffix();
    const nonAdmin = await createAndLogin(admin, `phase9-nonadmin-${suffix}`, 'Non Admin');
    const target = await createAndLogin(admin, `phase9-idor-target-${suffix}`, 'IDOR Target');
    const targetDetail = await admin.api.get(`/api/users/${target.userId}`, { headers: admin.auth });

    const forbiddenAttempt = await nonAdmin.api.post(
      `/api/users/${target.userId}/reset-password`,
      { newPassword: 'ShouldBeRejected!123', expectedVersion: targetDetail.data.rowVersion },
      { headers: nonAdmin.auth },
    );
    expect(forbiddenAttempt.status).toBe(403);

    const notFoundAttempt = await admin.api.post(
      `/api/users/00000000-0000-0000-0000-000000000000/reset-password`,
      { newPassword: 'ShouldBeRejected!123', expectedVersion: 'AAAAAAAAAAE=' },
      { headers: admin.auth },
    );
    expect(notFoundAttempt.status).toBe(404);
  });
});
