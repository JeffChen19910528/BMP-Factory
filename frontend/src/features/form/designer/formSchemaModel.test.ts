import { describe, expect, it } from 'vitest';
import type { FormSchema } from '../../../types/form';
import { createField, deserializeFormSchema, generateUniqueFieldKey, serializeFormSchema } from './formSchemaModel';

// The most important test in this iteration (frontend spec §12): a form authored visually must
// produce backend-compatible JSON without losing semantic information. Compared by content
// (field order + values), not raw JSON string equality, though for this model field *order* is
// itself semantically meaningful (frontend spec §12: "array ordering should be preserved where
// ordering is semantically meaningful" — a form's fields render top-to-bottom in that exact
// order) so, unlike the Process Designer's node/transition round-trip test, this one *does* check
// order — deliberately, not by oversight.
const REAL_WORLD_SCHEMA: FormSchema = {
  fields: [
    { key: 'itemName', type: 'Text', label: 'Item Name', required: true, placeholder: 'e.g. Laptop', validation: { minLength: 1, maxLength: 100 } },
    { key: 'quantity', type: 'Number', label: 'Quantity', required: true, validation: { minValue: 1, maxValue: 1000 } },
    { key: 'category', type: 'Select', label: 'Category', required: true, options: [{ value: 'hw', label: 'Hardware' }, { value: 'sw', label: 'Software' }] },
    { key: 'department', type: 'Department', label: 'Department', required: false },
    {
      key: 'approved',
      type: 'Checkbox',
      label: 'Pre-approved',
      required: false,
      visibility: { when: { field: 'category', operator: 'Equals', value: 'hw' } },
    },
  ],
};

// Normalizes for comparison against hand-authored fixtures: drops `null`s (the model always
// round-trips optional fields as explicit `null`, fixtures often omit them) and defaults a
// missing `readOnly` to `false` (the model's own default for that field), since neither
// difference is semantically meaningful — the backend treats an absent key and an explicit
// default identically.
function dropNullFields<T extends object>(value: T): T {
  return JSON.parse(
    JSON.stringify(value, (key, v) => {
      if (v === null) return undefined;
      if (key === 'readOnly' && v === false) return undefined;
      return v;
    }),
  );
}

describe('deserializeFormSchema / serializeFormSchema round trip', () => {
  it('preserves field keys, types, labels, options, validation, and order', () => {
    const fields = deserializeFormSchema(REAL_WORLD_SCHEMA);
    const roundTripped = serializeFormSchema(fields);

    expect(dropNullFields(roundTripped)).toEqual(dropNullFields(REAL_WORLD_SCHEMA));
  });

  it('preserves the Visibility condition even though no UI edits it this iteration', () => {
    const fields = deserializeFormSchema(REAL_WORLD_SCHEMA);
    const roundTripped = serializeFormSchema(fields);

    expect(roundTripped.fields.find((f) => f.key === 'approved')?.visibility).toEqual({
      when: { field: 'category', operator: 'Equals', value: 'hw' },
    });
  });

  it('assigns every deserialized field a stable internalId distinct from its key', () => {
    const fields = deserializeFormSchema(REAL_WORLD_SCHEMA);
    const internalIds = new Set(fields.map((f) => f.internalId));
    expect(internalIds.size).toBe(fields.length);
    for (const field of fields) {
      expect(field.internalId).not.toBe(field.key);
    }
  });

  it('preserves an unsupported field type read-only instead of discarding it', () => {
    const withSignature: FormSchema = {
      fields: [
        { key: 'itemName', type: 'Text', label: 'Item Name', required: true },
        { key: 'sig', type: 'Signature', label: 'Signature' },
      ],
    };

    const fields = deserializeFormSchema(withSignature);
    const sigField = fields.find((f) => f.key === 'sig')!;
    expect(sigField.supported).toBe(false);
    expect(sigField.type).toBe('Signature');

    const roundTripped = serializeFormSchema(fields);
    expect(dropNullFields(roundTripped)).toEqual(dropNullFields(withSignature));
  });

  it('handles an empty schema without crashing', () => {
    const empty: FormSchema = { fields: [] };
    const fields = deserializeFormSchema(empty);
    expect(fields).toHaveLength(0);
    expect(serializeFormSchema(fields)).toEqual(empty);
  });

  it('is semantically stable when reordered — reordering changes array position only, never a key', () => {
    const fields = deserializeFormSchema(REAL_WORLD_SCHEMA);
    const reordered = [fields[1], fields[0], ...fields.slice(2)];
    const roundTripped = serializeFormSchema(reordered);

    expect(roundTripped.fields.map((f) => f.key)).toEqual(['quantity', 'itemName', 'category', 'department', 'approved']);
    // Every field's own content is still exactly what it was — only position moved.
    expect(dropNullFields(roundTripped.fields[1])).toEqual(dropNullFields(REAL_WORLD_SCHEMA.fields[0]));
  });
});

describe('createField / generateUniqueFieldKey', () => {
  it('never uses an array index as a field key', () => {
    const first = createField('Text', new Set());
    const second = createField('Text', new Set([first.key]));
    expect(first.key).not.toBe('0');
    expect(second.key).not.toBe('1');
    expect(first.key).not.toBe(second.key);
  });

  it('generates a fresh key that never collides with existing ones', () => {
    const existing = new Set(['field1', 'field2', 'field3']);
    const key = generateUniqueFieldKey(existing);
    expect(existing.has(key)).toBe(false);
    expect(key).toBe('field4');
  });

  it('gives option-based field types a starter option so Select/Radio are never published empty', () => {
    const select = createField('Select', new Set());
    expect(select.options).toHaveLength(1);
    const text = createField('Text', new Set());
    expect(text.options).toBeNull();
  });
});
