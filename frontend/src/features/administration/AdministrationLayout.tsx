import { Tabs } from 'antd';
import { Outlet, useLocation, useNavigate } from 'react-router-dom';
import { useTranslation } from '../../i18n/LanguageContext';

// The Administration IA (Phase 5.5.2 §2): Users/Departments/Roles/Audit Logs sub-nav under one
// shell, matching the shell/nav conventions the rest of the app already uses (AppLayout's Sider
// for top-level areas, this Tabs bar for the one area with sub-pages) rather than inventing a
// second navigation pattern.
export function AdministrationLayout() {
  const navigate = useNavigate();
  const location = useLocation();
  const { t } = useTranslation();

  const tabs = [
    { key: 'users', label: t('administration.users') },
    { key: 'organizations', label: t('administration.organizations') },
    { key: 'departments', label: t('administration.departments') },
    { key: 'roles', label: t('administration.roles') },
    { key: 'sla-policies', label: t('administration.slaPolicies') },
    { key: 'audit', label: t('administration.audit') },
    { key: 'operational-health', label: t('administration.operationalHealth') },
  ];

  const activeKey = tabs.find((t) => location.pathname.startsWith(`/administration/${t.key}`))?.key ?? 'users';

  return (
    <div>
      <Tabs activeKey={activeKey} items={tabs} onChange={(key) => navigate(`/administration/${key}`)} />
      <Outlet />
    </div>
  );
}
