import type {
  FormCondition,
  FormConditionOperator,
  FormFieldCondition,
  FormFieldDefinition,
  FormFieldType,
  FormRule,
  FormSchema,
} from '../../../types/form';

// Type-aware operator sets (Phase 5.4.2 §5/§8) — mirrors
// BPM.Workflow.Validation.FormRuleValidator's AllowedOperators exactly, so the Rule Builder never
// even offers a combination the backend would reject; kept as one lookup here rather than
// duplicated inline in the UI component.
const TEXT_OPS: FormConditionOperator[] = ['Equals', 'NotEquals', 'Contains', 'NotContains', 'IsEmpty', 'IsNotEmpty'];
const NUMERIC_OPS: FormConditionOperator[] = ['Equals', 'NotEquals', 'GreaterThan', 'GreaterThanOrEqual', 'LessThan', 'LessThanOrEqual', 'IsEmpty', 'IsNotEmpty'];
const CHOICE_OPS: FormConditionOperator[] = ['Equals', 'NotEquals', 'IsEmpty', 'IsNotEmpty'];
const CHECKBOX_OPS: FormConditionOperator[] = ['Equals', 'NotEquals'];
const FILE_OPS: FormConditionOperator[] = ['IsEmpty', 'IsNotEmpty'];

export function operatorsForFieldType(type: FormFieldType): FormConditionOperator[] {
  switch (type) {
    case 'Text':
    case 'Textarea':
      return TEXT_OPS;
    case 'Number':
    case 'Currency':
    case 'Date':
    case 'DateTime':
      return NUMERIC_OPS;
    case 'Select':
    case 'Radio':
    case 'User':
    case 'Department':
      return CHOICE_OPS;
    case 'Checkbox':
      return CHECKBOX_OPS;
    case 'File':
      return FILE_OPS;
    default:
      return TEXT_OPS;
  }
}

// Frontend mirror of BPM.Workflow.Engine.FormRuleEngine/FormFormulaEvaluator (Phase 5.4.2 §11) —
// used ONLY for immediate Designer Preview / future live-form UX feedback. It is never
// authoritative: the backend re-evaluates every rule at Save/Submit time regardless of what this
// says (§12), so a bug or drift here can make the Preview wrong but can never let bad data through.
// Kept as a single shared implementation (not duplicated between Preview and a future runtime form
// renderer) per §17's own instruction.

export interface EvaluatedFieldState {
  visible: boolean;
  enabled: boolean;
  required: boolean;
}

export interface EvaluatedFormState {
  fields: Record<string, EvaluatedFieldState>;
  values: Record<string, unknown>;
  calculationErrors: Record<string, string>;
}

const NUMERIC_TYPES = new Set(['Number', 'Currency']);

function isEmptyValue(v: unknown): boolean {
  if (v === undefined || v === null) return true;
  if (typeof v === 'string') return v.length === 0;
  if (Array.isArray(v)) return v.length === 0;
  return false;
}

function toDecimal(v: unknown): number | null {
  if (typeof v === 'number') return Number.isFinite(v) ? v : null;
  if (typeof v === 'string') {
    const n = Number(v);
    return Number.isFinite(n) && v.trim() !== '' ? n : null;
  }
  return null;
}

function toDisplayString(v: unknown): string {
  if (v === undefined || v === null) return '';
  if (typeof v === 'boolean') return v ? 'true' : 'false';
  return String(v);
}

function evaluateLeaf(leaf: FormFieldCondition, fieldsByKey: Map<string, FormFieldDefinition>, values: Record<string, unknown>): boolean {
  const empty = isEmptyValue(values[leaf.field]);
  if (leaf.operator === 'IsEmpty') return empty;
  if (leaf.operator === 'IsNotEmpty') return !empty;
  if (empty) return false;

  const field = fieldsByKey.get(leaf.field);
  const isNumeric = !!field && NUMERIC_TYPES.has(field.type);
  const expectedNum = leaf.value != null ? Number(leaf.value) : NaN;

  if (isNumeric && Number.isFinite(expectedNum)) {
    const actual = toDecimal(values[leaf.field]);
    if (actual === null) return false;
    switch (leaf.operator) {
      case 'Equals': return actual === expectedNum;
      case 'NotEquals': return actual !== expectedNum;
      case 'GreaterThan': return actual > expectedNum;
      case 'GreaterThanOrEqual': return actual >= expectedNum;
      case 'LessThan': return actual < expectedNum;
      case 'LessThanOrEqual': return actual <= expectedNum;
      default: return false;
    }
  }

  const actual = toDisplayString(values[leaf.field]);
  const expected = leaf.value ?? '';
  switch (leaf.operator) {
    case 'Equals': return actual === expected;
    case 'NotEquals': return actual !== expected;
    case 'Contains': return actual.includes(expected);
    case 'NotContains': return !actual.includes(expected);
    default: return false;
  }
}

