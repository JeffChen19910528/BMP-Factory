import { useEffect, useMemo, useState } from 'react';
import { EyeOutlined, FormOutlined, RedoOutlined, UndoOutlined } from '@ant-design/icons';
import { Button, Layout, Space, Tag } from 'antd';
import type { FormFieldType, FormRule, FormSchema, WorkflowValidationResult } from '../../../types/form';
import { useTranslation } from '../../../i18n/LanguageContext';
import { FieldPalette } from './FieldPalette';
import { FormCanvas } from './FormCanvas';
import { FieldPreview } from './FieldPreview';
import { PropertiesPanel } from './PropertiesPanel';
import { ValidationPanel, resolveValidationErrors } from './ValidationPanel';
import { createField, deserializeFormSchema, deserializeRules, serializeFormSchema, type DesignerField } from './formSchemaModel';
import { evaluateForm, ruleReferencesField } from './ruleEngine';
import { useHistory } from './useHistory';

// Combined undo/redo unit (Phase 5.4.2): fields and rules are edited together so Undo/Redo covers
// Add/Edit/Delete Rule exactly the same way it already covered field changes — one history, not
// two independently-stepping ones that could desync.
interface DesignerState {
  fields: DesignerField[];
  rules: FormRule[];
}

const { Sider, Content } = Layout;

interface FormDesignerProps {
  initialSchema: FormSchema;
  readOnly: boolean;
  formName?: string;
  // Fired after every committed edit with the freshly re-serialized schema and whether it differs
  // from `initialSchema` — mirrors ProcessDesigner's onChange contract exactly (the parent page
  // owns Save/Validate/Publish network calls; this component only reports state).
  onChange: (schema: FormSchema, dirty: boolean) => void;
  onSaveDraft: () => void;
  isSaving?: boolean;
  onValidate: () => void;
  isValidating?: boolean;
  validationResult: WorkflowValidationResult | null;
  onPublish: () => void;
  isPublishing?: boolean;
}

