import { useMutation } from '@tanstack/react-query';
import { Alert, Button, Card, Form, Input, Typography } from 'antd';
import { useLocation, useNavigate } from 'react-router-dom';
import { login } from '../services/authService';
import { useAuthStore } from '../stores/authStore';
import type { LoginRequest } from '../types/auth';
import { toApiError } from '../services/apiClient';
import { useTranslation } from '../i18n/LanguageContext';

export function LoginPage() {
  const navigate = useNavigate();
  const location = useLocation();
  const setSession = useAuthStore((state) => state.setSession);
  const { t } = useTranslation();

  const mutation = useMutation({
    mutationFn: (values: LoginRequest) => login(values),
    onSuccess: (data) => {
      setSession(data.accessToken, {
        userId: data.userId,
        displayName: data.displayName,
        roles: data.roles,
      });
      const redirectTo = (location.state as { from?: { pathname?: string } })?.from?.pathname ?? '/processes';
      navigate(redirectTo, { replace: true });
    },
  });

  return (
    <div
      style={{
        minHeight: '100vh',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        background: '#f0f2f5',
      }}
    >
      <Card style={{ width: 360 }}>
        <Typography.Title level={3} style={{ textAlign: 'center' }}>
          {t('login.title')}
        </Typography.Title>
        {mutation.isError && (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 16 }}
            message={toApiError(mutation.error).message || t('login.failed')}
          />
        )}
        <Form layout="vertical" onFinish={(values: LoginRequest) => mutation.mutate(values)}>
          <Form.Item name="username" label={t('login.username')} rules={[{ required: true }]}>
            <Input autoFocus autoComplete="username" />
          </Form.Item>
          <Form.Item name="password" label={t('login.password')} rules={[{ required: true }]}>
            <Input.Password autoComplete="current-password" />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" block loading={mutation.isPending}>
              {t('login.login')}
            </Button>
          </Form.Item>
        </Form>
      </Card>
    </div>
  );
}