export function evaluateCondition(condition: FormCondition, fieldsByKey: Map<string, FormFieldDefinition>, values: Record<string, unknown>): boolean {
  if (condition.kind === 'field') {
    return evaluateLeaf(condition, fieldsByKey, values);
  }
  switch (condition.operator) {
    case 'And': return condition.conditions.every((c) => evaluateCondition(c, fieldsByKey, values));
    case 'Or': return condition.conditions.some((c) => evaluateCondition(c, fieldsByKey, values));
    case 'Not': return condition.conditions.length === 1 && !evaluateCondition(condition.conditions[0], fieldsByKey, values);
    default: return false;
  }
}

function evaluateRuleGroup(rules: FormRule[], fieldKey: string, type: FormRule['type'], fieldsByKey: Map<string, FormFieldDefinition>, values: Record<string, unknown>, defaultWhenNoRules: boolean): boolean {
  const matching = rules.filter((r) => r.type === type && r.target === fieldKey);
  if (matching.length === 0) return defaultWhenNoRules;
  return matching.some((r) => r.condition && evaluateCondition(r.condition, fieldsByKey, values));
}

// --- Safe formula evaluator (numeric literals, field refs, + - * / and parens only) ---

type FormulaNode =
  | { kind: 'number'; value: number }
  | { kind: 'field'; field: string }
  | { kind: 'unary'; operand: FormulaNode }
  | { kind: 'binary'; op: '+' | '-' | '*' | '/'; left: FormulaNode; right: FormulaNode };

export function parseFormula(formula: string): FormulaNode | null {
  type Token = { kind: 'num' | 'ident' | '+' | '-' | '*' | '/' | '(' | ')' | 'end'; text: string };
  const tokens: Token[] = [];
  let i = 0;
  while (i < formula.length) {
    const c = formula[i];
    if (/\s/.test(c)) { i++; continue; }
    if (/[0-9.]/.test(c)) {
      let start = i;
      let sawDot = false;
      while (i < formula.length && (/[0-9]/.test(formula[i]) || (formula[i] === '.' && !sawDot))) {
        if (formula[i] === '.') sawDot = true;
        i++;
      }
      tokens.push({ kind: 'num', text: formula.slice(start, i) });
      continue;
    }
    if (/[A-Za-z_]/.test(c)) {
      let start = i;
      while (i < formula.length && /[A-Za-z0-9_]/.test(formula[i])) i++;
      tokens.push({ kind: 'ident', text: formula.slice(start, i) });
      continue;
    }
    if ('+-*/()'.includes(c)) {
      tokens.push({ kind: c as Token['kind'], text: c });
      i++;
      continue;
    }
    return null; // unsupported character
  }
  tokens.push({ kind: 'end', text: '' });

  let pos = 0;
  const current = () => tokens[pos];

  function parseFactor(): FormulaNode | null {
    if (current().kind === '-') {
      pos++;
      const operand = parseFactor();
      return operand ? { kind: 'unary', operand } : null;
    }
    if (current().kind === 'num') {
      const n = Number(current().text);
      pos++;
      return Number.isFinite(n) ? { kind: 'number', value: n } : null;
    }
    if (current().kind === 'ident') {
      const field = current().text;
      pos++;
      return { kind: 'field', field };
    }
    if (current().kind === '(') {
      pos++;
      const inner = parseExpression();
      if (!inner || current().kind !== ')') return null;
      pos++;
      return inner;
    }
    return null;
  }

  function parseTerm(): FormulaNode | null {
    let left = parseFactor();
    if (!left) return null;
    while (current().kind === '*' || current().kind === '/') {
      const op = current().kind as '*' | '/';
      pos++;
      const right = parseFactor();
      if (!right) return null;
      left = { kind: 'binary', op, left, right };
    }
    return left;
  }

  function parseExpression(): FormulaNode | null {
    let left = parseTerm();
    if (!left) return null;
    while (current().kind === '+' || current().kind === '-') {
      const op = current().kind as '+' | '-';
      pos++;
      const right = parseTerm();
      if (!right) return null;
      left = { kind: 'binary', op, left, right };
    }
    return left;
  }

  const result = parseExpression();
  if (!result || current().kind !== 'end') return null;
  return result;
}

export function referencedFields(node: FormulaNode): string[] {
  const out = new Set<string>();
  function walk(n: FormulaNode) {
    if (n.kind === 'field') out.add(n.field);
    else if (n.kind === 'unary') walk(n.operand);
    else if (n.kind === 'binary') { walk(n.left); walk(n.right); }
  }
  walk(node);
  return [...out];
}

export interface FormulaEvalResult {
  value: number | null;
  error: string | null;
}

