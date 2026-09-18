import { DeleteOutlined, PlusOutlined } from '@ant-design/icons';
import { Alert, Button, Divider, Input, Select, Space, Typography } from 'antd';
import type {
  FormCompoundCondition,
  FormCondition,
  FormFieldCondition,
  FormFieldType,
  FormLogicalOperator,
  FormRule,
  FormRuleType,
} from '../../../types/form';
import { useTranslation } from '../../../i18n/LanguageContext';
import type { DesignerField } from './formSchemaModel';
import { createRule, isSupportedRuleType, SUPPORTED_RULE_TYPES } from './formSchemaModel';
import { operatorsForFieldType, parseFormula } from './ruleEngine';

// Display labels only — the FormRuleType/operator values themselves are never localized; they are
// sent to the backend/stored in FormSchema JSON exactly as the engine defines them.
const RULE_TYPE_LABEL_KEYS: Record<FormRuleType, string> = {
  Visibility: 'forms.ruleType.Visibility',
  Enabled: 'forms.ruleType.Enabled',
  Required: 'forms.ruleType.Required',
  Calculated: 'forms.ruleType.Calculated',
  Unknown: 'forms.ruleType.Unknown',
};

const OPERATOR_LABEL_KEYS: Record<string, string> = {
  Equals: 'forms.operator.Equals',
  NotEquals: 'forms.operator.NotEquals',
  Contains: 'forms.operator.Contains',
  NotContains: 'forms.operator.NotContains',
  GreaterThan: 'forms.operator.GreaterThan',
  GreaterThanOrEqual: 'forms.operator.GreaterThanOrEqual',
  LessThan: 'forms.operator.LessThan',
  LessThanOrEqual: 'forms.operator.LessThanOrEqual',
  IsEmpty: 'forms.operator.IsEmpty',
  IsNotEmpty: 'forms.operator.IsNotEmpty',
};

function emptyLeaf(): FormFieldCondition {
  return { kind: 'field', field: '', operator: 'Equals', value: '' };
}

interface RuleBuilderProps {
  field: DesignerField;
  allFields: DesignerField[];
  rules: FormRule[];
  readOnly: boolean;
  onChange: (rules: FormRule[]) => void;
}

// The Rule Builder, integrated directly into the Properties Panel (Phase 5.4.2 §7: "Do not create
// a separate page for rules") — shows only the rules whose `target` is the currently-selected
// field. Deliberately supports authoring a single leaf condition or one flat AND/OR group of leaf
// conditions per rule (not an arbitrarily nested tree) — the engine and FormRuleValidator both
// support full recursive nesting (§6), and the Advanced JSON editor remains the way to author a
// deeper structure; this keeps the visual builder itself simple and reliably testable, matching
// how FormCanvas chose explicit Move Up/Down over native drag-to-reorder for the same reason.
export function RuleBuilder({ field, allFields, rules, readOnly, onChange }: RuleBuilderProps) {
  const { t } = useTranslation();
  const targetRules = rules.filter((r) => r.target === field.key);
  const existingIds = new Set(rules.map((r) => r.id));

  function updateRule(id: string, next: FormRule) {
    onChange(rules.map((r) => (r.id === id ? next : r)));
  }

  function deleteRule(id: string) {
    onChange(rules.filter((r) => r.id !== id));
  }

  function addRule() {
    const rule = createRule('Visibility', field.key, existingIds);
    onChange([...rules, rule]);
  }

  return (
    <div style={{ marginTop: 16 }}>
      <Divider style={{ margin: '12px 0' }} />
      <Typography.Text strong>{t('forms.rules')}</Typography.Text>
      <Space direction="vertical" style={{ width: '100%', marginTop: 8 }}>
        {targetRules.length === 0 && (
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {t('forms.noRulesForField')}
          </Typography.Text>
        )}
        {targetRules.map((rule) =>
          isSupportedRuleType(rule.type) ? (
            <RuleRow key={rule.id} rule={rule} allFields={allFields} readOnly={readOnly} onChange={(next) => updateRule(rule.id, next)} onDelete={() => deleteRule(rule.id)} />
          ) : (
            <div key={rule.id} style={{ padding: 8, border: '1px solid #ffe58f', borderRadius: 4, background: '#fffbe6' }}>
              <Typography.Text type="warning" style={{ fontSize: 12 }}>
                {t('forms.unsupportedRuleTypeNotice1')} ("{rule.type}") {t('forms.unsupportedRuleTypeNotice2')}
              </Typography.Text>
            </div>
          ),
        )}
        {!readOnly && (
          <Button block size="small" icon={<PlusOutlined />} onClick={addRule}>
            {t('forms.addRule')}
          </Button>
        )}
      </Space>
    </div>
  );
}

