import { ReloadOutlined } from '@ant-design/icons';
import { Button, Card, Col, Descriptions, Row, Statistic, Tag, Typography } from 'antd';
import { QueryStateView } from '../../components/QueryStateView';
import type { ComponentHealth, ComponentStatus } from '../../types/operationalHealth';
import { useTranslation } from '../../i18n/LanguageContext';
import { useOperationalHealth } from './hooks';

const statusColors: Record<ComponentStatus, string> = {
  Healthy: 'green',
  Unhealthy: 'red',
  NotInstrumented: 'default',
};

// Phase 9 Part 30-37 — a small, read-only Administrator diagnostic view assembled entirely from
// existing infrastructure (the health-check registration, NotificationDelivery rows, and a
// handful of non-secret configuration values) — never a new monitoring/APM subsystem. Every
// status this page renders comes directly from the backend; it never guesses or upgrades a
// "NotInstrumented" signal into "Healthy" for a nicer-looking page.
export function OperationalHealthPage() {
  const { t } = useTranslation();
  const query = useOperationalHealth();
  const data = query.data;

  function componentCard(component: ComponentHealth) {
    return (
      <Col xs={24} sm={12} md={6} key={component.name}>
        <Card size="small">
          <Typography.Text strong>{component.name}</Typography.Text>
          <div style={{ marginTop: 8 }}>
            <Tag color={statusColors[component.status]}>{component.status}</Tag>
          </div>
          {component.detail && (
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {component.detail}
            </Typography.Text>
          )}
        </Card>
      </Col>
    );
  }

  return (
    <div>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', margin: '16px 0' }}>
        <Typography.Title level={4} style={{ margin: 0 }}>
          {t('administration.operationalHealth')}
        </Typography.Title>
        <Button icon={<ReloadOutlined />} onClick={() => query.refetch()} loading={query.isFetching}>
          {t('common.refresh')}
        </Button>
      </div>

      <QueryStateView isLoading={query.isLoading} error={query.error}>
        {data && (
          <div>
            <Row gutter={[16, 16]} style={{ marginBottom: 16 }}>
              {componentCard(data.api)}
              {componentCard(data.database)}
              {componentCard(data.objectStorage)}
              {componentCard(data.cache)}
            </Row>

            <Card title={t('administration.notificationDelivery')} style={{ marginBottom: 16 }}>
              <Row gutter={16}>
                <Col span={6}>
                  <Statistic title={t('administration.pending')} value={data.notificationDelivery.pendingCount} />
                </Col>
                <Col span={6}>
                  <Statistic title={t('administration.processing')} value={data.notificationDelivery.processingCount} />
                </Col>
                <Col span={6}>
                  <Statistic title={t('administration.failed')} value={data.notificationDelivery.failedCount} valueStyle={data.notificationDelivery.failedCount > 0 ? { color: '#cf1322' } : undefined} />
                </Col>
                <Col span={6}>
                  <Statistic title={t('administration.sentLast24Hours')} value={data.notificationDelivery.sentLast24Hours} />
                </Col>
              </Row>
            </Card>

            <Card title={t('administration.diagnosticConfiguration')}>
              <Typography.Paragraph type="secondary">
                {t('administration.diagnosticConfigDescription')}
              </Typography.Paragraph>
              <Descriptions column={2} bordered size="small">
                <Descriptions.Item label={t('administration.emailEnabled')}>{data.configuration.email.enabled ? t('common.yes') : t('common.no')}</Descriptions.Item>
                <Descriptions.Item label={t('administration.emailProvider')}>{data.configuration.email.provider || '—'}</Descriptions.Item>
                <Descriptions.Item label={t('administration.slaSchedulerEnabled')}>{data.configuration.slaScheduler.enabled ? t('common.yes') : t('common.no')}</Descriptions.Item>
                <Descriptions.Item label={t('administration.slaSchedulerPollInterval')}>{data.configuration.slaScheduler.pollIntervalSeconds}s</Descriptions.Item>
                <Descriptions.Item label={t('administration.slaSchedulerBatchSize')}>{data.configuration.slaScheduler.batchSize}</Descriptions.Item>
                <Descriptions.Item label={t('administration.dashboardDueSoonHours')}>{data.configuration.dashboard.dueSoonHours}h</Descriptions.Item>
              </Descriptions>
              <Typography.Paragraph type="secondary" style={{ marginTop: 12, fontSize: 12 }}>
                {t('administration.slaSchedulerHeartbeatNote')}
              </Typography.Paragraph>
            </Card>
          </div>
        )}
      </QueryStateView>
    </div>
  );
}
