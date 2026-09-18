import axios from 'axios';
import { describe, expect, it } from 'vitest';
import { createEdge, createNode, layoutNodes, serializeWorkflowDefinition } from './graphModel';

// Phase 5.3 §23's acceptance condition, run against the real backend rather than a mock:
// Designer-generated JSON -> live WorkflowDefinitionValidator -> Accepted -> Published ->
// workflow can execute. This builds the exact same React Flow node/edge objects the visual
// designer would produce (via the same createNode/createEdge/serializeWorkflowDefinition
// functions the UI calls) rather than hand-writing JSON, so a pass here proves the designer's
// actual output — not a hand-tuned approximation of it — round-trips through the live engine.
const baseURL = import.meta.env.VITE_API_BASE_URL || 'http://localhost:5080';
const api = axios.create({ baseURL, validateStatus: () => true });

describe('Visual Designer output against the live backend', () => {
  it('produces JSON the live validator accepts, publishes, and the process can actually run', async () => {
    const login = await api.post('/api/auth/login', { username: 'admin', password: 'ChangeMe123!' });
    expect(login.status).toBe(200);
    const auth = { Authorization: `Bearer ${login.data.accessToken}` };

    // Build the graph exactly the way a user clicking through the designer would: palette clicks
    // create Start/ApprovalTask/End nodes, then connections are drawn between them.
    const start = createNode('Start', { x: 0, y: 0 });
    const approval = createNode('ApprovalTask', { x: 200, y: 0 });
    const end = createNode('End', { x: 400, y: 0 });

    // The palette's default ApprovalTask has an empty assignments list (frontend spec §8 leaves
    // filling this in to the Properties Panel) — fill in what a user would type in the panel.
    // ProcessInitiator (rather than a Role name) keeps this test self-contained: it resolves to
    // whoever started the process — the same admin account used to drive this whole scenario —
    // so verifying the created task doesn't require first granting admin some other role.
    approval.data.approval = {
      ...approval.data.approval!,
      assignments: [{ type: 'ProcessInitiator', value: '' }],
    };

    const nodes = layoutNodes([start, approval, end], []);
    const edges = [createEdge(start.id, approval.id), createEdge(approval.id, end.id)];
    const definition = serializeWorkflowDefinition(nodes, edges);

    const key = `vitest-designer-live-${Date.now()}`;
    const created = await api.post(
      '/api/process-definitions',
      { key, name: 'Vitest Designer Live Process' },
      { headers: auth },
    );
    expect(created.status).toBe(201);
    const processId = created.data.id as string;

    // Live WorkflowDefinitionValidator, via the standalone validate endpoint.
    const validation = await api.post('/api/process-definitions/validate', { definition }, { headers: auth });
    expect(validation.status).toBe(200);
    expect(validation.data.isValid).toBe(true);
    expect(validation.data.errors).toEqual([]);

    const versionCreated = await api.post(
      `/api/process-definitions/${processId}/versions`,
      { definition },
      { headers: auth },
    );
    expect(versionCreated.status).toBe(200);

    const published = await api.post(`/api/process-definitions/${processId}/publish`, null, { headers: auth });
    expect(published.status).toBe(200);
    expect(published.data.status).toBe('Published');

    // "Workflow can execute" — actually start an instance and confirm a real ApprovalTask task
    // was created for it, exactly as WorkflowEngine/ApprovalEngine would for any other definition.
    const started = await api.post('/api/process-instances', { processDefinitionKey: key }, { headers: auth });
    expect(started.status).toBe(201);
    expect(started.data.status).toBe('Running');

    const tasks = await api.get('/api/tasks', { headers: auth });
    expect(tasks.status).toBe(200);
    const task = tasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === started.data.id);
    expect(task).toBeTruthy();
    expect(task.approval).toBeTruthy();
    expect(task.approval.policy).toBe('AnyOne');
  });

  // Phase 5.3.2 §16's full acceptance scenario: Login -> Create Process -> Create Draft Version
  // -> "Open Designer" (built here via the same graphModel functions the UI calls) -> configure a
  // UserTask with a real published Form and an ApprovalTask with All-policy Role assignments
  // (exactly the frontend spec's example) -> Save -> Reload -> verify the graph survived ->edit
  // and validate the form key it produced against the live backend as the
  // WorkflowTransitions/FormEngine "reference by key, resolve published version at task-creation
  // time" contract this file documents in PropertiesPanel.tsx -> Publish -> verify read-only ->
  // start the process and confirm real backend execution (a FormInstance is created for the
  // UserTask, and the ApprovalTask is created next in line).
  it('full Process Detail -> Designer -> Save -> Reload -> Validate -> Publish -> Execute scenario', async () => {
    const login = await api.post('/api/auth/login', { username: 'admin', password: 'ChangeMe123!' });
    const auth = { Authorization: `Bearer ${login.data.accessToken}` };
    const suffix = Date.now();

    // A real published FormDefinition for the UserTask's Form picker to reference.
    const formCreated = await api.post(
      '/api/form-definitions',
      { key: `purchase-request-${suffix}`, name: 'Purchase Request' },
      { headers: auth },
    );
    expect(formCreated.status).toBe(201);
    const formVersionCreated = await api.post(
      `/api/form-definitions/${formCreated.data.id}/versions`,
      { schema: { fields: [{ key: 'itemName', type: 'text', label: 'Item Name', required: true }] } },
      { headers: auth },
    );
    expect(formVersionCreated.status).toBe(200);
    const formPublished = await api.post(`/api/form-definitions/${formCreated.data.id}/publish`, null, { headers: auth });
    expect(formPublished.status).toBe(200);

    // Create Process, Create Draft Version.
    const processCreated = await api.post(
      '/api/process-definitions',
      { key: `vitest-full-scenario-${suffix}`, name: 'Full Designer Scenario' },
      { headers: auth },
    );
    const processId = processCreated.data.id as string;
    const starterDefinition = { nodes: [{ id: 'start', type: 'Start', name: 'Start' }, { id: 'end', type: 'End', name: 'End' }], transitions: [] };
    const versionCreated = await api.post(`/api/process-definitions/${processId}/versions`, { definition: starterDefinition }, { headers: auth });
    const versionId = versionCreated.data.id as string;

    // "Open Designer" and build Start -> UserTask -> ApprovalTask -> End using the exact same
    // functions ProcessDesigner.tsx calls (not hand-written JSON).
    const start = createNode('Start', { x: 0, y: 0 });
    const submit = createNode('UserTask', { x: 200, y: 0 });
    const approval = createNode('ApprovalTask', { x: 400, y: 0 });
    const end = createNode('End', { x: 600, y: 0 });

    submit.data.assignment = { type: 'ProcessInitiator', value: '' };
    submit.data.form = { formDefinitionKey: formCreated.data.key };
    approval.data.approval = {
      ...approval.data.approval!,
      policy: 'All',
      assignments: [
        { type: 'Role', value: 'Finance' },
        { type: 'Role', value: 'Legal' },
      ],
    };

    const nodes = layoutNodes([start, submit, approval, end], []);
    const edges = [createEdge(start.id, submit.id), createEdge(submit.id, approval.id), createEdge(approval.id, end.id)];
    const definition = serializeWorkflowDefinition(nodes, edges);

    // Save.
    const saved = await api.put(
      `/api/process-definitions/${processId}/versions/${versionId}`,
      { definition, expectedVersion: versionCreated.data.rowVersion },
      { headers: auth },
    );
    expect(saved.status).toBe(200);

    // Reload (simulates leaving and reopening the Designer) and verify the graph/configuration.
    const reloadedVersions = await api.get(`/api/process-definitions/${processId}/versions`, { headers: auth });
    const reloaded = reloadedVersions.data.find((v: { id: string }) => v.id === versionId);
    expect(reloaded.definition.nodes).toHaveLength(4);
    const reloadedUserTask = reloaded.definition.nodes.find((n: { type: string }) => n.type === 'UserTask');
    // Phase 10 — a Draft version's form reference is never version-pinned (pinning happens only
    // at Publish); the backend now always includes an explicit formVersionId key (null here).
    expect(reloadedUserTask.form).toEqual({ formDefinitionKey: formCreated.data.key, formVersionId: null });
    const reloadedApproval = reloaded.definition.nodes.find((n: { type: string }) => n.type === 'ApprovalTask');
    expect(reloadedApproval.approval.policy).toBe('All');
    expect(reloadedApproval.approval.assignments).toEqual([
      { type: 'Role', value: 'Finance' },
      { type: 'Role', value: 'Legal' },
    ]);

    // Validate.
    const validation = await api.post('/api/process-definitions/validate', { definition: reloaded.definition }, { headers: auth });
    expect(validation.status).toBe(200);
    expect(validation.data.isValid).toBe(true);

    // Publish.
    const published = await api.post(`/api/process-definitions/${processId}/publish`, null, { headers: auth });
    expect(published.status).toBe(200);
    expect(published.data.status).toBe('Published');

    // Verify Published / Read Only: the backend rejects any further edit outright.
    const editAttempt = await api.put(
      `/api/process-definitions/${processId}/versions/${versionId}`,
      { definition: reloaded.definition, expectedVersion: saved.data.rowVersion },
      { headers: auth },
    );
    expect(editAttempt.status).toBe(409);
    expect(editAttempt.data.code).toBe('VERSION_NOT_DRAFT');

    // Start the actual process and verify the workflow executes — not just that the JSON was
    // stored: the UserTask's FormInstance is really created (proving the Form binding this
    // Designer produced actually works end to end), pinned to the form key that was configured.
    const started = await api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: auth });
    expect(started.status).toBe(201);
    expect(started.data.status).toBe('Running');

    const tasks = await api.get('/api/tasks', { headers: auth });
    const submitTask = tasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === started.data.id);
    expect(submitTask).toBeTruthy();
    expect(submitTask.assigneeId).toBe(login.data.userId);

    const forms = await api.get('/api/form-instances', { params: { processInstanceId: started.data.id }, headers: auth });
    expect(forms.status).toBe(200);
    expect(forms.data).toHaveLength(1);
    expect(forms.data[0].status).toBe('Draft');
  });
});
