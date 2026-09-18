import type { ReactNode } from 'react';
import { Result } from 'antd';
import { useAuthStore } from '../stores/authStore';
import { useTranslation } from '../i18n/LanguageContext';

// Phase 5.5.2 §3: "frontend route protection required" for Administration, hiding the menu is
// not sufficient on its own — but this is UX only. The backend remains the authoritative gate:
// every Administration-mutating endpoint (and RolesController/AuditLogsController's GETs) is
// independently [Authorize(Roles = "Administrator")] regardless of what this component renders,
// so a normal user calling the API directly is still rejected even if this check were bypassed.
export function AdminRoute({ children }: { children: ReactNode }) {
  const roles = useAuthStore((state) => state.user?.roles ?? []);
  const { t } = useTranslation();

  if (!roles.includes('Administrator')) {
    return (
      <Result
        status="403"
        title="403"
        subTitle={t('administration.accessDeniedMessage')}
      />
    );
  }

  return <>{children}</>;
}