export function evaluateFormula(node: FormulaNode, values: Record<string, number | null>): FormulaEvalResult {
  switch (node.kind) {
    case 'number':
      return { value: node.value, error: null };
    case 'field':
      return { value: values[node.field] ?? null, error: null };
    case 'unary': {
      const r = evaluateFormula(node.operand, values);
      if (r.error) return r;
      return { value: r.value === null ? null : -r.value, error: null };
    }
    case 'binary': {
      const l = evaluateFormula(node.left, values);
      if (l.error) return l;
      const r = evaluateFormula(node.right, values);
      if (r.error) return r;
      if (l.value === null || r.value === null) return { value: null, error: null };
      switch (node.op) {
        case '+': return { value: l.value + r.value, error: null };
        case '-': return { value: l.value - r.value, error: null };
        case '*': return { value: l.value * r.value, error: null };
        case '/':
          return r.value === 0 ? { value: null, error: 'DIVISION_BY_ZERO' } : { value: l.value / r.value, error: null };
      }
    }
  }
}

// --- Full form evaluation (defaults are applied server-side/at-creation only, not here — see
// FormEngine's BuildInitialDataJson; the Designer Preview has no "instance" to seed defaults for,
// it just renders the schema, so it isn't in scope here). ---

// Used by the Designer when a field is deleted, to also drop any rule that would otherwise be
// left pointing at a field key that no longer exists (a dangling reference the backend would
// reject at Validate/Publish time anyway, but silently, well after the deletion that caused it).
export function ruleReferencesField(rule: FormRule, fieldKey: string): boolean {
  if (rule.condition && conditionReferencesField(rule.condition, fieldKey)) return true;
  if (rule.formula) {
    const node = parseFormula(rule.formula);
    if (node && referencedFields(node).includes(fieldKey)) return true;
  }
  return false;
}

function conditionReferencesField(condition: FormCondition, fieldKey: string): boolean {
  if (condition.kind === 'field') return condition.field === fieldKey;
  return condition.conditions.some((c) => conditionReferencesField(c, fieldKey));
}

export function evaluateForm(schema: FormSchema, values: Record<string, unknown>): EvaluatedFormState {
  const fieldsByKey = new Map(schema.fields.map((f) => [f.key, f]));
  const rules = (schema.rules ?? []).filter((r) => r.type !== 'Unknown');
  const effectiveValues: Record<string, unknown> = { ...values };
  const calculationErrors: Record<string, string> = {};

  // Calculated fields, evaluated in dependency order.
  const calcRules = rules.filter((r) => r.type === 'Calculated' && fieldsByKey.has(r.target));
  const parsed = new Map<string, FormulaNode>();
  const dependsOn = new Map<string, Set<string>>();
  for (const rule of calcRules) {
    if (!rule.formula) continue;
    const node = parseFormula(rule.formula);
    if (!node) continue;
    parsed.set(rule.target, node);
    const refs = referencedFields(node).filter((f) => calcRules.some((r) => r.target === f));
    dependsOn.set(rule.target, new Set(refs));
  }
  const order: string[] = [];
  const visited = new Set<string>();
  const visiting = new Set<string>();
  function visit(node: string) {
    if (visited.has(node) || visiting.has(node)) return;
    visiting.add(node);
    for (const dep of dependsOn.get(node) ?? []) visit(dep);
    visiting.delete(node);
    visited.add(node);
    order.push(node);
  }
  for (const target of parsed.keys()) visit(target);

  for (const target of order) {
    const node = parsed.get(target)!;
    const lookup: Record<string, number | null> = {};
    for (const ref of referencedFields(node)) lookup[ref] = toDecimal(effectiveValues[ref]);
    const result = evaluateFormula(node, lookup);
    if (result.error) {
      calculationErrors[target] = result.error;
      continue;
    }
    if (result.value !== null) effectiveValues[target] = result.value;
  }

  const fields: Record<string, EvaluatedFieldState> = {};
  for (const field of schema.fields) {
    let visible = evaluateRuleGroup(rules, field.key, 'Visibility', fieldsByKey, effectiveValues, true);
    if (field.visibility) {
      const when = field.visibility.when;
      const actual = toDisplayString(effectiveValues[when.field]);
      const matches = actual === when.value;
      const legacyVisible = when.operator === 'Equals' ? matches : !matches;
      visible = visible && legacyVisible;
    }
    const enabled = evaluateRuleGroup(rules, field.key, 'Enabled', fieldsByKey, effectiveValues, true);
    const conditionallyRequired = evaluateRuleGroup(rules, field.key, 'Required', fieldsByKey, effectiveValues, false);
    fields[field.key] = { visible, enabled, required: !!field.required || conditionallyRequired };
  }

  return { fields, values: effectiveValues, calculationErrors };
}
