import axios from 'axios';
import { describe, expect, it } from 'vitest';

// Main acceptance scenario (frontend spec §16), run against the real backend rather than a
// mock: Login -> Processes -> Create Process -> Create Draft Version -> Edit Definition ->
// Validate -> Save -> Publish -> Verify Published Version. Uses axios directly (not the app's
// apiClient, which reads Zustand/browser globals this Node test environment doesn't have) but
// hits the exact same endpoints/DTO shapes the frontend's services/processService.ts calls.
const baseURL = import.meta.env.VITE_API_BASE_URL || 'http://localhost:5080';
const api = axios.create({ baseURL, validateStatus: () => true });

describe('Process Management acceptance scenario (live backend)', () => {
  it('logs in, creates a process, drafts/validates/saves/publishes a version', async () => {
    const login = await api.post('/api/auth/login', { username: 'admin', password: 'ChangeMe123!' });
    expect(login.status).toBe(200);
    const token = login.data.accessToken as string;
    expect(token).toBeTruthy();

    const auth = { Authorization: `Bearer ${token}` };
    const key = `vitest-live-${Date.now()}`;

    const created = await api.post(
      '/api/process-definitions',
      { key, name: 'Vitest Live Process', description: 'created by live acceptance test', category: 'Testing' },
      { headers: auth },
    );
    expect(created.status).toBe(201);
    expect(created.data.status).toBe('Draft');
    const processId = created.data.id as string;

    const listResult = await api.get('/api/process-definitions', { params: { search: key }, headers: auth });
    expect(listResult.status).toBe(200);
    expect(listResult.data.totalCount).toBe(1);
    expect(listResult.data.items[0].id).toBe(processId);

    const invalidDraft = {
      nodes: [{ id: 'start', type: 'Start', name: 'Start' }],
      transitions: [],
    };
    const versionCreated = await api.post(
      `/api/process-definitions/${processId}/versions`,
      { definition: invalidDraft },
      { headers: auth },
    );
    expect(versionCreated.status).toBe(200);
    expect(versionCreated.data.status).toBe('Draft');
    const versionId = versionCreated.data.id as string;

    // Validate: the starter graph has no End node, so this must come back invalid.
    const invalidValidation = await api.post(
      '/api/process-definitions/validate',
      { definition: invalidDraft },
      { headers: auth },
    );
    expect(invalidValidation.status).toBe(200);
    expect(invalidValidation.data.isValid).toBe(false);
    expect(invalidValidation.data.errors.some((e: { code: string }) => e.code === 'MISSING_END_NODE')).toBe(true);

    // Edit Definition: a real Start -> UserTask -> End graph.
    const validDefinition = {
      nodes: [
        { id: 'start', type: 'Start', name: 'Start' },
        { id: 'approval', type: 'UserTask', name: 'Approval', assignment: { type: 'Role', value: 'Manager' } },
        { id: 'end', type: 'End', name: 'End' },
      ],
      transitions: [
        { id: 't1', source: 'start', target: 'approval' },
        { id: 't2', source: 'approval', target: 'end' },
      ],
    };

    const validValidation = await api.post(
      '/api/process-definitions/validate',
      { definition: validDefinition },
      { headers: auth },
    );
    expect(validValidation.status).toBe(200);
    expect(validValidation.data.isValid).toBe(true);

    // Save Draft — Phase 5.3.2 §14: UpdateProcessVersionRequest now requires ExpectedVersion
    // (optimistic concurrency, echoing back ProcessVersionDto.RowVersion from CreateVersionAsync).
    const saved = await api.put(
      `/api/process-definitions/${processId}/versions/${versionId}`,
      { definition: validDefinition, expectedVersion: versionCreated.data.rowVersion },
      { headers: auth },
    );
    expect(saved.status).toBe(200);
    expect(saved.data.definition.nodes).toHaveLength(3);
    expect(saved.data.rowVersion).not.toBe(versionCreated.data.rowVersion);

    // Publish.
    const published = await api.post(`/api/process-definitions/${processId}/publish`, null, { headers: auth });
    expect(published.status).toBe(200);
    expect(published.data.status).toBe('Published');
    expect(published.data.publishedBy).toBeTruthy();
    expect(published.data.publishedAt).toBeTruthy();

    // Verify Published Version: immutable, and the definition's parent flips to Published too.
    // (Still a 409, but VERSION_NOT_DRAFT — the version-status check happens before the
    // concurrency check, so even a current ExpectedVersion is correctly rejected.)
    const rejectedEdit = await api.put(
      `/api/process-definitions/${processId}/versions/${versionId}`,
      { definition: validDefinition, expectedVersion: saved.data.rowVersion },
      { headers: auth },
    );
    expect(rejectedEdit.status).toBe(409);
    expect(rejectedEdit.data.code).toBe('VERSION_NOT_DRAFT');

    const finalDefinition = await api.get(`/api/process-definitions/${processId}`, { headers: auth });
    expect(finalDefinition.data.status).toBe('Published');
    expect(finalDefinition.data.currentVersionId).toBe(versionId);
  });

  it('rejects process creation and validation from a non-admin user with 403', async () => {
    const login = await api.post('/api/auth/login', { username: 'admin', password: 'ChangeMe123!' });
    const adminAuth = { Authorization: `Bearer ${login.data.accessToken}` };

    const username = `vitest-live-user-${Date.now()}`;
    await api.post(
      '/api/users',
      { username, displayName: 'Vitest Live User', email: `${username}@bpm-tests.local`, password: 'Password123!' },
      { headers: adminAuth },
    );

    const userLogin = await api.post('/api/auth/login', { username, password: 'Password123!' });
    expect(userLogin.status).toBe(200);
    const userAuth = { Authorization: `Bearer ${userLogin.data.accessToken}` };

    const forbiddenCreate = await api.post(
      '/api/process-definitions',
      { key: `${username}-proc`, name: 'Should be forbidden' },
      { headers: userAuth },
    );
    expect(forbiddenCreate.status).toBe(403);

    const forbiddenValidate = await api.post(
      '/api/process-definitions/validate',
      { definition: { nodes: [], transitions: [] } },
      { headers: userAuth },
    );
    expect(forbiddenValidate.status).toBe(403);

    const allowedList = await api.get('/api/process-definitions', { headers: userAuth });
    expect(allowedList.status).toBe(200);

    const unauthenticated = await api.get('/api/process-definitions');
    expect(unauthenticated.status).toBe(401);
  });

  it('rejects a stale Save Draft with 409 and does not overwrite the winning save', async () => {
    const login = await api.post('/api/auth/login', { username: 'admin', password: 'ChangeMe123!' });
    const auth = { Authorization: `Bearer ${login.data.accessToken}` };
    const key = `vitest-live-concurrency-${Date.now()}`;

    const created = await api.post('/api/process-definitions', { key, name: 'Concurrency Test' }, { headers: auth });
    const processId = created.data.id as string;
    const definition = {
      nodes: [
        { id: 'start', type: 'Start', name: 'Start' },
        { id: 'end', type: 'End', name: 'End' },
      ],
      transitions: [{ id: 't1', source: 'start', target: 'end' }],
    };
    const versionCreated = await api.post(`/api/process-definitions/${processId}/versions`, { definition }, { headers: auth });
    const versionId = versionCreated.data.id as string;
    const originalRowVersion = versionCreated.data.rowVersion as string;

    // User A saves first — this succeeds and advances the RowVersion.
    const userASave = await api.put(
      `/api/process-definitions/${processId}/versions/${versionId}`,
      { definition, expectedVersion: originalRowVersion },
      { headers: auth },
    );
    expect(userASave.status).toBe(200);

    // User B read the draft at the same original RowVersion (before A's save) and now tries to
    // save against that now-stale token — this must be rejected, not silently applied.
    const userBSave = await api.put(
      `/api/process-definitions/${processId}/versions/${versionId}`,
      { definition, expectedVersion: originalRowVersion },
      { headers: auth },
    );
    expect(userBSave.status).toBe(409);
    expect(userBSave.data.code).toBe('PROCESS_VERSION_CONCURRENCY_CONFLICT');

    // The safe recovery path: reload (GET the current version), then retry with its RowVersion.
    const versions = await api.get(`/api/process-definitions/${processId}/versions`, { headers: auth });
    const reloaded = versions.data.find((v: { id: string }) => v.id === versionId);
    expect(reloaded.rowVersion).toBe(userASave.data.rowVersion);

    const retried = await api.put(
      `/api/process-definitions/${processId}/versions/${versionId}`,
      { definition, expectedVersion: reloaded.rowVersion },
      { headers: auth },
    );
    expect(retried.status).toBe(200);
  });
});
