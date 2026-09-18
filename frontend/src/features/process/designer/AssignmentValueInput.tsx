import { Select } from 'antd';
import { useDepartments } from '../../../hooks/useDepartments';
import { useUsersById } from '../../../hooks/useUsers';
import { useRoles } from '../../../hooks/useRoles';
import type { WorkflowAssignmentType } from '../../../types/process';
import { useTranslation } from '../../../i18n/LanguageContext';

interface AssignmentValueInputProps {
  type: WorkflowAssignmentType;
  value: string;
  disabled: boolean;
  onChange: (value: string) => void;
}

// The "value" half of a WorkflowAssignment — always a real backend-issued option, never free
// text, per Phase 5.3.2 §3 ("Do not hard-code Finance/Legal/IT/specific users/departments — the
// UI should retrieve available options from backend APIs"). Which list backs the dropdown depends
// entirely on `type`, per AssignmentResolver's actual value semantics (BPM.Workflow.Engine):
//   Role             -> role *name*        (GET /api/roles)
//   User              -> user id            (GET /api/users)
//   Department        -> department id      (GET /api/departments)
//   DepartmentManager -> department id too — it resolves to *that department's* ManagerUserId,
//                        not a directly-picked user (AssignmentResolver's DepartmentManager case)
//   ProcessInitiator  -> no value at all (always resolves to whoever started the process)
export function AssignmentValueInput({ type, value, disabled, onChange }: AssignmentValueInputProps) {
  const { roles, isLoading: rolesLoading } = useRoles();
  const { users, isLoading: usersLoading } = useUsersById();
  const { departments, isLoading: departmentsLoading } = useDepartments();
  const { t } = useTranslation();

  if (type === 'ProcessInitiator') {
    return (
      <Select
        disabled
        style={{ width: '100%' }}
        value="not needed"
        options={[{ label: t('processDesigner.notNeeded'), value: 'not needed' }]}
      />
    );
  }

  if (type === 'Role') {
    return (
      <SearchableSelect
        value={value}
        disabled={disabled}
        loading={rolesLoading}
        placeholder={t('processDesigner.selectRole')}
        options={roles.map((r) => ({ label: r.name, value: r.name }))}
        onChange={onChange}
        notFoundLabel={t('processDesigner.notFound')}
      />
    );
  }

  if (type === 'User') {
    return (
      <SearchableSelect
        value={value}
        disabled={disabled}
        loading={usersLoading}
        placeholder={t('processDesigner.selectUser')}
        options={users.map((u) => ({ label: u.displayName, value: u.id }))}
        onChange={onChange}
        notFoundLabel={t('processDesigner.notFound')}
      />
    );
  }

  // Department and DepartmentManager both pick a department — DepartmentManager just resolves
  // differently on the backend (see this component's doc comment above).
  return (
    <SearchableSelect
      value={value}
      disabled={disabled}
      loading={departmentsLoading}
      placeholder={t('processDesigner.selectDepartment')}
      options={departments.map((d) => ({ label: d.name, value: d.id }))}
      onChange={onChange}
      notFoundLabel={t('processDesigner.notFound')}
    />
  );
}

// A previously-saved value that no longer matches any current option (the role was renamed, the
// user deactivated, the department deleted) is kept visible and selected — labeled distinctly —
// rather than silently blanked out, so opening the Designer never quietly discards a real
// configuration.
function SearchableSelect({
  value,
  disabled,
  loading,
  placeholder,
  options,
  onChange,
  notFoundLabel,
}: {
  value: string;
  disabled: boolean;
  loading: boolean;
  placeholder: string;
  options: { label: string; value: string }[];
  onChange: (value: string) => void;
  notFoundLabel: string;
}) {
  const hasValue = value !== '';
  const knownOption = options.some((o) => o.value === value);
  const effectiveOptions = hasValue && !knownOption ? [...options, { label: `${value} (${notFoundLabel})`, value }] : options;

  return (
    <Select
      showSearch
      allowClear
      virtual={false}
      style={{ width: '100%' }}
      value={hasValue ? value : undefined}
      disabled={disabled}
      loading={loading}
      placeholder={placeholder}
      options={effectiveOptions}
      filterOption={(input, option) => (option?.label ?? '').toLowerCase().includes(input.toLowerCase())}
      onChange={(next) => onChange(next ?? '')}
    />
  );
}
