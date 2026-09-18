import axios from 'axios';
import { describe, expect, it } from 'vitest';
import { createField, createRule, deserializeFormSchema, serializeFormSchema } from '../form/designer/formSchemaModel';

// Phase 5.4.3 §27's full acceptance scenario, run against the real backend: start a real
// ProcessInstance -> resolve the real TaskInstance -> resolve its bound FormInstance the same way
// TaskDetailPage.tsx does (GET /api/form-instances?processInstanceId=... filtered by
// taskInstanceId) -> fill fields, exercise rule-driven conditional-required and calculated-field
// behavior -> Save Draft -> reload -> verify data persisted -> Submit -> verify the backend
// rejects when a conditionally-required field is missing and accepts once it's filled -> verify
// the task itself transitioned to Completed and the process advanced, all through the real
// Workflow Engine (FormEngine.SubmitAsync -> WorkflowTransitions), never a direct
// POST /api/tasks/{id}/complete call for this form-bound case.
const baseURL = import.meta.env.VITE_API_BASE_URL || 'http://localhost:5080';
const api = axios.create({ baseURL, validateStatus: () => true });

describe('Task -> Form Runtime -> Submit -> Complete Task against the live backend', () => {
  it('resolves a task-bound FormInstance, saves a draft, reloads it, is rejected on missing conditional field, then succeeds and completes the task', async () => {
    const login = await api.post('/api/auth/login', { username: 'admin', password: 'ChangeMe123!' });
    expect(login.status).toBe(200);
    const auth = { Authorization: `Bearer ${login.data.accessToken}` };
    const suffix = Date.now();

    // A real published Form with a conditional-required rule and a calculated field, built via
    // the exact Designer functions (not hand-written JSON) — the same schema Phase 5.4.2's own
    // live tests already exercise, reused here to drive an actual task through Runtime.
    const formCreated = await api.post(
      '/api/form-definitions',
      { key: `vitest-task-runtime-${suffix}`, name: 'Vitest Task Runtime Form' },
      { headers: auth },
    );
    expect(formCreated.status).toBe(201);
    const formId = formCreated.data.id as string;

    const starterVersion = await api.post(
      `/api/form-definitions/${formId}/versions`,
      { schema: { fields: [{ key: 'field1', type: 'Text', label: 'x', required: false }] } },
      { headers: auth },
    );
    let fields = deserializeFormSchema(starterVersion.data.schema).filter((f) => f.key !== 'field1');

    const expenseType = createField('Select', new Set(fields.map((f) => f.key)));
    expenseType.key = 'expenseType';
    expenseType.label = 'Expense Type';
    expenseType.required = true;
    expenseType.options = [{ value: 'OTHER', label: 'Other' }, { value: 'TRAVEL', label: 'Travel' }];
    fields = [...fields, expenseType];

    const description = createField('Text', new Set(fields.map((f) => f.key)));
    description.key = 'description';
    description.label = 'Description';
    fields = [...fields, description];

    const quantity = createField('Number', new Set(fields.map((f) => f.key)));
    quantity.key = 'quantity';
    quantity.label = 'Quantity';
    fields = [...fields, quantity];

    const unitPrice = createField('Number', new Set(fields.map((f) => f.key)));
    unitPrice.key = 'unitPrice';
    unitPrice.label = 'Unit Price';
    fields = [...fields, unitPrice];

    const total = createField('Number', new Set(fields.map((f) => f.key)));
    total.key = 'total';
    total.label = 'Total';
    fields = [...fields, total];

    const requiredRule = createRule('Required', 'description', new Set());
    requiredRule.condition = { kind: 'field', field: 'expenseType', operator: 'Equals', value: 'OTHER' };
    const calcRule = createRule('Calculated', 'total', new Set([requiredRule.id]));
    calcRule.formula = 'quantity * unitPrice';

    const schema = serializeFormSchema(fields, [requiredRule, calcRule]);
    const saved = await api.put(
      `/api/form-definitions/${formId}/versions/${starterVersion.data.id}`,
      { schema, expectedVersion: starterVersion.data.rowVersion },
      { headers: auth },
    );
    expect(saved.status).toBe(200);
    await api.post(`/api/form-definitions/${formId}/publish`, null, { headers: auth });

    // A real process whose single UserTask references this form, assigned to the process
    // initiator (admin) so this test drives the whole thing as one user, start to finish.
    const processCreated = await api.post(
      '/api/process-definitions',
      { key: `vitest-task-runtime-proc-${suffix}`, name: 'Task Runtime Process' },
      { headers: auth },
    );
    const processId = processCreated.data.id as string;
    const definition = {
      nodes: [
        { id: 'start', type: 'Start', name: 'Start' },
        { id: 'submit', type: 'UserTask', name: 'Submit Expense', assignment: { type: 'ProcessInitiator', value: '' }, form: { formDefinitionKey: formCreated.data.key } },
        { id: 'end', type: 'End', name: 'End' },
      ],
      transitions: [
        { id: 't1', source: 'start', target: 'submit' },
        { id: 't2', source: 'submit', target: 'end' },
      ],
    };
    await api.post(`/api/process-definitions/${processId}/versions`, { definition }, { headers: auth });
    await api.post(`/api/process-definitions/${processId}/publish`, null, { headers: auth });

    // Start a real ProcessInstance and resolve its real TaskInstance.
    const started = await api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: auth });
    expect(started.status).toBe(201);
    const processInstanceId = started.data.id as string;

    const tasks = await api.get('/api/tasks', { headers: auth });
    const task = tasks.data.items.find((t: { processInstanceId: string }) => t.processInstanceId === processInstanceId);
    expect(task).toBeTruthy();
    expect(task.status).toBe('Pending');

    // Scenario A/F: resolve the task-bound FormInstance exactly the way TaskDetailPage does.
    const formInstances = await api.get('/api/form-instances', { params: { processInstanceId }, headers: auth });
    expect(formInstances.status).toBe(200);
    const boundInstance = formInstances.data.find((f: { taskInstanceId: string | null }) => f.taskInstanceId === task.id);
    expect(boundInstance).toBeTruthy();
    expect(boundInstance.status).toBe('Draft');

    const dataQuery = await api.get(`/api/form-instances/${boundInstance.id}/data`, { headers: auth });
    expect(dataQuery.status).toBe(200);

    // Scenario E: Save Draft with partial data, reload, verify it persisted.
    const draftSave = await api.put(
      `/api/form-instances/${boundInstance.id}/data`,
      { data: { expenseType: 'OTHER', quantity: 3, unitPrice: 100 }, expectedVersion: dataQuery.data.version },
      { headers: auth },
    );
    expect(draftSave.status).toBe(200);
    // Scenario D: the calculated field was recomputed server-side.
    expect(draftSave.data.data.total).toBe(300);

    const reloadedData = await api.get(`/api/form-instances/${boundInstance.id}/data`, { headers: auth });
    expect(reloadedData.data.data.quantity).toBe(3);
    expect(reloadedData.data.data.total).toBe(300);

    // Scenario B/C: expenseType=OTHER makes description required — submitting without it must be
    // rejected by the real backend.
    const submitMissingDescription = await api.post(`/api/form-instances/${boundInstance.id}/submit`, null, { headers: auth });
    expect(submitMissingDescription.status).toBe(400);
    expect(submitMissingDescription.data.errors.some((e: { code: string }) => e.code === 'FIELD_REQUIRED')).toBe(true);

    // Task must still be Pending — a rejected submission must never complete the task.
    const taskAfterRejection = await api.get(`/api/tasks/${task.id}`, { headers: auth });
    expect(taskAfterRejection.data.status).toBe('Pending');

    // Fill in description and submit again — this time it must succeed.
    const finalSave = await api.put(
      `/api/form-instances/${boundInstance.id}/data`,
      { data: { description: 'Office supplies' }, expectedVersion: reloadedData.data.version },
      { headers: auth },
    );
    expect(finalSave.status).toBe(200);

    const submitted = await api.post(`/api/form-instances/${boundInstance.id}/submit`, null, { headers: auth });
    expect(submitted.status).toBe(200);
    // A task-bound instance goes straight to Locked, not just Submitted — completing the linked
    // task (Phase 4's FormEngine.SubmitAsync -> CompleteLinkedTaskAsync) locks the form the same
    // instant, since it fed a task that's now finished.
    expect(submitted.data.status).toBe('Locked');

    // Scenario F, the actual point of this test: the task completed and the process advanced
    // through the real Workflow Engine, purely as a side effect of the form Submit — never a
    // direct POST /api/tasks/{id}/complete call.
    const taskAfterSubmit = await api.get(`/api/tasks/${task.id}`, { headers: auth });
    expect(taskAfterSubmit.data.status).toBe('Completed');

    const processAfter = await api.get(`/api/process-instances/${processInstanceId}`, { headers: auth });
    expect(processAfter.data.status).toBe('Completed');

    // The submitted FormInstance is now locked/read-only per the existing Phase 4 lifecycle.
    const editAttempt = await api.put(
      `/api/form-instances/${boundInstance.id}/data`,
      { data: { description: 'changed' }, expectedVersion: finalSave.data.version },
      { headers: auth },
    );
    expect(editAttempt.status).toBe(409);
  });
});
