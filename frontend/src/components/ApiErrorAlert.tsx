import { Alert, Typography } from 'antd';
import { toApiError } from '../services/apiClient';
import { useTranslation } from '../i18n/LanguageContext';

// Shared rendering of the backend's uniform {code, message, errors?, traceId} error shape
// (ErrorHandlingMiddleware) — every Process Management page uses this instead of inventing its
// own error text so 401/403/404/409/validation failures all look and read the same way.
//
// Phase 13 (Part 十九): the API contract itself is never localized — {code, message, traceId}
// stays exactly as the backend sends it. The frontend looks up a localized message by `code`
// (LanguageContext.translateErrorCode) and falls back to the backend's own English `message` when
// no translation exists for that code, so an error this dictionary doesn't yet know about still
// reads as real text, never a blank or a raw key.
export function ApiErrorAlert({ error, title }: { error: unknown; title?: string }) {
  const { t, translateErrorCode } = useTranslation();
  const apiError = toApiError(error);
  const localizedMessage = translateErrorCode(apiError.code) ?? apiError.message;

  return (
    <Alert
      type="error"
      showIcon
      message={title ?? t('common.somethingWentWrong')}
      description={
        <div>
          <Typography.Paragraph style={{ marginBottom: 4 }}>{localizedMessage}</Typography.Paragraph>
          {apiError.errors && apiError.errors.length > 0 && (
            <ul style={{ margin: 0, paddingInlineStart: 20 }}>
              {apiError.errors.map((e, i) => (
                <li key={i}>
                  <Typography.Text code>{e.code}</Typography.Text> {e.message}
                </li>
              ))}
            </ul>
          )}
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {apiError.code}
            {apiError.traceId ? ` · trace ${apiError.traceId}` : ''}
          </Typography.Text>
        </div>
      }
    />
  );
}