function RuleRow({
  rule,
  allFields,
  readOnly,
  onChange,
  onDelete,
}: {
  rule: FormRule;
  allFields: DesignerField[];
  readOnly: boolean;
  onChange: (rule: FormRule) => void;
  onDelete: () => void;
}) {
  const { t } = useTranslation();
  const sourceOptions = allFields.filter((f) => f.key !== rule.target && f.key !== '');

  function setType(type: FormRuleType) {
    if (type === 'Calculated') {
      onChange({ id: rule.id, target: rule.target, type, formula: rule.formula ?? '' });
    } else {
      onChange({ id: rule.id, target: rule.target, type, condition: rule.condition ?? emptyLeaf() });
    }
  }

  return (
    <div style={{ padding: 8, border: '1px solid #d9d9d9', borderRadius: 4 }}>
      <Space style={{ width: '100%', justifyContent: 'space-between' }}>
        <Select
          size="small"
          style={{ width: 140 }}
          value={rule.type}
          disabled={readOnly}
          virtual={false}
          onChange={setType}
          options={SUPPORTED_RULE_TYPES.map((rt) => ({ label: t(RULE_TYPE_LABEL_KEYS[rt]), value: rt }))}
        />
        {!readOnly && <Button size="small" danger icon={<DeleteOutlined />} onClick={onDelete} aria-label={`${t('common.delete')} ${t('forms.rule')} ${rule.id}`} />}
      </Space>

      {rule.type === 'Calculated' ? (
        <FormulaEditor rule={rule} readOnly={readOnly} onChange={onChange} />
      ) : (
        <ConditionEditor condition={rule.condition ?? emptyLeaf()} sourceOptions={sourceOptions} readOnly={readOnly} onChange={(c) => onChange({ ...rule, condition: c })} />
      )}
    </div>
  );
}

function FormulaEditor({ rule, readOnly, onChange }: { rule: FormRule; readOnly: boolean; onChange: (rule: FormRule) => void }) {
  const { t } = useTranslation();
  const formula = rule.formula ?? '';
  const parsed = formula.trim() === '' ? null : parseFormula(formula);
  const invalid = formula.trim() !== '' && parsed === null;

  return (
    <div style={{ marginTop: 8 }}>
      <Input
        size="small"
        placeholder={t('forms.formulaPlaceholder')}
        value={formula}
        disabled={readOnly}
        status={invalid ? 'error' : undefined}
        onChange={(e) => onChange({ ...rule, formula: e.target.value })}
      />
      {invalid && (
        <Alert style={{ marginTop: 4 }} type="error" showIcon message={t('forms.invalidFormula')} />
      )}
    </div>
  );
}

function ConditionEditor({
  condition,
  sourceOptions,
  readOnly,
  onChange,
}: {
  condition: FormCondition;
  sourceOptions: DesignerField[];
  readOnly: boolean;
  onChange: (condition: FormCondition) => void;
}) {
  const { t } = useTranslation();
  if (condition.kind === 'compound') {
    return <CompoundEditor condition={condition} sourceOptions={sourceOptions} readOnly={readOnly} onChange={onChange} />;
  }

  return (
    <div style={{ marginTop: 8 }}>
      <LeafEditor leaf={condition} sourceOptions={sourceOptions} readOnly={readOnly} onChange={onChange} />
      {!readOnly && (
        <Button
          type="link"
          size="small"
          style={{ paddingLeft: 0 }}
          onClick={() => onChange({ kind: 'compound', operator: 'And', conditions: [condition, emptyLeaf()] })}
        >
          {t('forms.groupWithAndOr')}
        </Button>
      )}
    </div>
  );
}

