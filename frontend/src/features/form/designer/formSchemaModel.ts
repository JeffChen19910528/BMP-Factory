import type {
  FormFieldDefinition,
  FormFieldOption,
  FormFieldType,
  FormFieldValidation,
  FormRule,
  FormRuleType,
  FormSchema,
} from '../../../types/form';

// The Form Designer's field palette (Phase 5.4.1 §5) — exactly the field types
// FormSchemaValidator.SupportedFieldTypes already enforces. Everything else (RichText, Table,
// Signature, Formula, Computed) is a *reserved* FormFieldType the backend enum already knows
// about but can't render/validate yet — a field of one of those types is preserved read-only,
// never edited or discarded here (mirrors designer/graphModel.ts's SUPPORTED_NODE_TYPES pattern
// in the Process Designer exactly).
export const SUPPORTED_FIELD_TYPES: readonly FormFieldType[] = [
  'Text',
  'Textarea',
  'Number',
  'Currency',
  'Date',
  'DateTime',
  'Select',
  'Radio',
  'Checkbox',
  'User',
  'Department',
  'File',
];

export function isSupportedFieldType(type: FormFieldType): boolean {
  return (SUPPORTED_FIELD_TYPES as readonly string[]).includes(type);
}

export const OPTION_BASED_TYPES: readonly FormFieldType[] = ['Select', 'Radio'];
export const NUMERIC_TYPES: readonly FormFieldType[] = ['Number', 'Currency'];
export const TEXT_LIKE_TYPES: readonly FormFieldType[] = ['Text', 'Textarea'];

// The Designer's per-field model. `internalId` is a stable React list identity that's completely
// independent of `key` — editing the key (a real, backend-meaningful rename) must never remount
// the row or lose selection, and reordering must never touch `key` either (frontend spec §6:
// "Field keys must remain stable when the field is reordered"). This is the same "designer id vs.
// semantic id" split the Process Designer's graphModel.ts uses for nodes, except there the two
// happen to be the same value (a WorkflowNodeDefinition's `id` is never independently
// user-renamed) — here they're deliberately different because a field's `key` *is* meant to be
// editable.
export interface DesignerField extends Record<string, unknown> {
  internalId: string;
  key: string;
  type: FormFieldType;
  label: string;
  description: string | null;
  required: boolean;
  defaultValue: string | null;
  placeholder: string | null;
  options: FormFieldOption[] | null;
  validation: FormFieldValidation | null;
  readOnly: boolean;
  supported: boolean;
  // Original definition, round-tripped verbatim for unsupported types; also carries forward
  // `visibility` (Skill.md's declarative show/hide condition) for *supported* fields too, since
  // this iteration has no UI for editing it — preserved rather than silently dropped.
  raw: FormFieldDefinition;
}

function newInternalId(): string {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : `field-${Math.random().toString(36).slice(2)}`;
}

export function defaultLabelFor(type: FormFieldType): string {
  switch (type) {
    case 'Text':
      return 'Text Field';
    case 'Textarea':
      return 'Textarea Field';
    case 'Number':
      return 'Number Field';
    case 'Currency':
      return 'Currency Field';
    case 'Date':
      return 'Date Field';
    case 'DateTime':
      return 'Date & Time Field';
    case 'Select':
      return 'Select Field';
    case 'Radio':
      return 'Radio Field';
    case 'Checkbox':
      return 'Checkbox Field';
    case 'User':
      return 'User Field';
    case 'Department':
      return 'Department Field';
    case 'File':
      return 'File Field';
    default:
      return type;
  }
}

// Generates a fresh, unique, safe field key — never an array index (frontend spec §6). Keeps
// trying "field1", "field2", ... until one doesn't collide with `existingKeys`, so adding several
// fields of the same type in a row never produces a duplicate the backend would reject.
export function generateUniqueFieldKey(existingKeys: ReadonlySet<string>, hint = 'field'): string {
  let n = 1;
  let candidate = `${hint}${n}`;
  while (existingKeys.has(candidate)) {
    n += 1;
    candidate = `${hint}${n}`;
  }
  return candidate;
}

