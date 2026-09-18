import type { ReactNode } from 'react';
import { Empty, Spin } from 'antd';
import { ApiErrorAlert } from './ApiErrorAlert';
import { useTranslation } from '../i18n/LanguageContext';

interface QueryStateViewProps {
  isLoading: boolean;
  error: unknown;
  isEmpty?: boolean;
  emptyDescription?: string;
  children: ReactNode;
}

// Every Process Management page routes its loading/error/empty handling through this so the four
// states (loading, error, empty, success) look the same everywhere (frontend spec §14).
export function QueryStateView({ isLoading, error, isEmpty, emptyDescription, children }: QueryStateViewProps) {
  const { t } = useTranslation();

  if (isLoading) {
    return (
      <div style={{ textAlign: 'center', padding: '48px 0' }}>
        <Spin size="large" />
      </div>
    );
  }

  if (error) {
    return <ApiErrorAlert error={error} />;
  }

  if (isEmpty) {
    return <Empty description={emptyDescription ?? t('common.noData')} style={{ padding: '48px 0' }} />;
  }

  return <>{children}</>;
}
