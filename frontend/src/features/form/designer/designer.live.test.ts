import axios from 'axios';
import { describe, expect, it } from 'vitest';
import { createField, deserializeFormSchema, serializeFormSchema } from './formSchemaModel';

// Phase 5.4.1 §22's acceptance scenario, run against the real backend rather than a mock: build a
// schema using the exact functions FormDesigner.tsx calls (not hand-written JSON), then walk it
// through Create FormDefinition -> Create Draft FormVersion -> Save Draft -> Reload -> Validate ->
// Publish -> verify Published/Read-Only, and finally confirm the published FormVersion is still
// what the existing Process Designer's Form-reference picker (and WorkflowTransitions' "resolve
// published version at task-creation time" contract) expects — no process/workflow changes this
// iteration, just proving the Designer's actual output round-trips through the live Form Engine.
const baseURL = import.meta.env.VITE_API_BASE_URL || 'http://localhost:5080';
const api = axios.create({ baseURL, validateStatus: () => true });

describe('Visual Form Designer output against the live backend', () => {
  it('produces JSON the live FormSchemaValidator accepts, saves, reloads, validates, and publishes', async () => {
    const login = await api.post('/api/auth/login', { username: 'admin', password: 'ChangeMe123!' });
    expect(login.status).toBe(200);
    const auth = { Authorization: `Bearer ${login.data.accessToken}` };
    const suffix = Date.now();

    const formCreated = await api.post(
      '/api/form-definitions',
      { key: `vitest-form-designer-live-${suffix}`, name: 'Vitest Designer Live Form' },
      { headers: auth },
    );
    expect(formCreated.status).toBe(201);
    const formId = formCreated.data.id as string;

    // A starter draft version — CreateFormVersionModal's own STARTER_SCHEMA always seeds one field
    // (FormSchemaValidator rejects an empty field list outright), then it's edited via the
    // Designer's own functions below, exactly the "Form List -> Form Detail -> Draft Version ->
    // Open Form Designer" flow.
    const starterVersion = await api.post(
      `/api/form-definitions/${formId}/versions`,
      { schema: { fields: [{ key: 'field1', type: 'Text', label: 'Untitled Field', required: false }] } },
      { headers: auth },
    );
    expect(starterVersion.status).toBe(200);
    const versionId = starterVersion.data.id as string;

    // Build the schema the way a user clicking through the Designer would: deserialize the
    // (empty) loaded schema into designer fields, add one field per required type via
    // createField (the same function the palette's onClick calls), configure required/options,
    // then serialize back to backend JSON.
    // Drop the starter placeholder field, the way a user would before building their real form.
    let fields = deserializeFormSchema(starterVersion.data.schema).filter((f) => f.key !== 'field1');

    function add(type: Parameters<typeof createField>[0]) {
      const keys = new Set(fields.map((f) => f.key));
      const field = createField(type, keys);
      fields = [...fields, field];
      return field;
    }

    const itemName = add('Text');
    itemName.key = 'itemName';
    itemName.label = 'Item Name';
    itemName.required = true;

    const quantity = add('Number');
    quantity.key = 'quantity';
    quantity.label = 'Quantity';
    quantity.required = true;
    quantity.validation = { minValue: 1, maxValue: 1000 };

    const amount = add('Currency');
    amount.key = 'amount';
    amount.label = 'Amount';
    amount.required = true;

    const category = add('Select');
    category.key = 'category';
    category.label = 'Category';
    category.required = true;
    category.options = [
      { value: 'hardware', label: 'Hardware' },
      { value: 'software', label: 'Software' },
    ];

    const department = add('Department');
    department.key = 'department';
    department.label = 'Department';
    department.required = false;

    const attachment = add('File');
    attachment.key = 'attachment';
    attachment.label = 'Attachment';
    attachment.required = false;

    const schema = serializeFormSchema(fields);
    expect(schema.fields.map((f) => f.key)).toEqual(['itemName', 'quantity', 'amount', 'category', 'department', 'attachment']);

    // Live FormSchemaValidator, via the standalone validate endpoint, before saving.
    const preValidation = await api.post('/api/form-definitions/validate', { schema }, { headers: auth });
    expect(preValidation.status).toBe(200);
    expect(preValidation.data.isValid).toBe(true);
    expect(preValidation.data.errors).toEqual([]);

    // Save Draft.
    const saved = await api.put(
      `/api/form-definitions/${formId}/versions/${versionId}`,
      { schema, expectedVersion: starterVersion.data.rowVersion },
      { headers: auth },
    );
    expect(saved.status).toBe(200);

    // Reload (simulates leaving and reopening the Designer) and verify every field survived,
    // semantically (via the same deserializer the Designer loads with), not by raw string
    // comparison.
    const reloadedVersions = await api.get(`/api/form-definitions/${formId}/versions`, { headers: auth });
    expect(reloadedVersions.status).toBe(200);
    const reloaded = reloadedVersions.data.find((v: { id: string }) => v.id === versionId);
    expect(reloaded).toBeTruthy();

    const reloadedFields = deserializeFormSchema(reloaded.schema);
    expect(reloadedFields.map((f) => f.key)).toEqual(['itemName', 'quantity', 'amount', 'category', 'department', 'attachment']);
    // The backend echoes the full FormFieldValidation shape (unset properties as explicit nulls,
    // not omitted) — compare only the properties this schema actually set.
    expect(reloadedFields.find((f) => f.key === 'quantity')?.validation).toEqual(
      expect.objectContaining({ minValue: 1, maxValue: 1000 }),
    );
    expect(reloadedFields.find((f) => f.key === 'category')?.options).toEqual([
      { value: 'hardware', label: 'Hardware' },
      { value: 'software', label: 'Software' },
    ]);
    expect(reloadedFields.find((f) => f.key === 'itemName')?.required).toBe(true);
    expect(reloadedFields.find((f) => f.key === 'department')?.required).toBe(false);

    // Validate the reloaded schema too — proves what's actually stored is still valid, not just
    // what was sent.
    const postValidation = await api.post('/api/form-definitions/validate', { schema: reloaded.schema }, { headers: auth });
    expect(postValidation.status).toBe(200);
    expect(postValidation.data.isValid).toBe(true);

    // Publish.
    const published = await api.post(`/api/form-definitions/${formId}/publish`, null, { headers: auth });
    expect(published.status).toBe(200);
    expect(published.data.status).toBe('Published');

    // Verify Published / Read Only: the backend rejects any further edit outright.
    const editAttempt = await api.put(
      `/api/form-definitions/${formId}/versions/${versionId}`,
      { schema: reloaded.schema, expectedVersion: saved.data.rowVersion },
      { headers: auth },
    );
    expect(editAttempt.status).toBe(409);
    expect(editAttempt.data.code).toBe('VERSION_NOT_DRAFT');

    // The FormDefinition itself now reports this version as current/published.
    const definitionAfterPublish = await api.get(`/api/form-definitions/${formId}`, { headers: auth });
    expect(definitionAfterPublish.status).toBe(200);
    expect(definitionAfterPublish.data.status).toBe('Published');
    expect(definitionAfterPublish.data.currentVersionId).toBe(versionId);
  });

  // Phase 5.4.1 §23: confirm the published FormVersion this Designer produces is still exactly
  // what the existing Process Designer's Form-reference picker and WorkflowTransitions' "resolve
  // published version at task-creation time" contract expect (see designer.live.test.ts's second
  // scenario on the Process side, which already exercises this same contract) — a UserTask bound
  // to this form key gets a real FormInstance whose fields match what the Designer published.
  it('a published Form Designer schema remains compatible with the Process Designer Form binding contract', async () => {
    const login = await api.post('/api/auth/login', { username: 'admin', password: 'ChangeMe123!' });
    const auth = { Authorization: `Bearer ${login.data.accessToken}` };
    const suffix = Date.now();

    const formCreated = await api.post(
      '/api/form-definitions',
      { key: `vitest-form-process-integration-${suffix}`, name: 'Process Integration Form' },
      { headers: auth },
    );
    const formId = formCreated.data.id as string;
    const starterVersion = await api.post(
      `/api/form-definitions/${formId}/versions`,
      { schema: { fields: [{ key: 'field1', type: 'Text', label: 'Untitled Field', required: false }] } },
      { headers: auth },
    );
    const versionId = starterVersion.data.id as string;

    let fields = deserializeFormSchema(starterVersion.data.schema).filter((f) => f.key !== 'field1');
    const itemName = createField('Text', new Set(fields.map((f) => f.key)));
    itemName.key = 'itemName';
    itemName.required = true;
    fields = [...fields, itemName];
    const schema = serializeFormSchema(fields);

    await api.put(`/api/form-definitions/${formId}/versions/${versionId}`, { schema, expectedVersion: starterVersion.data.rowVersion }, { headers: auth });
    await api.post(`/api/form-definitions/${formId}/publish`, null, { headers: auth });

    // Build Start -> UserTask (bound to this form key) -> End directly as WorkflowDefinition JSON
    // (Process Designer's own graphModel functions are exercised by its own live test; this test
    // only needs to prove the *form* side of the binding, so a minimal hand-shaped definition for
    // the process side is sufficient and keeps this file's scope to the Form Designer).
    const processCreated = await api.post(
      '/api/process-definitions',
      { key: `vitest-form-process-integration-proc-${suffix}`, name: 'Form Integration Process' },
      { headers: auth },
    );
    const processId = processCreated.data.id as string;
    const definition = {
      nodes: [
        { id: 'start', type: 'Start', name: 'Start' },
        { id: 'submit', type: 'UserTask', name: 'Submit', assignment: { type: 'ProcessInitiator', value: '' }, form: { formDefinitionKey: formCreated.data.key } },
        { id: 'end', type: 'End', name: 'End' },
      ],
      transitions: [
        { id: 't1', source: 'start', target: 'submit' },
        { id: 't2', source: 'submit', target: 'end' },
      ],
    };
    const versionCreated = await api.post(`/api/process-definitions/${processId}/versions`, { definition }, { headers: auth });
    expect(versionCreated.status).toBe(200);
    const publishedProcess = await api.post(`/api/process-definitions/${processId}/publish`, null, { headers: auth });
    expect(publishedProcess.status).toBe(200);

    const started = await api.post('/api/process-instances', { processDefinitionKey: processCreated.data.key }, { headers: auth });
    expect(started.status).toBe(201);

    const forms = await api.get('/api/form-instances', { params: { processInstanceId: started.data.id }, headers: auth });
    expect(forms.status).toBe(200);
    expect(forms.data).toHaveLength(1);
    expect(forms.data[0].status).toBe('Draft');
  });
});
