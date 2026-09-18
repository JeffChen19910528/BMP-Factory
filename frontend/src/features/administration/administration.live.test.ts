import axios from 'axios';
import { describe, expect, it } from 'vitest';

// Phase 5.5.2 — Administration Foundation, run against the real rebuilt Docker stack with genuinely
// distinct logged-in users (Administrator/Normal User/Unauthorized User), covering this phase's own
// Scenarios A-H over the real HTTP endpoints. This is the only way to actually exercise
// [Authorize(Roles="Administrator")] — an in-process test bypasses the ASP.NET auth pipeline
// entirely, so authorization/IDOR/privilege-escalation must be proven here, not just unit-tested.
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

describe('Administration against the live backend', () => {
  it('covers Scenarios A-H: Users/Departments/Roles/Audit CRUD+persist, denial of non-admins, IDOR, privilege escalation, and concurrency', async () => {
    const admin = await loginAsAdmin();
    const suffix = Date.now();

    const normal = await createAndLogin(admin, `normal-${suffix}`, 'Normal User');
    const unauthorized = await createAndLogin(admin, `stranger-${suffix}`, 'Unauthorized User');

    const orgs = await admin.api.get('/api/organizations', { headers: admin.auth });
    let orgId = orgs.data[0]?.id as string | undefined;
    if (!orgId) {
      const createOrg = await admin.api.post('/api/organizations', { name: `Org-${suffix}`, parentId: null }, { headers: admin.auth });
      orgId = createOrg.data.id;
    }

    // ---- Scenario A: Users create+assign+persist ----
    const deptA = await admin.api.post('/api/departments', { name: `Dept-A-${suffix}`, organizationId: orgId, parentId: null, managerUserId: null }, { headers: admin.auth });
    expect(deptA.status).toBe(200);

    const scenarioAUser = await admin.api.post(
      '/api/users',
      { username: `scenA-${suffix}`, displayName: 'Scenario A User', email: `scenA-${suffix}@bpm-tests.local`, password: 'Passw0rd!123', departmentId: deptA.data.id },
      { headers: admin.auth },
    );
    expect(scenarioAUser.status).toBe(201);

    const roles = await admin.api.get('/api/roles', { headers: admin.auth });
    let managerRole = roles.data.find((r: { name: string }) => r.name !== 'Administrator');
    if (!managerRole) {
      const createRole = await admin.api.post('/api/roles', { name: `Role-A-${suffix}` }, { headers: admin.auth });
      managerRole = createRole.data;
    }
    const assignA = await admin.api.post('/api/roles/assign', { userId: scenarioAUser.data.id, roleId: managerRole.id }, { headers: admin.auth });
    expect(assignA.status).toBe(204);

    const persistedUsers = await admin.api.get('/api/users', { headers: admin.auth });
    const foundA = persistedUsers.data.find((u: { id: string }) => u.id === scenarioAUser.data.id);
    expect(foundA?.departmentId).toBe(deptA.data.id);
    const membersA = await admin.api.get(`/api/roles/${managerRole.id}/members`, { headers: admin.auth });
    expect(membersA.data.some((m: { id: string }) => m.id === scenarioAUser.data.id)).toBe(true);

    // ---- Scenario B: Department create+assign manager+persist ----
    const deptB = await admin.api.post('/api/departments', { name: `Dept-B-${suffix}`, organizationId: orgId, parentId: null, managerUserId: scenarioAUser.data.id }, { headers: admin.auth });
    expect(deptB.status).toBe(200);
    expect(deptB.data.managerUserId).toBe(scenarioAUser.data.id);
    const deptListB = await admin.api.get('/api/departments', { headers: admin.auth });
    expect(deptListB.data.find((d: { id: string }) => d.id === deptB.data.id)?.managerUserId).toBe(scenarioAUser.data.id);

    // ---- Scenario C: Role view members + change membership + persist ----
    const unassignC = await admin.api.post('/api/roles/unassign', { userId: scenarioAUser.data.id, roleId: managerRole.id }, { headers: admin.auth });
    expect(unassignC.status).toBe(204);
    const membersCAfter = await admin.api.get(`/api/roles/${managerRole.id}/members`, { headers: admin.auth });
    expect(membersCAfter.data.some((m: { id: string }) => m.id === scenarioAUser.data.id)).toBe(false);

    // ---- Scenario D: Audit shows relevant events with Actor/Action/Entity/Timestamp ----
    const auditD = await admin.api.get('/api/audit-logs', { params: { entityType: 'User', entityId: scenarioAUser.data.id }, headers: admin.auth });
    expect(auditD.status).toBe(200);
    expect(auditD.data.items.some((e: { action: string; entityId: string }) => e.entityId === scenarioAUser.data.id && e.action === 'CreateUser')).toBe(true);

    // ---- Scenario E: Normal User denied Administration API ----
    expect((await normal.api.get('/api/roles', { headers: normal.auth })).status).toBe(403);
    expect((await normal.api.get('/api/audit-logs', { headers: normal.auth })).status).toBe(403);
    expect(
      (await normal.api.post('/api/users', { username: 'x', displayName: 'x', email: 'x@bpm-tests.local', password: 'Passw0rd!123' }, { headers: normal.auth })).status,
    ).toBe(403);
    expect(
      (
        await normal.api.put(
          `/api/departments/${deptA.data.id}`,
          { name: 'Hacked', parentId: null, managerUserId: null, expectedVersion: deptA.data.rowVersion },
          { headers: normal.auth },
        )
      ).status,
    ).toBe(403);

    // ---- Scenario F: IDOR — identifier/query-param manipulation must not bypass authorization ----
    expect(
      (
        await normal.api.put(
          `/api/users/${scenarioAUser.data.id}`,
          { displayName: 'Hacked', email: foundA.email, departmentId: null, isActive: true, expectedVersion: foundA.rowVersion },
          { headers: normal.auth },
        )
      ).status,
    ).toBe(403);
    expect((await normal.api.post('/api/roles/unassign', { userId: normal.userId, roleId: managerRole.id }, { headers: normal.auth })).status).toBe(403);
    expect(
      (await normal.api.get('/api/audit-logs', { params: { userId: scenarioAUser.data.id, entityId: scenarioAUser.data.id }, headers: normal.auth })).status,
    ).toBe(403);
    expect((await normal.api.get(`/api/roles/${managerRole.id}/members`, { headers: normal.auth })).status).toBe(403);
    expect((await unauthorized.api.get(`/api/roles/${managerRole.id}/members`, { headers: unauthorized.auth })).status).toBe(403);

    // ---- Scenario G: Privilege escalation — self-assign Administrator, modify administrator ----
    const adminRole = roles.data.find((r: { name: string }) => r.name === 'Administrator');
    expect((await normal.api.post('/api/roles/assign', { userId: normal.userId, roleId: adminRole.id }, { headers: normal.auth })).status).toBe(403);
    expect(
      (
        await normal.api.put(
          `/api/users/${admin.userId}`,
          { displayName: 'Hacked Admin', email: 'admin@bpm.local', departmentId: null, isActive: true, expectedVersion: 'AAAAAAAAAAA=' },
          { headers: normal.auth },
        )
      ).status,
    ).toBe(403);

    // ---- Scenario H: Concurrency — two admins load, first saves, second's stale save conflicts ----
    const deptH = await admin.api.post('/api/departments', { name: `Dept-H-${suffix}`, organizationId: orgId, parentId: null, managerUserId: null }, { headers: admin.auth });
    const loadedByAdminA = deptH.data;
    const loadedByAdminB = { ...deptH.data };
    const firstSave = await admin.api.put(
      `/api/departments/${deptH.data.id}`,
      { name: `Renamed by A ${suffix}`, parentId: null, managerUserId: null, expectedVersion: loadedByAdminA.rowVersion },
      { headers: admin.auth },
    );
    expect(firstSave.status).toBe(200);
    const secondSave = await admin.api.put(
      `/api/departments/${deptH.data.id}`,
      { name: `Renamed by B ${suffix}`, parentId: null, managerUserId: null, expectedVersion: loadedByAdminB.rowVersion },
      { headers: admin.auth },
    );
    expect(secondSave.status).toBe(409);
    expect(secondSave.data.code).toBe('DEPARTMENT_CONCURRENCY_CONFLICT');
    const afterConflict = await admin.api.get('/api/departments', { headers: admin.auth });
    expect(afterConflict.data.find((d: { id: string }) => d.id === deptH.data.id)?.name).toBe(`Renamed by A ${suffix}`);

    // Self-lockout: an administrator cannot remove their own Administrator role.
    const selfLockout = await admin.api.post('/api/roles/unassign', { userId: admin.userId, roleId: adminRole.id }, { headers: admin.auth });
    expect(selfLockout.status).toBe(409);
    expect(selfLockout.data.code).toBe('CANNOT_REMOVE_OWN_ADMINISTRATOR_ROLE');
  });
});
