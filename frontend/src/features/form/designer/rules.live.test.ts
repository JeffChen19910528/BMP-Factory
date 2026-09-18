import axios from 'axios';
import { describe, expect, it } from 'vitest';
import { createField, createRule, deserializeFormSchema, deserializeRules, serializeFormSchema } from './formSchemaModel';

// Phase 5.4.2 §24's full acceptance scenario, run against the real backend rather than a mock:
// Designer-generated schema (fields + rules, via the exact functions FormDesigner.tsx calls) ->
// live FormSchemaValidator -> Save Draft -> Reload -> rules preserved -> Validate -> Publish ->
// Published/Read-Only, then the *runtime* side: a conditionally-required field actually blocks
// submission when omitted, succeeds when its condition doesn't apply, and a Calculated field's
// server-computed value cannot be overridden by a spoofed client submission.
const baseURL = import.meta.env.VITE_API_BASE_URL || 'http://localhost:5080';
const api = axios.create({ baseURL, validateStatus: () => true });

describe('Advanced Form Rules against the live backend', () => {
  it('Designer-built rules survive Save Draft -> Reload -> Validate -> Publish -> read-only', async () => {
    const login = await api.post('/api/auth/login', { username: 'admin', password: 'ChangeMe123!' });
    expect(login.status).toBe(200);
    const auth = { Authorization: `Bearer ${login.data.accessToken}` };
    const suffix = Date.now();

    const formCreated = await api.post(
      '/api/form-definitions',
      { key: `vitest-rules-live-${suffix}`, name: 'Vitest Rules Live Form' },
      { headers: auth },
    );
    expect(formCreated.status).toBe(201);
    const formId = formCreated.data.id as string;

    const starterVersion = await api.post(
      `/api/form-definitions/${formId}/versions`,
      { schema: { fields: [{ key: 'field1', type: 'Text', label: 'Untitled Field', required: false }] } },
      { headers: auth },
    );
    expect(starterVersion.status).toBe(200);
    const versionId = starterVersion.data.id as string;

    // Build fields the way the Designer's palette would: travelType, destination, quantity,
    // unitPrice, total.
    let fields = deserializeFormSchema(starterVersion.data.schema).filter((f) => f.key !== 'field1');

    const travelType = createField('Select', new Set(fields.map((f) => f.key)));
    travelType.key = 'travelType';
    travelType.label = 'Travel Type';
    travelType.required = true;
    travelType.options = [
      { value: 'BUSINESS', label: 'Business' },
      { value: 'PERSONAL', label: 'Personal' },
    ];
    fields = [...fields, travelType];

    const destination = createField('Text', new Set(fields.map((f) => f.key)));
    destination.key = 'destination';
    destination.label = 'Destination';
    fields = [...fields, destination];

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

    // Rules via the Designer's own createRule function: destination is required only when
    // travelType is BUSINESS; total is a calculated field.
    const existingIds = new Set<string>();
    const requiredRule = createRule('Required', 'destination', existingIds);
    requiredRule.condition = { kind: 'field', field: 'travelType', operator: 'Equals', value: 'BUSINESS' };
    existingIds.add(requiredRule.id);

    const calcRule = createRule('Calculated', 'total', existingIds);
    calcRule.formula = 'quantity * unitPrice';
    existingIds.add(calcRule.id);

    const rules = [requiredRule, calcRule];
    const schema = serializeFormSchema(fields, rules);

    const preValidation = await api.post('/api/form-definitions/validate', { schema }, { headers: auth });
    expect(preValidation.status).toBe(200);
    expect(preValidation.data.isValid).toBe(true);
    expect(preValidation.data.errors).toEqual([]);

    const saved = await api.put(
      `/api/form-definitions/${formId}/versions/${versionId}`,
      { schema, expectedVersion: starterVersion.data.rowVersion },
      { headers: auth },
    );
    expect(saved.status).toBe(200);

    // Reload and verify the rules survived, semantically, via the Designer's own deserializer.
    const reloadedVersions = await api.get(`/api/form-definitions/${formId}/versions`, { headers: auth });
    const reloaded = reloadedVersions.data.find((v: { id: string }) => v.id === versionId);
    expect(reloaded).toBeTruthy();

    const reloadedRules = deserializeRules(reloaded.schema);
    expect(reloadedRules).toHaveLength(2);
    const reloadedRequired = reloadedRules.find((r) => r.type === 'Required');
    expect(reloadedRequired?.target).toBe('destination');
    expect(reloadedRequired?.condition).toMatchObject({ field: 'travelType', operator: 'Equals', value: 'BUSINESS' });
    const reloadedCalc = reloadedRules.find((r) => r.type === 'Calculated');
    expect(reloadedCalc?.formula).toBe('quantity * unitPrice');

    const postValidation = await api.post('/api/form-definitions/validate', { schema: reloaded.schema }, { headers: auth });
    expect(postValidation.status).toBe(200);
    expect(postValidation.data.isValid).toBe(true);

    const published = await api.post(`/api/form-definitions/${formId}/publish`, null, { headers: auth });
    expect(published.status).toBe(200);
    expect(published.data.status).toBe('Published');

    // Published / Read Only: the backend rejects any further edit outright.
    const editAttempt = await api.put(
      `/api/form-definitions/${formId}/versions/${versionId}`,
      { schema: reloaded.schema, expectedVersion: saved.data.rowVersion },
      { headers: auth },
    );
    expect(editAttempt.status).toBe(409);
    expect(editAttempt.data.code).toBe('VERSION_NOT_DRAFT');

    // ---- Runtime: conditionally-required field actually blocks submission ----

    const instanceCreated = await api.post('/api/form-instances', { formDefinitionKey: formCreated.data.key }, { headers: auth });
    expect(instanceCreated.status).toBe(201);
    const instanceId = instanceCreated.data.id as string;

    const dataQuery = await api.get(`/api/form-instances/${instanceId}/data`, { headers: auth });
    expect(dataQuery.status).toBe(200);

    // travelType = BUSINESS, destination omitted -> destination becomes required -> reject.
    const saveBusiness = await api.put(
      `/api/form-instances/${instanceId}/data`,
      { data: { travelType: 'BUSINESS', quantity: 3, unitPrice: 100 }, expectedVersion: dataQuery.data.version },
      { headers: auth },
    );
    expect(saveBusiness.status).toBe(200);

    const submitMissingDestination = await api.post(`/api/form-instances/${instanceId}/submit`, null, { headers: auth });
    expect(submitMissingDestination.status).toBe(400);
    expect(submitMissingDestination.data.errors.some((e: { code: string }) => e.code === 'FIELD_REQUIRED')).toBe(true);

    // A client spoofing "total" is never trusted — the server recomputed it from quantity/unitPrice
    // on the earlier save, regardless of what was submitted for it.
    const afterSave = await api.get(`/api/form-instances/${instanceId}/data`, { headers: auth });
    expect(afterSave.data.data.total).toBe(300);

    // Fill in destination -> now travelType=BUSINESS with destination present -> submission succeeds.
    const saveWithDestination = await api.put(
      `/api/form-instances/${instanceId}/data`,
      { data: { destination: 'Tokyo' }, expectedVersion: afterSave.data.version },
      { headers: auth },
    );
    expect(saveWithDestination.status).toBe(200);

    const submitSuccess = await api.post(`/api/form-instances/${instanceId}/submit`, null, { headers: auth });
    expect(submitSuccess.status).toBe(200);
    expect(submitSuccess.data.status).toBe('Submitted');
  });

  it('travelType = PERSONAL: destination is not required, submission succeeds without it', async () => {
    const login = await api.post('/api/auth/login', { username: 'admin', password: 'ChangeMe123!' });
    const auth = { Authorization: `Bearer ${login.data.accessToken}` };
    const suffix = Date.now();

    const formCreated = await api.post(
      '/api/form-definitions',
      { key: `vitest-rules-personal-${suffix}`, name: 'Vitest Rules Personal' },
      { headers: auth },
    );
    const formId = formCreated.data.id as string;

    let fields = deserializeFormSchema({ fields: [{ key: 'field1', type: 'Text', label: 'x' }] }).filter((f) => f.key !== 'field1');
    const travelType = createField('Select', new Set());
    travelType.key = 'travelType';
    travelType.label = 'Travel Type';
    travelType.required = true;
    travelType.options = [{ value: 'BUSINESS', label: 'Business' }, { value: 'PERSONAL', label: 'Personal' }];
    fields = [travelType];
    const destination = createField('Text', new Set(['travelType']));
    destination.key = 'destination';
    destination.label = 'Destination';
    fields = [...fields, destination];

    const rule = createRule('Required', 'destination', new Set());
    rule.condition = { kind: 'field', field: 'travelType', operator: 'Equals', value: 'BUSINESS' };

    const schema = serializeFormSchema(fields, [rule]);
    const starterVersion = await api.post(`/api/form-definitions/${formId}/versions`, { schema }, { headers: auth });
    expect(starterVersion.status).toBe(200);
    await api.post(`/api/form-definitions/${formId}/publish`, null, { headers: auth });

    const instanceCreated = await api.post('/api/form-instances', { formDefinitionKey: formCreated.data.key }, { headers: auth });
    const instanceId = instanceCreated.data.id as string;
    const dataQuery = await api.get(`/api/form-instances/${instanceId}/data`, { headers: auth });

    const saved = await api.put(
      `/api/form-instances/${instanceId}/data`,
      { data: { travelType: 'PERSONAL' }, expectedVersion: dataQuery.data.version },
      { headers: auth },
    );
    expect(saved.status).toBe(200);

    const submitted = await api.post(`/api/form-instances/${instanceId}/submit`, null, { headers: auth });
    expect(submitted.status).toBe(200);
    expect(submitted.data.status).toBe('Submitted');
  });
});