export function FormDesigner({
  initialSchema,
  readOnly,
  formName,
  onChange,
  onSaveDraft,
  isSaving,
  onValidate,
  isValidating,
  validationResult,
  onPublish,
  isPublishing,
}: FormDesignerProps) {
  const { t } = useTranslation();
  // Computed only once, at mount — same reasoning as ProcessDesigner.tsx's identical comment: the
  // parent echoes every onChange back into the prop it passes as `initialSchema`, so tracking it
  // reactively would make the dirty baseline chase the live state and `dirty` would always read
  // false after the first edit. A fresh baseline only happens on an explicit remount (the parent
  // bumps a `key`), not from this prop changing on its own.
  const [initialState] = useState<DesignerState>(() => ({
    fields: deserializeFormSchema(initialSchema),
    rules: deserializeRules(initialSchema),
  }));
  const [savedJson] = useState(() => JSON.stringify(serializeFormSchema(initialState.fields, initialState.rules)));

  const history = useHistory<DesignerState>(initialState);
  const { present, commit, undo, redo, canUndo, canRedo } = history;
  const { fields, rules } = present;
  const [selectedFieldId, setSelectedFieldId] = useState<string | null>(null);
  const [mode, setMode] = useState<'designer' | 'preview'>('designer');

  const currentSchema = useMemo(() => serializeFormSchema(fields, rules), [fields, rules]);
  const currentJson = useMemo(() => JSON.stringify(currentSchema), [currentSchema]);
  const dirty = currentJson !== savedJson;

  useEffect(() => {
    onChange(currentSchema, dirty);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [currentJson, dirty]);

  const resolvedErrors = useMemo(() => resolveValidationErrors(validationResult, fields), [validationResult, fields]);
  const mentionedFieldKeys = useMemo(
    () => new Set(resolvedErrors.map((e) => e.field?.key).filter((k): k is string => !!k)),
    [resolvedErrors],
  );

  const selectedField = fields.find((f) => f.internalId === selectedFieldId) ?? null;
  const existingKeys = new Set(fields.filter((f) => f.internalId !== selectedFieldId).map((f) => f.key));

  function handleAddField(type: FormFieldType) {
    const keys = new Set(fields.map((f) => f.key));
    const field = createField(type, keys);
    commit({ fields: [...fields, field], rules });
    setSelectedFieldId(field.internalId);
  }

  function handleMoveField(internalId: string, direction: 'up' | 'down') {
    const index = fields.findIndex((f) => f.internalId === internalId);
    if (index === -1) return;
    const swapWith = direction === 'up' ? index - 1 : index + 1;
    if (swapWith < 0 || swapWith >= fields.length) return;
    const next = fields.slice();
    [next[index], next[swapWith]] = [next[swapWith], next[index]];
    commit({ fields: next, rules });
  }

  function handleDeleteField(internalId: string) {
    const deletedKey = fields.find((f) => f.internalId === internalId)?.key;
    // Deleting a field also drops any rule that targets it or whose condition/formula references
    // it — leaving a dangling reference around would just resurface as a confusing validation
    // error the user has no obvious way to trace back to "I deleted a field."
    const survivingRules = deletedKey
      ? rules.filter((r) => r.target !== deletedKey && !ruleReferencesField(r, deletedKey))
      : rules;
    commit({ fields: fields.filter((f) => f.internalId !== internalId), rules: survivingRules });
    if (selectedFieldId === internalId) setSelectedFieldId(null);
  }

  function handleDuplicateField(internalId: string) {
    const source = fields.find((f) => f.internalId === internalId);
    if (!source) return;
    const keys = new Set(fields.map((f) => f.key));
    const duplicate = createField(source.type, keys);
    const cloned: DesignerField = {
      ...source,
      internalId: duplicate.internalId,
      key: duplicate.key,
      raw: { ...source.raw, key: duplicate.key },
    };
    const index = fields.findIndex((f) => f.internalId === internalId);
    const next = fields.slice();
    next.splice(index + 1, 0, cloned);
    commit({ fields: next, rules });
    setSelectedFieldId(cloned.internalId);
  }

  function handleFieldPropertyChange(internalId: string, partial: Partial<DesignerField>) {
    commit({ fields: fields.map((f) => (f.internalId === internalId ? { ...f, ...partial } : f)), rules });
  }

  function handleRulesChange(nextRules: FormRule[]) {
    commit({ fields, rules: nextRules });
  }

  const fieldCount = fields.length;

  return (
    <Layout style={{ background: '#fff', border: '1px solid #f0f0f0', borderRadius: 8, overflow: 'hidden' }}>
      <div
        style={{
          padding: '8px 16px',
          borderBottom: '1px solid #f0f0f0',
          display: 'flex',
          justifyContent: 'space-between',
          alignItems: 'center',
          flexWrap: 'wrap',
          gap: 8,
        }}
      >
        <Space wrap>
          {!readOnly && (
            <>
              <Button type="primary" onClick={onSaveDraft} loading={isSaving}>
                {t('common.saveDraft')}
              </Button>
              <Button onClick={onValidate} loading={isValidating}>
                {t('processes.validate')}
              </Button>
            </>
          )}
          <Button
            icon={mode === 'designer' ? <EyeOutlined /> : <FormOutlined />}
            onClick={() => setMode(mode === 'designer' ? 'preview' : 'designer')}
          >
            {mode === 'designer' ? t('forms.preview') : t('forms.backToDesigner')}
          </Button>
          {!readOnly && (
            <>
              <Button onClick={onPublish} loading={isPublishing}>
                {t('common.publish')}
              </Button>
              <Button icon={<UndoOutlined />} onClick={undo} disabled={!canUndo || mode === 'preview'}>
                {t('forms.undo')}
              </Button>
              <Button icon={<RedoOutlined />} onClick={redo} disabled={!canRedo || mode === 'preview'}>
                {t('forms.redo')}
              </Button>
            </>
          )}
          {dirty && !readOnly && <Tag color="gold">{t('forms.unsavedChanges')}</Tag>}
          {readOnly && <Tag color="green">{t('forms.publishedReadOnlyTag')}</Tag>}
        </Space>
        <Tag color={fieldCount > 0 ? 'default' : 'error'}>{fieldCount} {fieldCount === 1 ? t('forms.fieldSingular') : t('forms.fieldPlural')}</Tag>
      </div>

      {mode === 'designer' && <ValidationPanel result={validationResult} fields={fields} onSelectField={(key) => {
        const target = fields.find((f) => f.key === key);
        if (target) setSelectedFieldId(target.internalId);
      }} />}

      {mode === 'preview' ? (
        <div style={{ padding: 24 }}>
          <PreviewForm formName={formName} schema={currentSchema} />
        </div>
      ) : (
        <Layout style={{ background: '#fff', minHeight: 520 }}>
          {!readOnly && (
            <Sider width={180} theme="light" style={{ borderInlineEnd: '1px solid #f0f0f0' }}>
              <FieldPalette onAddField={handleAddField} />
            </Sider>
          )}
          <Content>
            <FormCanvas
              fields={fields}
              selectedFieldId={selectedFieldId}
              readOnly={readOnly}
              title={formName}
              mentionedFieldKeys={mentionedFieldKeys}
              onSelectField={setSelectedFieldId}
              onMoveField={handleMoveField}
              onDeleteField={handleDeleteField}
              onDuplicateField={handleDuplicateField}
              onDropFieldType={handleAddField}
            />
          </Content>
          <Sider width={300} theme="light" style={{ borderInlineStart: '1px solid #f0f0f0' }}>
            <PropertiesPanel
              field={selectedField}
              allFields={fields}
              existingKeys={existingKeys}
              readOnly={readOnly}
              onChange={handleFieldPropertyChange}
              onDelete={handleDeleteField}
              rules={rules}
              onRulesChange={handleRulesChange}
            />
          </Sider>
        </Layout>
      )}
    </Layout>
  );
}

// A basic, read-only-of-configuration but interactively-fillable rendering of the whole form
// (frontend spec §17) — reuses FieldPreview in `interactive` mode field by field. Phase 5.4.2:
// Preview now actually evaluates rules (visibility/enabled/required/calculated), live, using the
// exact same evaluateForm implementation a future runtime form renderer would use (§17: "avoid
// duplicating rule behavior between Designer Preview and Runtime Form") — every keystroke
// re-evaluates the whole schema against the values entered so far, matching how the backend will
// eventually re-evaluate the same rules at submission time (though this evaluation itself is only
// ever UX, never authoritative — see ruleEngine.ts's own header comment).
function PreviewForm({ formName, schema }: { formName?: string; schema: FormSchema }) {
  const { t } = useTranslation();
  const [values, setValues] = useState<Record<string, unknown>>({});
  const state = useMemo(() => evaluateForm(schema, values), [schema, values]);

  return (
    <div style={{ maxWidth: 480 }}>
      {formName && <h3 style={{ marginTop: 0 }}>{formName}</h3>}
      <Space direction="vertical" style={{ width: '100%' }} size="large">
        {schema.fields.map((field) => {
          const fieldState = state.fields[field.key];
          if (fieldState && !fieldState.visible) return null;
          const designerField: DesignerField = {
            internalId: field.key,
            key: field.key,
            type: field.type,
            label: field.label,
            description: field.description ?? null,
            required: fieldState?.required ?? !!field.required,
            defaultValue: field.defaultValue ?? null,
            placeholder: field.placeholder ?? null,
            options: field.options ?? null,
            validation: field.validation ?? null,
            readOnly: field.readOnly ?? false,
            supported: true,
            raw: field,
          };
          const isCalculated = (schema.rules ?? []).some((r) => r.type === 'Calculated' && r.target === field.key);
          return (
            <div key={field.key}>
              {field.type !== 'Checkbox' && (
                <div style={{ marginBottom: 4 }}>
                  <strong>{field.label}</strong>
                  {fieldState?.required && <span style={{ color: '#ff4d4f' }}> *</span>}
                </div>
              )}
              <FieldPreview
                field={designerField}
                interactive
                ruleDisabled={(fieldState ? !fieldState.enabled : false) || isCalculated}
                controlledValue={isCalculated ? state.values[field.key] : values[field.key]}
                onControlledChange={(v) => {
                  if (!isCalculated) setValues((prev) => ({ ...prev, [field.key]: v }));
                }}
              />
              {state.calculationErrors[field.key] && (
                <div style={{ fontSize: 12, color: '#ff4d4f', marginTop: 4 }}>{t('forms.cannotCalculate')}: {state.calculationErrors[field.key]}</div>
              )}
              {field.description && <div style={{ fontSize: 12, color: 'rgba(0,0,0,0.45)', marginTop: 4 }}>{field.description}</div>}
            </div>
          );
        })}
      </Space>
    </div>
  );
}