export function createField(type: (typeof SUPPORTED_FIELD_TYPES)[number], existingKeys: ReadonlySet<string>): DesignerField {
  const key = generateUniqueFieldKey(existingKeys);
  const raw: FormFieldDefinition = { key, type, label: defaultLabelFor(type), required: false };
  return {
    internalId: newInternalId(),
    key,
    type,
    label: raw.label,
    description: null,
    required: false,
    defaultValue: null,
    placeholder: null,
    options: OPTION_BASED_TYPES.includes(type) ? [{ value: 'option1', label: 'Option 1' }] : null,
    validation: null,
    readOnly: false,
    supported: true,
    raw,
  };
}

// ---- Deserialize: backend FormSchema JSON -> Designer model ----

export function deserializeFormSchema(schema: FormSchema): DesignerField[] {
  return schema.fields.map((field) => ({
    internalId: newInternalId(),
    key: field.key,
    type: field.type,
    label: field.label,
    description: field.description ?? null,
    required: field.required ?? false,
    defaultValue: field.defaultValue ?? null,
    placeholder: field.placeholder ?? null,
    options: field.options ?? null,
    validation: field.validation ?? null,
    readOnly: field.readOnly ?? false,
    supported: isSupportedFieldType(field.type),
    raw: field,
  }));
}

// ---- Rules (Phase 5.4.2) ----

// The four rule types this Designer's Rule Builder actually understands/authors. A rule whose
// `type` is anything else (a future phase's addition) is kept exactly as `deserializeRules` found
// it and is never edited, deleted, or re-typed by this UI — same "preserve, don't destroy"
// treatment SUPPORTED_FIELD_TYPES gives an unrecognized field type.
export const SUPPORTED_RULE_TYPES: readonly FormRuleType[] = ['Visibility', 'Enabled', 'Required', 'Calculated'];

export function isSupportedRuleType(type: FormRuleType): boolean {
  return (SUPPORTED_RULE_TYPES as readonly string[]).includes(type);
}

export function generateUniqueRuleId(existingIds: ReadonlySet<string>, hint = 'rule'): string {
  let n = 1;
  let candidate = `${hint}${n}`;
  while (existingIds.has(candidate)) {
    n += 1;
    candidate = `${hint}${n}`;
  }
  return candidate;
}

export function createRule(type: FormRuleType, target: string, existingIds: ReadonlySet<string>): FormRule {
  const id = generateUniqueRuleId(existingIds);
  if (type === 'Calculated') {
    return { id, target, type, formula: '' };
  }
  return { id, target, type, condition: { kind: 'field', field: '', operator: 'Equals', value: '' } };
}

// Rules need no field-style internalId/raw split: an unrecognized rule's extra JSON properties
// already ride along on the plain object (FormRule's index signature), and nothing else in the UI
// references a rule by anything other than its own `id` — the same value used for React list keys
// and for the serialized `id` field, since a rule's id (unlike a field's key) is never user-edited.
export function deserializeRules(schema: FormSchema): FormRule[] {
  return schema.rules ? schema.rules.map((r) => ({ ...r })) : [];
}

// ---- Serialize: Designer model -> backend FormSchema JSON ----

export function serializeFormSchema(fields: DesignerField[], rules?: FormRule[]): FormSchema {
  return {
    fields: fields.map((field): FormFieldDefinition => {
      if (!field.supported) {
        // Round-trip verbatim — the designer doesn't understand this field type well enough to
        // safely re-derive it from edited properties (frontend spec §13).
        return field.raw;
      }
      return {
        key: field.key,
        type: field.type,
        label: field.label,
        description: field.description,
        required: field.required,
        defaultValue: field.defaultValue,
        placeholder: field.placeholder,
        options: OPTION_BASED_TYPES.includes(field.type) ? field.options : undefined,
        validation: field.validation,
        // Preserved untouched — no UI edits it this iteration (frontend spec §27 excludes
        // conditional-logic authoring; the field can still carry a rule set some other way, e.g.
        // the JSON editor, without this designer clobbering it on save).
        visibility: field.raw.visibility,
        readOnly: field.readOnly,
      };
    }),
    rules: rules && rules.length > 0 ? rules : undefined,
  };
}
