import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ConfigProvider } from 'antd';
import zhTW from 'antd/locale/zh_TW';
import enUS from 'antd/locale/en_US';
import { Navigate, Route, BrowserRouter, Routes } from 'react-router-dom';
import { LanguageProvider, useTranslation } from './i18n/LanguageContext';
import { AdminRoute } from './components/AdminRoute';
import { AppLayout } from './components/AppLayout';
import { ProtectedRoute } from './components/ProtectedRoute';
import { AdministrationLayout } from './features/administration/AdministrationLayout';
import { AuditLogsPage } from './features/administration/AuditLogsPage';
import { DepartmentsPage } from './features/administration/DepartmentsPage';
import { OperationalHealthPage } from './features/administration/OperationalHealthPage';
import { OrganizationsPage } from './features/administration/OrganizationsPage';
import { RolesPage } from './features/administration/RolesPage';
import { SlaPoliciesPage } from './features/administration/SlaPoliciesPage';
import { UsersPage } from './features/administration/UsersPage';
import { ApprovalsPage } from './features/approval/ApprovalsPage';
import { DashboardPage } from './features/dashboard/DashboardPage';
import { FormDefinitionsPage } from './features/form/FormDefinitionsPage';
import { FormDetailPage } from './features/form/FormDetailPage';
import { FormVersionEditorPage } from './features/form/FormVersionEditorPage';
import { ProcessDefinitionsPage } from './features/process/ProcessDefinitionsPage';
import { ProcessDetailPage } from './features/process/ProcessDetailPage';
import { ProcessVersionEditorPage } from './features/process/ProcessVersionEditorPage';
import { TaskDetailPage } from './features/task/TaskDetailPage';
import { TasksPage } from './features/task/TasksPage';
import { ProcessInstanceDetailPage } from './features/processMonitoring/ProcessInstanceDetailPage';
import { ProcessMonitoringPage } from './features/processMonitoring/ProcessMonitoringPage';
import { ReportingPage } from './features/reports/ReportingPage';
import { AnalyticsPage } from './features/analytics/AnalyticsPage';
import { LoginPage } from './pages/LoginPage';

// Phase 12 — conservative global defaults for a read-heavy, non-realtime app (Discovery found
// this had never been set: staleTime 0 + refetchOnWindowFocus true meant every tab-focus
// re-fetched every visible query, even Reporting/Analytics data that only changes when someone
// takes an action elsewhere). 30s staleTime avoids refetch storms on ordinary navigation/focus
// without masking real changes for more than a few seconds. Any query needing fresher or
// differently-tuned data (e.g. useUnreadCount's own 30s refetchInterval poll) sets its own
// per-query options, which always take precedence over these defaults.
const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      refetchOnWindowFocus: false,
      refetchOnReconnect: true,
      retry: 1,
    },
  },
});

// Phase 13 — Bilingual UI. AntD's own component locale (DatePicker/Table/Pagination/Form
// validation text etc.) must track the selected language too, not just our own dictionary — see
// Part 二十. This inner component exists only so it can call useTranslation() from inside
// LanguageProvider before ConfigProvider is rendered.
function AppShell() {
  const { language } = useTranslation();
  return (
    <ConfigProvider theme={{ token: { colorPrimary: '#1677ff' } }} locale={language === 'zh-TW' ? zhTW : enUS}>
      <BrowserRouter>
        <AppRoutes />
      </BrowserRouter>
    </ConfigProvider>
  );
}

function AppRoutes() {
  return (
    <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route
              element={
                <ProtectedRoute>
                  <AppLayout />
                </ProtectedRoute>
              }
            >
              <Route path="/dashboard" element={<DashboardPage />} />
              <Route path="/processes" element={<ProcessDefinitionsPage />} />
              <Route path="/processes/:id" element={<ProcessDetailPage />} />
              <Route path="/processes/:id/versions/:versionId" element={<ProcessVersionEditorPage />} />
              <Route path="/instances" element={<ProcessMonitoringPage />} />
              <Route path="/instances/:processInstanceId" element={<ProcessInstanceDetailPage />} />
              <Route path="/tasks" element={<TasksPage />} />
              <Route path="/tasks/:id" element={<TaskDetailPage />} />
              <Route path="/approvals" element={<ApprovalsPage />} />
              <Route path="/reports" element={<ReportingPage />} />
              <Route path="/analytics" element={<AnalyticsPage />} />
              <Route path="/forms" element={<FormDefinitionsPage />} />
              <Route path="/forms/:id" element={<FormDetailPage />} />
              <Route path="/forms/:id/versions/:versionId" element={<FormVersionEditorPage />} />
              <Route
                path="/administration"
                element={
                  <AdminRoute>
                    <AdministrationLayout />
                  </AdminRoute>
                }
              >
                <Route index element={<Navigate to="/administration/users" replace />} />
                <Route path="users" element={<UsersPage />} />
                <Route path="organizations" element={<OrganizationsPage />} />
                <Route path="departments" element={<DepartmentsPage />} />
                <Route path="roles" element={<RolesPage />} />
                <Route path="sla-policies" element={<SlaPoliciesPage />} />
                <Route path="audit" element={<AuditLogsPage />} />
                <Route path="operational-health" element={<OperationalHealthPage />} />
              </Route>
              <Route path="/" element={<Navigate to="/dashboard" replace />} />
            </Route>
            <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}

export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <LanguageProvider>
        <AppShell />
      </LanguageProvider>
    </QueryClientProvider>
  );
}