function CompoundEditor({
  condition,
  sourceOptions,
  readOnly,
  onChange,
}: {
  condition: FormCompoundCondition;
  sourceOptions: DesignerField[];
  readOnly: boolean;
  onChange: (condition: FormCondition) => void;
}) {
  const { t } = useTranslation();
  const leaves = condition.conditions.filter((c): c is FormFieldCondition => c.kind === 'field');

  function updateLeaf(index: number, next: FormFieldCondition) {
    const copy = leaves.slice();
    copy[index] = next;
    onChange({ ...condition, conditions: copy });
  }

  function removeLeaf(index: number) {
    onChange({ ...condition, conditions: leaves.filter((_, i) => i !== index) });
  }

  function addLeaf() {
    onChange({ ...condition, conditions: [...leaves, emptyLeaf()] });
  }

  function setOperator(operator: FormLogicalOperator) {
    onChange({ ...condition, operator });
  }

  return (
    <div style={{ marginTop: 8 }}>
      <Select
        size="small"
        style={{ width: 90 }}
        value={condition.operator === 'Not' ? 'And' : condition.operator}
        disabled={readOnly}
        virtual={false}
        onChange={setOperator}
        options={[
          { label: 'AND', value: 'And' },
          { label: 'OR', value: 'Or' },
        ]}
      />
      <Space direction="vertical" style={{ width: '100%', marginTop: 4 }}>
        {leaves.map((leaf, index) => (
          <Space key={index} style={{ width: '100%' }} align="start">
            <LeafEditor leaf={leaf} sourceOptions={sourceOptions} readOnly={readOnly} onChange={(next) => updateLeaf(index, next as FormFieldCondition)} />
            {!readOnly && leaves.length > 1 && (
              <Button size="small" icon={<DeleteOutlined />} aria-label={`${t('forms.removeCondition')} ${index + 1}`} onClick={() => removeLeaf(index)} />
            )}
          </Space>
        ))}
        {!readOnly && (
          <Button size="small" icon={<PlusOutlined />} onClick={addLeaf}>
            {t('forms.addCondition')}
          </Button>
        )}
      </Space>
    </div>
  );
}

function LeafEditor({
  leaf,
  sourceOptions,
  readOnly,
  onChange,
}: {
  leaf: FormFieldCondition;
  sourceOptions: DesignerField[];
  readOnly: boolean;
  onChange: (condition: FormCondition) => void;
}) {
  const { t } = useTranslation();
  const sourceField = sourceOptions.find((f) => f.key === leaf.field);
  const operators = operatorsForFieldType((sourceField?.type ?? 'Text') as FormFieldType);
  const needsValue = leaf.operator !== 'IsEmpty' && leaf.operator !== 'IsNotEmpty';

  function setField(key: string) {
    const next = sourceOptions.find((f) => f.key === key);
    const allowed = operatorsForFieldType((next?.type ?? 'Text') as FormFieldType);
    onChange({ ...leaf, field: key, operator: allowed.includes(leaf.operator) ? leaf.operator : allowed[0] });
  }

  return (
    <Space wrap size={4}>
      <Select
        size="small"
        style={{ width: 130 }}
        placeholder={t('forms.field')}
        value={leaf.field || undefined}
        disabled={readOnly}
        virtual={false}
        onChange={setField}
        options={sourceOptions.map((f) => ({ label: f.label, value: f.key }))}
      />
      <Select
        size="small"
        style={{ width: 150 }}
        value={leaf.operator}
        disabled={readOnly}
        virtual={false}
        onChange={(op) => onChange({ ...leaf, operator: op })}
        options={operators.map((op) => ({ label: t(OPERATOR_LABEL_KEYS[op]), value: op }))}
      />
      {needsValue &&
        (sourceField?.type === 'Select' || sourceField?.type === 'Radio' ? (
          <Select
            size="small"
            style={{ width: 130 }}
            placeholder={t('forms.value')}
            value={leaf.value || undefined}
            disabled={readOnly}
            virtual={false}
            onChange={(v) => onChange({ ...leaf, value: v })}
            options={(sourceField.options ?? []).map((o) => ({ label: o.label, value: o.value }))}
          />
        ) : sourceField?.type === 'Checkbox' ? (
          <Select
            size="small"
            style={{ width: 100 }}
            placeholder={t('forms.value')}
            value={leaf.value || undefined}
            disabled={readOnly}
            virtual={false}
            onChange={(v) => onChange({ ...leaf, value: v })}
            options={[
              { label: 'true', value: 'true' },
              { label: 'false', value: 'false' },
            ]}
          />
        ) : (
          <Input size="small" style={{ width: 120 }} placeholder={t('forms.value')} value={leaf.value ?? ''} disabled={readOnly} onChange={(e) => onChange({ ...leaf, value: e.target.value })} />
        ))}
    </Space>
  );
}
