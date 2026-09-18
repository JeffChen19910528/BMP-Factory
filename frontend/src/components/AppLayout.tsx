import {
  ApartmentOutlined,
  AuditOutlined,
  CheckSquareOutlined,
  DashboardOutlined,
  DeploymentUnitOutlined,
  FormOutlined,
  GlobalOutlined,
  KeyOutlined,
  LogoutOutlined,
  BarChartOutlined,
  LineChartOutlined,
  SettingOutlined,
} from '@ant-design/icons';
import { Avatar, Dropdown, Layout, Menu, Space, Typography } from 'antd';
import type { MenuProps } from 'antd';
import { useState } from 'react';
import { Outlet, useLocation, useNavigate } from 'react-router-dom';
import { NotificationBell } from '../features/notification/NotificationBell';
import { useAuthStore } from '../stores/authStore';
import { useTranslation, type Language } from '../i18n/LanguageContext';
import { ChangePasswordModal } from './ChangePasswordModal';

const { Header, Sider, Content } = Layout;

export function AppLayout() {
  const navigate = useNavigate();
  const location = useLocation();
  const user = useAuthStore((state) => state.user);
  const clearSession = useAuthStore((state) => state.clearSession);
  const [changePasswordOpen, setChangePasswordOpen] = useState(false);
  const { t, language, setLanguage } = useTranslation();

  const baseNavItems: Exclude<MenuProps['items'], undefined> = [
    { key: '/dashboard', icon: <DashboardOutlined />, label: t('nav.dashboard') },
    { key: '/processes', icon: <ApartmentOutlined />, label: t('nav.processDefinitions') },
    { key: '/instances', icon: <DeploymentUnitOutlined />, label: t('nav.processInstances') },
    { key: '/tasks', icon: <CheckSquareOutlined />, label: t('nav.myTasks') },
    { key: '/approvals', icon: <AuditOutlined />, label: t('nav.approvals') },
    { key: '/forms', icon: <FormOutlined />, label: t('nav.formDefinitions') },
    { key: '/reports', icon: <BarChartOutlined />, label: t('nav.reporting') },
    { key: '/analytics', icon: <LineChartOutlined />, label: t('nav.analytics') },
  ];

  const administrationNavItem: Exclude<MenuProps['items'], undefined>[number] = {
    key: '/administration',
    icon: <SettingOutlined />,
    label: t('nav.administration'),
  };

  // Phase 5.5.2 §3/§21: only shown when the existing JWT roles claim reliably says so — hiding
  // the menu is a UX nicety, not the authorization boundary (see AdminRoute for the actual gate,
  // and every Administration-mutating backend endpoint's own [Authorize(Roles="Administrator")]).
  const navItems: MenuProps['items'] = user?.roles.includes('Administrator')
    ? [...baseNavItems, administrationNavItem]
    : baseNavItems;

  const selectedKey =
    navItems?.map((item) => item!.key as string).find((key) => location.pathname.startsWith(key)) ??
    '/dashboard';

  function handleLogout() {
    clearSession();
    navigate('/login', { replace: true });
  }

  const userMenu: MenuProps['items'] = [
    { key: 'changePassword', icon: <KeyOutlined />, label: t('common.changePassword'), onClick: () => setChangePasswordOpen(true) },
    { key: 'logout', icon: <LogoutOutlined />, label: t('common.logout'), onClick: handleLogout },
  ];

  // Phase 13 — the language switch lives in the header, next to the existing user menu (Part
  // 十六: "不要重新設計整個 Header" / "在現有 Header... 中找最自然的位置"). Switching updates every
  // translated string on the current page immediately (React context, no reload) and never calls
  // the backend — it is a pure client-side preference.
  const languageMenu: MenuProps['items'] = (['zh-TW', 'en-US'] as Language[]).map((code) => ({
    key: code,
    label: code === 'zh-TW' ? t('language.zhTW') : t('language.enUS'),
    onClick: () => setLanguage(code),
  }));

  return (
    <Layout style={{ minHeight: '100vh' }}>
      <Sider breakpoint="lg" collapsedWidth="0">
        <div
          style={{
            height: 48,
            margin: 16,
            color: '#fff',
            fontWeight: 600,
            fontSize: 16,
          }}
        >
          {t('nav.title')}
        </div>
        <Menu
          theme="dark"
          mode="inline"
          selectedKeys={[selectedKey]}
          items={navItems}
          onClick={({ key }) => navigate(key)}
        />
      </Sider>
      <Layout>
        <Header style={{ background: '#fff', padding: '0 16px', display: 'flex', justifyContent: 'flex-end', alignItems: 'center', gap: 16 }}>
          <Dropdown menu={{ items: languageMenu, selectedKeys: [language] }} placement="bottomRight">
            <Space style={{ cursor: 'pointer' }} aria-label={t('language.label')}>
              <GlobalOutlined />
              <Typography.Text>{language === 'zh-TW' ? t('language.zhTW') : t('language.enUS')}</Typography.Text>
            </Space>
          </Dropdown>
          <NotificationBell />
          <Dropdown menu={{ items: userMenu }} placement="bottomRight">
            <Space style={{ cursor: 'pointer' }}>
              <Avatar>{user?.displayName?.charAt(0).toUpperCase() ?? '?'}</Avatar>
              <Typography.Text>{user?.displayName}</Typography.Text>
            </Space>
          </Dropdown>
        </Header>
        <Content style={{ margin: 24 }}>
          <Outlet />
        </Content>
      </Layout>

      <ChangePasswordModal open={changePasswordOpen} onClose={() => setChangePasswordOpen(false)} />
    </Layout>
  );
}
